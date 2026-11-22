using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using BaBaBooeyStrap.App.DependencyInjection;
using BaBaBooeyStrap.App.Services.Pipeline;
using BaBaBooeyStrap.Bootstrapper.Deployment.Models;
using BaBaBooeyStrap.Core;
using BaBaBooeyStrap.Core.Launch;
using BaBaBooeyStrap.Core.Logging;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Models.Enums;
using BaBaBooeyStrap.Core.Roblox;
using BaBaBooeyStrap.Core.Storage;
using BaBaBooeyStrap.Integrations.Custom;
using BaBaBooeyStrap.Integrations.Discord;
using BaBaBooeyStrap.Integrations.Matchmaking;
using BaBaBooeyStrap.Integrations.Watcher;
using BaBaBooeyStrap.Integrations.Window;
using BaBaBooeyStrap.MultiInstance;
using BaBaBooeyStrap.MultiInstance.Abstractions;

namespace BaBaBooeyStrap.App.Services;

public sealed record ActiveServerSessionInfo(
    int ProcessId,
    string? AccountUsername,
    string PlaceId,
    string JobId,
    string ServerIp,
    int ServerPort,
    ServerLocationInfo? Location,
    DateTime JoinedAtUtc
);

public sealed class BootstrapperOrchestrator : IRobloxLauncher, IDisposable
{
    private readonly IPaths _paths;
    private readonly IJsonManager<Settings> _settingsManager;
    private readonly IJsonManager<State> _stateManager;
    private readonly IJsonManager<RobloxState> _robloxStateManager;
    private readonly IMultiInstanceController _multiInstanceController;
    private readonly IProcessSupervisor _processSupervisor;
    private readonly ILaunchGate _launchGate;
    private readonly IRobloxAuthService _authService;
    private readonly IRoValraService _roValraService;
    private readonly ICustomIntegrationRunner _customIntegrationRunner;
    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly IReadOnlyList<IBootstrapPipelineStep> _pipelineSteps;
    private readonly List<WatcherSession> _activeWatchers = new();
    private readonly ConcurrentDictionary<int, ActiveServerSessionInfo> _activeServers = new();
    private readonly ConcurrentDictionary<int, ActivityWatcher> _activeSessionWatchers = new();
    private readonly object _watcherLock = new();
    private readonly SemaphoreSlim _prepareLock = new(1, 1);
    private bool _disposed;

    public event EventHandler<ActiveServerSessionInfo>? ServerLocationResolved;
    public event EventHandler? ActiveServersChanged;

    public BootstrapperOrchestrator(
        IPaths? paths = null,
        IJsonManager<Settings>? settingsManager = null,
        IJsonManager<State>? stateManager = null,
        IJsonManager<RobloxState>? robloxStateManager = null,
        IMultiInstanceController? multiInstanceController = null,
        IProcessSupervisor? processSupervisor = null,
        IRobloxAuthService? authService = null,
        ILogger? logger = null,
        IRoValraService? roValraService = null,
        IEnumerable<IBootstrapPipelineStep>? pipelineSteps = null,
        HttpClient? httpClient = null,
        ICustomIntegrationRunner? customIntegrationRunner = null)
    {
        _paths = paths ?? Paths.Current;
        _settingsManager = settingsManager ?? new JsonManager<Settings>(_paths.SettingsFile);
        _stateManager = stateManager ?? new JsonManager<State>(_paths.StateFile);
        _robloxStateManager = robloxStateManager ?? new JsonManager<RobloxState>(_paths.RobloxStateFile);
        _multiInstanceController = multiInstanceController ?? new WindowsMultiInstanceController();
        _processSupervisor = processSupervisor ?? new ProcessSupervisor();
        _launchGate = new LaunchGate(_multiInstanceController);
        _authService = authService ?? new RobloxAuthService();
        _logger = logger ?? Logger.Current;
        _ownsHttpClient = httpClient == null;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _roValraService = roValraService ?? new RoValraService(_httpClient, logger: _logger);
        _customIntegrationRunner = customIntegrationRunner ?? new CustomIntegrationRunner(_logger);
        _pipelineSteps = pipelineSteps?.ToList()
            ?? AppServiceConfiguration.CreateDefaultBootstrapPipeline(
                _paths,
                _settingsManager,
                _stateManager,
                _robloxStateManager,
                _httpClient,
                _logger);
    }

