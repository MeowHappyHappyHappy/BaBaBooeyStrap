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
    private readonly IRunningInstanceTracker? _runningInstanceTracker;
    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly IReadOnlyList<IBootstrapPipelineStep> _pipelineSteps;
    private readonly List<WatcherSession> _activeWatchers = new();
    private readonly ConcurrentDictionary<int, ActiveServerSessionInfo> _activeServers = new();
    private readonly ConcurrentDictionary<int, ActivityWatcher> _activeSessionWatchers = new();
    private readonly ConcurrentDictionary<int, IWindowManipulation> _activeWindowManips = new();
    private readonly object _watcherLock = new();
    private readonly SemaphoreSlim _prepareLock = new(1, 1);
    private readonly CancellationTokenSource _shutdownCts = new();
    private volatile bool _disposed;

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
        ICustomIntegrationRunner? customIntegrationRunner = null,
        IRunningInstanceTracker? runningInstanceTracker = null)
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
        _runningInstanceTracker = runningInstanceTracker;
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
    public IRunningInstanceTracker? RunningInstanceTracker => _runningInstanceTracker;

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
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();

        CancellationTokenSource linkedCts;
        try
        {
            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdownCts.Token);
        }
        catch (ObjectDisposedException)
        {
            throw new ObjectDisposedException(nameof(BootstrapperOrchestrator));
        }

        using var _ = linkedCts;
        bool lockAcquired = false;
        try
        {
            await _prepareLock.WaitAsync(linkedCts.Token).ConfigureAwait(false);
            lockAcquired = true;
            await PrepareDeploymentAndModsCoreAsync(new LaunchSettings(), isStudio: false, progress: null, statusCallback: null, linkedCts.Token)
                .ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (_disposed)
        {
            throw new ObjectDisposedException(nameof(BootstrapperOrchestrator));
        }
        finally
        {
            if (lockAcquired)
            {
                try { _prepareLock.Release(); } catch (ObjectDisposedException) { }
            }
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
        Func<LaunchSettings, CancellationToken, Task<(LaunchSettings? PreparedSettings, LaunchResult? Failure)>>? preSpawnSettingsBuilder = null,
        RobloxAccount? launchedAccount = null,
        bool disableMultiInstanceForLaunch = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ct.ThrowIfCancellationRequested();

        var appSettings = _settingsManager.Prop;
        bool effectiveMultiInstance = appSettings.MultiInstanceEnabled && !disableMultiInstanceForLaunch;

        // 1. Multi-Instance launch gate
        if (effectiveMultiInstance)
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
            ObjectDisposedException.ThrowIf(_disposed, this);

            CancellationTokenSource linkedCts;
            try
            {
                linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdownCts.Token);
            }
            catch (ObjectDisposedException)
            {
                throw new ObjectDisposedException(nameof(BootstrapperOrchestrator));
            }

            string? resolvedGuid;
            string? pipelineFailureReason = null;
            bool lockAcquired = false;
            using (linkedCts)
            {
                try
                {
                    await _prepareLock.WaitAsync(linkedCts.Token).ConfigureAwait(false);
                    lockAcquired = true;
                    var pipelineResult = await PrepareDeploymentAndModsCoreAsync(settings, isStudio, progress, statusCallback, linkedCts.Token)
                        .ConfigureAwait(false);
                    resolvedGuid = pipelineResult.ResolvedVersionGuid;
                    pipelineFailureReason = pipelineResult.FailureReason;
                }
                catch (ObjectDisposedException) when (_disposed)
                {
                    throw new ObjectDisposedException(nameof(BootstrapperOrchestrator));
                }
                finally
                {
                    if (lockAcquired)
                    {
                        try { _prepareLock.Release(); } catch (ObjectDisposedException) { }
                    }
                }
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
        if (effectiveMultiInstance && _processSupervisor.GetRunningRobloxPids().Count > 0)
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

        bool boundPreLaunchedToPid = false;
        LaunchResult result;
        try
        {
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
            result = await coreLauncher.LaunchPlayerAsync(settings, ct).ConfigureAwait(false);

            if (result.Success && result.ProcessId.HasValue)
            {
                int pid = result.ProcessId.Value;
                _processSupervisor.RegisterProcess(pid);
                _customIntegrationRunner.BindPreLaunchedToPid(preLaunchedAutoClosePids, pid);
                boundPreLaunchedToPid = true;

                bool willManageTitleExternally = appSettings.EnableActivityTracking &&
                                                !isStudio &&
                                                appSettings.EnableWindowManipulation;
                if (willManageTitleExternally)
                {
                    _runningInstanceTracker?.MarkTitleManagedExternally(pid, true);
                }

                if (launchedAccount != null)
                {
                    _runningInstanceTracker?.RegisterLaunchedAccount(pid, launchedAccount);
                }
                else
                {
                    _runningInstanceTracker?.RefreshNow();
                }

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
                    CancellationToken shutdownToken;
                    try { shutdownToken = _shutdownCts.Token; } catch (ObjectDisposedException) { shutdownToken = CancellationToken.None; }
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, shutdownToken);
                            await _customIntegrationRunner
                                .LaunchIntegrationsAsync(integrationSnapshot, preLaunchOnly: false, trackedRobloxPid: pid, linked.Token)
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
        finally
        {
            if (!boundPreLaunchedToPid && preLaunchedAutoClosePids.Count > 0)
            {
                try
                {
                    _customIntegrationRunner.CloseIntegrationPids(preLaunchedAutoClosePids);
                }
                catch { }
            }
        }
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
        CancellationToken ct = default,
        bool disableMultiInstanceForLaunch = false)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(target);
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(account.Cookie))
        {
            return new LaunchResult(false, string.Empty, target, DateTimeOffset.UtcNow, null, "Account cookie is empty.");
        }

        var appSettings = _settingsManager.Prop;
        bool effectiveMultiInstance = appSettings.MultiInstanceEnabled && !disableMultiInstanceForLaunch;
        if (effectiveMultiInstance)
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
            },
            launchedAccount: account,
            disableMultiInstanceForLaunch: disableMultiInstanceForLaunch).ConfigureAwait(false);

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
        ActivityWatcher? activityWatcher = null;
        IDiscordRpcService? discordRpc = null;
        IWindowManipulation? windowManip = null;
        WatcherSession? session = null;

        try
        {
            var data = new WatcherData
            {
                ProcessId = pid,
                LogFile = _paths.RobloxLogs
            };

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

            discordRpc = appSettings.UseDiscordRichPresence
                ? new DiscordRpcService(new DiscordRpcOptions
                {
                    HideButtons = appSettings.HideRPCButtons,
                    StatusDisplayType = appSettings.RichPresenceStatusDisplayType
                }, _logger)
                : null;

            IReadOnlyDictionary<string, string>? titlePlaceholders = _runningInstanceTracker?.GetPlaceholdersForPid(pid);
            if (titlePlaceholders == null)
            {
                string safeAccount = !string.IsNullOrWhiteSpace(accountUsername) ? accountUsername : string.Empty;
                titlePlaceholders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Index"] = "1",
                    ["Account"] = safeAccount,
                    ["AccountName"] = safeAccount,
                    ["DisplayName"] = safeAccount,
                    ["Notes"] = string.Empty,
                    ["Server"] = string.Empty,
                    ["ServerCode"] = string.Empty,
                    ["Pid"] = pid.ToString()
                };
            }

            string effectiveTitleTemplate = appSettings.RobloxTitle;
            if (appSettings.AutoTagMultiInstanceWindowTitle &&
                (string.IsNullOrWhiteSpace(effectiveTitleTemplate) ||
                 string.Equals(effectiveTitleTemplate.Trim(), "Roblox", StringComparison.OrdinalIgnoreCase)))
            {
                effectiveTitleTemplate = appSettings.MultiInstanceWindowTitleTemplate;
            }

            windowManip = appSettings.EnableWindowManipulation
                ? new WindowManipulationService(
                    new Win32WindowInterop(),
                    new WindowManipulationOptions
                    {
                        TargetProcessId = (uint)pid,
                        CustomTitleTemplate = effectiveTitleTemplate,
                        TitlePlaceholders = titlePlaceholders,
                        CustomIconPath = appSettings.RobloxIcon == RobloxIcon.IconCustom ? appSettings.RobloxIconCustomLocation : null,
                        BorderlessFullscreen = appSettings.FakeBorderlessFullscreen
                    },
                    _logger)
                : null;

            if (windowManip != null)
            {
                _activeWindowManips[pid] = windowManip;
                _runningInstanceTracker?.MarkTitleManagedExternally(pid, true);
            }
            else
            {
                _runningInstanceTracker?.MarkTitleManagedExternally(pid, false);
            }

            activityWatcher.GameJoined += async (_, joinArgs) =>
            {
                try
                {
                    string? effectiveUsername = accountUsername;
                    if (_runningInstanceTracker != null &&
                        long.TryParse(activityWatcher.CurrentActivity?.UserId, out long parsedUserId) &&
                        parsedUserId > 0)
                    {
                        if (_runningInstanceTracker.TryAssociateUserId(pid, parsedUserId) &&
                            _activeWindowManips.TryGetValue(pid, out var associatedManip))
                        {
                            associatedManip.UpdateTitlePlaceholders(_runningInstanceTracker.GetPlaceholdersForPid(pid));
                        }
                        if (string.IsNullOrWhiteSpace(effectiveUsername))
                        {
                            effectiveUsername = _runningInstanceTracker.GetRunningInstances()
                                .FirstOrDefault(i => i.ProcessId == pid)?.Username;
                        }
                    }

                    await HandleGameJoinedAsync(pid, effectiveUsername, joinArgs, appSettings, discordRpc).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.Debug("BootstrapperOrchestrator", $"GameJoined handler error for PID {pid}: {ex.Message}");
                }
            };

            activityWatcher.GameLeft += async (_, _) =>
            {
                try
                {
                    await HandleGameLeftAsync(pid, discordRpc).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.Debug("BootstrapperOrchestrator", $"GameLeft handler error for PID {pid}: {ex.Message}");
                }
            };

            windowManip?.StartHook();

            session = new WatcherSession(data, activityWatcher, new ProcessLifecycleMonitor(), discordRpc, windowManip, _logger, ownsIntegrations: true);
            lock (_watcherLock)
            {
                _activeWatchers.Add(session);
            }

            CancellationToken shutdownToken;
            try
            {
                shutdownToken = _shutdownCts.Token;
            }
            catch (ObjectDisposedException)
            {
                shutdownToken = new CancellationToken(canceled: true);
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await session.RunAsync(shutdownToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    _logger.Debug("BootstrapperOrchestrator", $"WatcherSession failed for PID {pid}: {ex.Message}");
                }
                finally
                {
                    _processSupervisor.UnregisterProcess(pid);
                    _activeWindowManips.TryRemove(pid, out _);
                    _runningInstanceTracker?.MarkTitleManagedExternally(pid, false);
                    _runningInstanceTracker?.RefreshNow();
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
            _activeWindowManips.TryRemove(pid, out _);
            _activeSessionWatchers.TryRemove(pid, out _);
            _runningInstanceTracker?.MarkTitleManagedExternally(pid, false);

            if (session != null)
            {
                lock (_watcherLock)
                {
                    _activeWatchers.Remove(session);
                }
                try { session.Dispose(); } catch { }
            }
            else
            {
                try { (windowManip as IDisposable)?.Dispose(); } catch { }
                try { (discordRpc as IDisposable)?.Dispose(); } catch { }
                try { activityWatcher?.Dispose(); } catch { }
            }

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
            else
            {
                return;
            }

            string serverCode = BaBaBooeyStrap.Integrations.Window.RunningInstanceTracker.NormalizeServerCode(resolvedLocation?.Country);
            _runningInstanceTracker?.UpdateServerCode(pid, serverCode);
            UpdateWindowTitlePlaceholdersForPid(pid, accountUsername, serverCode);

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
        string? accountUsername = null;
        if (_activeServers.TryRemove(pid, out var removedSession))
        {
            accountUsername = removedSession.AccountUsername;
            ActiveServersChanged?.Invoke(this, EventArgs.Empty);
        }

        _runningInstanceTracker?.UpdateServerCode(pid, null);
        UpdateWindowTitlePlaceholdersForPid(pid, accountUsername, serverCode: string.Empty);

        if (discordRpc != null)
        {
            try { await discordRpc.ClearPresenceAsync().ConfigureAwait(false); } catch { }
        }
    }

    private void UpdateWindowTitlePlaceholdersForPid(int pid, string? accountUsername, string serverCode)
    {
        if (!_activeWindowManips.TryGetValue(pid, out var manip))
        {
            return;
        }

        if (_runningInstanceTracker != null)
        {
            manip.UpdateTitlePlaceholders(_runningInstanceTracker.GetPlaceholdersForPid(pid));
            return;
        }

        string safeAccount = !string.IsNullOrWhiteSpace(accountUsername) ? accountUsername : string.Empty;
        manip.UpdateTitlePlaceholders(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Index"] = "1",
            ["Account"] = safeAccount,
            ["AccountName"] = safeAccount,
            ["DisplayName"] = safeAccount,
            ["Notes"] = string.Empty,
            ["Server"] = serverCode,
            ["ServerCode"] = serverCode,
            ["Pid"] = pid.ToString()
        });
    }

    private void MonitorProcessExitForCustomIntegrations(int pid)
    {
        CancellationToken shutdownToken;
        try
        {
            shutdownToken = _shutdownCts.Token;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                await proc.WaitForExitAsync(shutdownToken).ConfigureAwait(false);
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

        try { _shutdownCts.Cancel(); } catch { }
        try { _customIntegrationRunner.Dispose(); } catch { }

        lock (_watcherLock)
        {
            foreach (var watcher in _activeWatchers.ToList())
            {
                try { watcher.Dispose(); } catch { }
            }
            _activeWatchers.Clear();
        }

        foreach (var manip in _activeWindowManips.Values.ToList())
        {
            try { (manip as IDisposable)?.Dispose(); } catch { }
        }
        _activeWindowManips.Clear();

        foreach (var sessionWatcher in _activeSessionWatchers.Values.ToList())
        {
            try { sessionWatcher.Dispose(); } catch { }
        }
        _activeSessionWatchers.Clear();
        _activeServers.Clear();

        try { _prepareLock.Dispose(); } catch { }
        try { _shutdownCts.Dispose(); } catch { }
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