    public IMultiInstanceController MultiInstanceController => _multiInstanceController;
    public IProcessSupervisor ProcessSupervisor => _processSupervisor;

    public IReadOnlyList<ActiveServerSessionInfo> GetActiveServers() =>
        _activeServers.Values.OrderBy(s => s.JoinedAtUtc).ToList();

    public async Task<LaunchResult> LaunchPlayerAsync(LaunchSettings settings, CancellationToken ct = default)
    {
        return await LaunchWithProgressAsync(settings, null, null, skipDeploymentCheck: false, ct).ConfigureAwait(false);
    }

    public Task<LaunchResult> LaunchWithProgressAsync(
        LaunchSettings settings,
        IProgress<PackageDownloadProgress>? progress,
        Action<string>? statusCallback,
        CancellationToken ct = default)
    {
        return LaunchWithProgressAsync(settings, progress, statusCallback, skipDeploymentCheck: false, ct);
    }

    public async Task PrepareForBatchLaunchAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await _prepareLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await PrepareDeploymentAndModsCoreAsync(new LaunchSettings(), isStudio: false, progress: null, statusCallback: null, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            _prepareLock.Release();
        }
    }

    public Task<LaunchResult> LaunchWithProgressAsync(
        LaunchSettings settings,
        IProgress<PackageDownloadProgress>? progress,
        Action<string>? statusCallback,
        bool skipDeploymentCheck,
        CancellationToken ct = default)
    {
        return LaunchWithProgressInternalAsync(settings, progress, statusCallback, skipDeploymentCheck, accountUsername: null, ct);
    }

    private async Task<LaunchResult> LaunchWithProgressInternalAsync(
        LaunchSettings settings,
        IProgress<PackageDownloadProgress>? progress,
        Action<string>? statusCallback,
        bool skipDeploymentCheck,
        string? accountUsername,
        CancellationToken ct = default,
        Func<LaunchSettings, CancellationToken, Task<(LaunchSettings? PreparedSettings, LaunchResult? Failure)>>? preSpawnSettingsBuilder = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ct.ThrowIfCancellationRequested();

        var appSettings = _settingsManager.Prop;

        // 1. Multi-Instance launch gate
        if (appSettings.MultiInstanceEnabled)
        {
            statusCallback?.Invoke(LocalizationService.Instance.GetString("Str_Bootstrapper_StatusVerifyingMutex"));
            var gateResult = _launchGate.VerifyLaunchReadiness(true);
            if (!gateResult.CanLaunch)
            {
                _logger.Warn("BootstrapperOrchestrator", gateResult.WarningMessage ?? "Launch blocked by mutex gate.");
                return new LaunchResult(
                    false,
                    settings.RobloxLaunchArgs,
                    RobloxLauncher.ParseTargetFromUri(settings.RobloxLaunchArgs),
                    DateTimeOffset.UtcNow,
                    null,
                    gateResult.WarningMessage);
            }
        }

        // 2-4. Execute bootstrap pipeline (version check, package deployment, mods/FastFlags, pre-launch cleanup) under _prepareLock
        bool isStudio = settings.RobloxLaunchMode is LaunchMode.Studio or LaunchMode.StudioAuth;
        if (!skipDeploymentCheck)
        {
            string? resolvedGuid;
            string? pipelineFailureReason = null;
            await _prepareLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var pipelineResult = await PrepareDeploymentAndModsCoreAsync(settings, isStudio, progress, statusCallback, ct)
                    .ConfigureAwait(false);
                resolvedGuid = pipelineResult.ResolvedVersionGuid;
                pipelineFailureReason = pipelineResult.FailureReason;
            }
            finally
            {
                _prepareLock.Release();
            }

            if (!string.IsNullOrWhiteSpace(pipelineFailureReason))
            {
                _logger.Error("BootstrapperOrchestrator", pipelineFailureReason);
                return new LaunchResult(
                    false,
                    settings.RobloxLaunchArgs,
                    RobloxLauncher.ParseTargetFromUri(settings.RobloxLaunchArgs),
                    DateTimeOffset.UtcNow,
                    null,
                    pipelineFailureReason);
            }

            if (!string.IsNullOrWhiteSpace(resolvedGuid) && string.IsNullOrWhiteSpace(settings.VersionFlag.Data))
            {
                settings.VersionFlag.Active = true;
                settings.VersionFlag.Data = resolvedGuid;
            }
        }

        // 5. Launch delay throttling (multi-instance safety)
        if (appSettings.MultiInstanceEnabled && _processSupervisor.GetRunningRobloxPids().Count > 0)
        {
            statusCallback?.Invoke(LocalizationService.Instance.GetString("Str_Bootstrapper_StatusWaitingThrottle"));
            await _processSupervisor.ThrottleLaunchAsync(
                TimeSpan.FromMilliseconds(Math.Max(500, appSettings.MultiInstanceLaunchDelayMs)),
                ct).ConfigureAwait(false);
        }

        HashSet<string>? preExistingLogs = null;
        if (appSettings.EnableActivityTracking && !isStudio && Directory.Exists(_paths.RobloxLogs))
        {
            try
            {
                preExistingLogs = Directory.GetFiles(_paths.RobloxLogs, "*.log")
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            catch { }
        }

        // 5b. Launch PreLaunch Custom Integrations
        IReadOnlyList<int> preLaunchedAutoClosePids = Array.Empty<int>();
        if (!isStudio && appSettings.CustomIntegrations.Count > 0)
        {
            preLaunchedAutoClosePids = await _customIntegrationRunner
                .LaunchIntegrationsAsync(appSettings.CustomIntegrations, preLaunchOnly: true, ct: ct)
                .ConfigureAwait(false);
        }

        // 5c. Build deferred auth ticket / matchmaking server right before process spawn so ticket never expires during long updates
        if (preSpawnSettingsBuilder != null)
        {
            var (preparedSettings, failureResult) = await preSpawnSettingsBuilder(settings, ct).ConfigureAwait(false);
            if (failureResult != null)
            {
                return failureResult;
            }
            if (preparedSettings != null)
            {
                settings = preparedSettings;
            }
        }
        else if (!isStudio && appSettings.EnableBetterMatchmaking && PlaceLauncherUriBuilder.IsValidLaunchUri(settings.RobloxLaunchArgs))
        {
            try
            {
                var uriBuilder = PlaceLauncherUriBuilder.Parse(settings.RobloxLaunchArgs);
                if (uriBuilder.PlaceId > 0 &&
                    string.IsNullOrWhiteSpace(uriBuilder.JobId) &&
                    string.IsNullOrWhiteSpace(uriBuilder.AccessCode) &&
                    string.IsNullOrWhiteSpace(uriBuilder.LinkCode) &&
                    !uriBuilder.UserId.HasValue)
                {
                    string? serverId = await _roValraService
                        .GetBetterMatchmakingServerIdAsync(
                            uriBuilder.PlaceId,
                            appSettings.EnableBetterMatchmakingRandomization,
                            requiredSlots: 1,
                            excludedServerIds: null,
                            ct)
                        .ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(serverId))
                    {
                        uriBuilder.JobId = serverId;
                        settings.RobloxLaunchArgs = uriBuilder.BuildProtocolUri();
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Warn("BootstrapperOrchestrator", $"Better matchmaking lookup failed, continuing with default matchmaking: {ex.Message}");
            }
        }

        // 6. Launch Roblox executable via Core RobloxLauncher
        statusCallback?.Invoke(LocalizationService.Instance.GetString("Str_Bootstrapper_StatusStartingRoblox"));
        var coreLauncher = new RobloxLauncher(_paths, _authService, _robloxStateManager, _logger);
        var result = await coreLauncher.LaunchPlayerAsync(settings, ct).ConfigureAwait(false);

        if (result.Success && result.ProcessId.HasValue)
        {
            int pid = result.ProcessId.Value;
            _processSupervisor.RegisterProcess(pid);
            _customIntegrationRunner.BindPreLaunchedToPid(preLaunchedAutoClosePids, pid);

            try
            {
                string handlerExe = File.Exists(_paths.Application) ? _paths.Application : _paths.Process;
                if (!string.IsNullOrWhiteSpace(handlerExe) && File.Exists(handlerExe))
                {
                    if (!WindowsRegistry.ArePlayerProtocolsRegistered(handlerExe))
                    {
                        WindowsRegistry.RegisterPlayer(handlerExe);
                    }
                    WindowsRegistry.RemoveRobloxStartupEntry();
                }
            }
            catch { }

            if (!isStudio && appSettings.CustomIntegrations.Count > 0)
            {
                var integrationSnapshot = appSettings.CustomIntegrations.ToList();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _customIntegrationRunner
                            .LaunchIntegrationsAsync(integrationSnapshot, preLaunchOnly: false, trackedRobloxPid: pid, ct)
                            .ConfigureAwait(false);
                    }
                    catch { }
                });
            }

            if (appSettings.EnableActivityTracking && !isStudio)
            {
                StartWatcherForPid(pid, appSettings, accountUsername, preExistingLogs);
            }
            else if (!isStudio && appSettings.CustomIntegrations.Any(i => i.AutoClose))
            {
                MonitorProcessExitForCustomIntegrations(pid);
            }
        }

        return result;
    }

    private async Task<BootstrapPipelineContext> PrepareDeploymentAndModsCoreAsync(
        LaunchSettings settings,
        bool isStudio,
        IProgress<PackageDownloadProgress>? progress,
        Action<string>? statusCallback,
        CancellationToken ct)
    {
        var context = new BootstrapPipelineContext(
            settings,
            _settingsManager.Prop,
            isStudio,
            progress,
            statusCallback);

        foreach (var step in _pipelineSteps)
        {
            ct.ThrowIfCancellationRequested();
            await step.ExecuteAsync(context, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(context.FailureReason))
            {
                break;
            }
        }

        return context;
    }

    public async Task<IReadOnlyList<string?>> AllocateBatchMatchmakingServersAsync(
        long placeId,
        int accountCount,
        CancellationToken ct = default)
    {
        if (accountCount <= 0)
        {
            return Array.Empty<string?>();
        }

        var appSettings = _settingsManager.Prop;
        if (!appSettings.EnableBetterMatchmaking || placeId <= 0)
        {
            return new string?[accountCount];
        }

        try
        {
            return await _roValraService
                .AllocateBetterMatchmakingServersAsync(
                    placeId,
                    accountCount,
                    appSettings.EnableBetterMatchmakingRandomization,
                    ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn("BootstrapperOrchestrator", $"Batch better matchmaking lookup failed for place {placeId}: {ex.Message}");
            return new string?[accountCount];
        }
    }

    public Task<LaunchResult> LaunchAccountAsync(RobloxAccount account, LaunchTarget target, CancellationToken ct = default)
    {
        return LaunchAccountAsync(account, target, null, null, skipDeploymentCheck: false, skipBetterMatchmaking: false, ct);
    }

    public Task<LaunchResult> LaunchAccountAsync(
        RobloxAccount account,
        LaunchTarget target,
        bool skipDeploymentCheck,
        CancellationToken ct = default)
    {
        return LaunchAccountAsync(account, target, null, null, skipDeploymentCheck, skipBetterMatchmaking: false, ct);
    }

    public Task<LaunchResult> LaunchAccountAsync(
        RobloxAccount account,
        LaunchTarget target,
        IProgress<PackageDownloadProgress>? progress,
        Action<string>? statusCallback,
        bool skipDeploymentCheck = false,
        CancellationToken ct = default)
    {
        return LaunchAccountAsync(account, target, progress, statusCallback, skipDeploymentCheck, skipBetterMatchmaking: false, ct);
    }

    public async Task<LaunchResult> LaunchAccountAsync(
        RobloxAccount account,
        LaunchTarget target,
        IProgress<PackageDownloadProgress>? progress,
        Action<string>? statusCallback,
        bool skipDeploymentCheck,
        bool skipBetterMatchmaking,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(target);
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(account.Cookie))
        {
            return new LaunchResult(false, string.Empty, target, DateTimeOffset.UtcNow, null, "Account cookie is empty.");
        }

        var appSettings = _settingsManager.Prop;
        if (appSettings.MultiInstanceEnabled)
        {
            var gateResult = _launchGate.VerifyLaunchReadiness(true);
            if (!gateResult.CanLaunch)
            {
                _logger.Warn("BootstrapperOrchestrator", gateResult.WarningMessage ?? "Launch blocked by mutex gate.");
                return new LaunchResult(
                    false,
                    string.Empty,
                    target,
                    DateTimeOffset.UtcNow,
                    null,
                    gateResult.WarningMessage);
            }
        }

        var coreLauncher = new RobloxLauncher(_paths, _authService, _robloxStateManager, _logger);
        RobloxLauncher.ResolvedLaunchTargetDetails resolution;
        try
        {
            resolution = await coreLauncher.ResolveAccountLaunchTargetAsync(account.Cookie, target, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (RobloxApiException apiEx)
        {
            _logger.Warn("BootstrapperOrchestrator", $"Failed to resolve target for '{account.Username}': {apiEx.StatusCode} {apiEx.Message}");
            return new LaunchResult(false, string.Empty, target, DateTimeOffset.UtcNow, null, apiEx.Message, apiEx.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.Warn("BootstrapperOrchestrator", $"Failed to resolve target for '{account.Username}': {ex.Message}");
            return new LaunchResult(false, string.Empty, target, DateTimeOffset.UtcNow, null, ex.Message);
        }

        if (!resolution.Success)
        {
            string? errorMessage = resolution.NotInGameTargetName != null
                ? LocalizationService.Instance.Format("Str_Accounts_FollowUserNotInGameFormat", resolution.NotInGameTargetName)
                : resolution.ErrorMessage;
            return new LaunchResult(false, string.Empty, resolution.Target, DateTimeOffset.UtcNow, null, errorMessage);
        }

        string? effectiveChannel = !string.IsNullOrWhiteSpace(appSettings.Channel) &&
                                   !string.Equals(appSettings.Channel, "production", StringComparison.OrdinalIgnoreCase) &&
                                   !string.Equals(appSettings.Channel, "live", StringComparison.OrdinalIgnoreCase)
            ? appSettings.Channel.Trim()
            : null;

        var initialSettings = new LaunchSettings
        {
            RobloxLaunchMode = LaunchMode.Player
        };

        var result = await LaunchWithProgressInternalAsync(
            initialSettings,
            progress,
            statusCallback,
            skipDeploymentCheck,
            account.Username,
            ct,
            preSpawnSettingsBuilder: async (preparedSettings, innerCt) =>
            {
                string? jobId = resolution.JobId;
                if (!skipBetterMatchmaking &&
                    appSettings.EnableBetterMatchmaking &&
                    resolution.PlaceId > 0 &&
                    string.IsNullOrWhiteSpace(jobId) &&
                    string.IsNullOrWhiteSpace(resolution.LinkCode) &&
                    !resolution.FollowUserId.HasValue)
                {
                    try
                    {
                        string? bestServerId = await _roValraService
                            .GetBetterMatchmakingServerIdAsync(
                                resolution.PlaceId,
                                appSettings.EnableBetterMatchmakingRandomization,
                                requiredSlots: 1,
                                excludedServerIds: null,
                                innerCt)
                            .ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(bestServerId))
                        {
                            jobId = bestServerId;
                        }
                    }
                    catch (OperationCanceledException) when (innerCt.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn("BootstrapperOrchestrator", $"Better matchmaking lookup failed for '{account.Username}': {ex.Message}");
                    }
                }

                string authTicket;
                try
                {
                    authTicket = await _authService.GenerateAuthTicketAsync(account.Cookie, innerCt).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (innerCt.IsCancellationRequested)
                {
                    throw;
                }
                catch (RobloxApiException apiEx)
                {
                    _logger.Warn("BootstrapperOrchestrator", $"Auth ticket failed for '{account.Username}': {apiEx.StatusCode} {apiEx.Message}");
                    return (null, new LaunchResult(false, string.Empty, resolution.Target, DateTimeOffset.UtcNow, null, apiEx.Message, apiEx.StatusCode));
                }
                catch (Exception ex)
                {
                    _logger.Warn("BootstrapperOrchestrator", $"Auth ticket failed for '{account.Username}': {ex.Message}");
                    return (null, new LaunchResult(false, string.Empty, resolution.Target, DateTimeOffset.UtcNow, null, ex.Message));
                }

                var builder = new PlaceLauncherUriBuilder
                {
                    PlaceId = resolution.PlaceId,
                    JobId = jobId,
                    LinkCode = resolution.LinkCode,
                    UserId = resolution.FollowUserId,
                    AuthTicket = authTicket,
                    Channel = effectiveChannel,
                    LaunchTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };

                preparedSettings.RobloxLaunchArgs = builder.BuildProtocolUri();
                preparedSettings.RobloxLaunchMode = LaunchMode.Player;
                return (preparedSettings, null);
            }).ConfigureAwait(false);

        if (result.Success)
        {
            account.LastLaunchedAt = DateTimeOffset.UtcNow;
        }

        return result with { Target = resolution.Target };
    }

    private void StartWatcherForPid(
        int pid,
        Settings appSettings,
        string? accountUsername = null,
        IReadOnlySet<string>? preExistingLogs = null)
    {
        try
        {
            var data = new WatcherData
            {
                ProcessId = pid,
                LogFile = _paths.RobloxLogs
            };

            ActivityWatcher? activityWatcher = null;
            activityWatcher = new ActivityWatcher(_logger, new ActivityWatcherOptions
            {
                LockToFirstSelectedLogFile = true,
                ExcludedLogFilePredicate = path =>
                {
                    if (preExistingLogs != null && preExistingLogs.Contains(path))
                    {
                        return true;
                    }

                    foreach (var kvp in _activeSessionWatchers)
                    {
                        if (kvp.Key != pid &&
                            !ReferenceEquals(kvp.Value, activityWatcher) &&
                            string.Equals(kvp.Value.CurrentLogFilePath, path, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }

                    return false;
                }
            });
            _activeSessionWatchers[pid] = activityWatcher;

            IDiscordRpcService? discordRpc = appSettings.UseDiscordRichPresence
                ? new DiscordRpcService(new DiscordRpcOptions
                {
                    HideButtons = appSettings.HideRPCButtons,
                    StatusDisplayType = appSettings.RichPresenceStatusDisplayType
                }, _logger)
                : null;

            IWindowManipulation? windowManip = appSettings.EnableWindowManipulation
                ? new WindowManipulationService(
                    new Win32WindowInterop(),
                    new WindowManipulationOptions
                    {
                        TargetProcessId = (uint)pid,
                        CustomTitleTemplate = appSettings.RobloxTitle,
                        CustomIconPath = appSettings.RobloxIcon == RobloxIcon.IconCustom ? appSettings.RobloxIconCustomLocation : null,
                        BorderlessFullscreen = appSettings.FakeBorderlessFullscreen
                    },
                    _logger)
                : null;

            activityWatcher.GameJoined += async (_, joinArgs) =>
            {
                await HandleGameJoinedAsync(pid, accountUsername, joinArgs, appSettings, discordRpc).ConfigureAwait(false);
            };

            activityWatcher.GameLeft += async (_, _) =>
            {
                await HandleGameLeftAsync(pid, discordRpc).ConfigureAwait(false);
            };

            windowManip?.StartHook();

            var session = new WatcherSession(data, activityWatcher, new ProcessLifecycleMonitor(), discordRpc, windowManip, _logger);
            lock (_watcherLock)
            {
                _activeWatchers.Add(session);
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await session.RunAsync().ConfigureAwait(false);
                }
                catch { }
                finally
                {
                    _processSupervisor.UnregisterProcess(pid);
                    _customIntegrationRunner.CloseIntegrationsForPid(pid);
                    _activeSessionWatchers.TryRemove(pid, out _);
                    if (_activeServers.TryRemove(pid, out _))
                    {
                        ActiveServersChanged?.Invoke(this, EventArgs.Empty);
                    }
                    lock (_watcherLock)
                    {
                        _activeWatchers.Remove(session);
                    }
                    session.Dispose();
                }
            });
        }
        catch (Exception ex)
        {
            _logger.Warn("BootstrapperOrchestrator", $"Failed to start WatcherSession for PID {pid}: {ex.Message}");
        }
    }

    internal async Task HandleGameJoinedAsync(
        int pid,
        string? accountUsername,
        GameJoinEventArgs joinArgs,
        Settings appSettings,
        IDiscordRpcService? discordRpc = null)
    {
        try
        {
            var initialSession = new ActiveServerSessionInfo(
                pid,
                accountUsername,
                joinArgs.PlaceId,
                joinArgs.JobId,
                joinArgs.ServerIp,
                joinArgs.ServerPort,
                null,
                joinArgs.Timestamp);

            _activeServers[pid] = initialSession;
            ActiveServersChanged?.Invoke(this, EventArgs.Empty);

            ServerLocationInfo? resolvedLocation = null;
            if (!string.IsNullOrWhiteSpace(joinArgs.ServerIp))
            {
                try
                {
                    resolvedLocation = await _roValraService.ResolveServerLocationAsync(joinArgs.ServerIp).ConfigureAwait(false);
                }
                catch { }
            }

            var finalSession = initialSession with { Location = resolvedLocation };
            if (_activeServers.TryUpdate(pid, finalSession, initialSession))
            {
                ActiveServersChanged?.Invoke(this, EventArgs.Empty);
                if (appSettings.ShowServerDetails)
                {
                    ServerLocationResolved?.Invoke(this, finalSession);
                }
            }

            if (discordRpc != null)
            {
                if (!discordRpc.IsConnected)
                    await discordRpc.ConnectAsync().ConfigureAwait(false);

                string details = $"Playing Place {joinArgs.PlaceId}";
                if (appSettings.ShowAccountOnRichPresence && !string.IsNullOrWhiteSpace(accountUsername))
                {
                    details = $"{details} ({accountUsername})";
                }
                string state = appSettings.ShowServerDetails && resolvedLocation != null
                    ? $"{resolvedLocation.City}, {resolvedLocation.Country}"
                    : (appSettings.ShowServerDetails && !string.IsNullOrWhiteSpace(joinArgs.ServerIp)
                        ? $"Server: {joinArgs.ServerIp}"
                        : "In Game");

                string deepLink = DiscordRpcService.BuildRobloxDeepLink(joinArgs.PlaceId, joinArgs.JobId);
                await discordRpc.UpdatePresenceAsync(new DiscordActivity(
                    details,
                    state,
                    null,
                    appSettings.BootstrapperTitle,
                    null,
                    null,
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    joinArgs.JobId,
                    deepLink)).ConfigureAwait(false);
            }
        }
        catch { }
    }

    internal async Task HandleGameLeftAsync(int pid, IDiscordRpcService? discordRpc = null)
    {
        if (_activeServers.TryRemove(pid, out _))
        {
            ActiveServersChanged?.Invoke(this, EventArgs.Empty);
        }

        if (discordRpc != null)
        {
            try { await discordRpc.ClearPresenceAsync().ConfigureAwait(false); } catch { }
        }
    }

    private void MonitorProcessExitForCustomIntegrations(int pid)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                await proc.WaitForExitAsync().ConfigureAwait(false);
            }
            catch { }
            finally
            {
                _customIntegrationRunner.CloseIntegrationsForPid(pid);
            }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _customIntegrationRunner.Dispose(); } catch { }

        lock (_watcherLock)
        {
            foreach (var watcher in _activeWatchers.ToList())
            {
                try { watcher.Dispose(); } catch { }
            }
            _activeWatchers.Clear();
        }
        _activeSessionWatchers.Clear();
        _activeServers.Clear();
        _prepareLock.Dispose();
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
