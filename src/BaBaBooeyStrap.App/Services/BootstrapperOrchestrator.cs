using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using BaBaBooeyStrap.Bootstrapper.Deployment;
using BaBaBooeyStrap.Bootstrapper.Deployment.Models;
using BaBaBooeyStrap.Bootstrapper.FastFlags;
using BaBaBooeyStrap.Bootstrapper.Mods;
using BaBaBooeyStrap.Bootstrapper.Settings;
using BaBaBooeyStrap.Core;
using BaBaBooeyStrap.Core.Launch;
using BaBaBooeyStrap.Core.Logging;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Models.Enums;
using BaBaBooeyStrap.Core.Roblox;
using BaBaBooeyStrap.Core.Storage;
using BaBaBooeyStrap.Integrations.Cleaner;
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
    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;
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
        IRoValraService? roValraService = null)
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
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _roValraService = roValraService ?? new RoValraService(_httpClient, logger: _logger);
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
        CancellationToken ct = default)
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

        // 2-4. Ensure target Roblox version is deployed, apply FastFlags/Mods, and run optional cleaner under _prepareLock
        bool isStudio = settings.RobloxLaunchMode is LaunchMode.Studio or LaunchMode.StudioAuth;
        if (!skipDeploymentCheck)
        {
            string? resolvedGuid;
            await _prepareLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                resolvedGuid = await PrepareDeploymentAndModsCoreAsync(settings, isStudio, progress, statusCallback, ct)
                    .ConfigureAwait(false);
            }
            finally
            {
                _prepareLock.Release();
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

        // 6. Launch Roblox executable via Core RobloxLauncher
        statusCallback?.Invoke(LocalizationService.Instance.GetString("Str_Bootstrapper_StatusStartingRoblox"));
        var coreLauncher = new RobloxLauncher(_paths, _authService, _robloxStateManager, _logger);
        var result = await coreLauncher.LaunchPlayerAsync(settings, ct).ConfigureAwait(false);

        if (result.Success && result.ProcessId.HasValue)
        {
            _processSupervisor.RegisterProcess(result.ProcessId.Value);

            if (appSettings.EnableActivityTracking && !isStudio)
            {
                StartWatcherForPid(result.ProcessId.Value, appSettings, accountUsername, preExistingLogs);
            }
        }

        return result;
    }

    private async Task<string?> PrepareDeploymentAndModsCoreAsync(
        LaunchSettings settings,
        bool isStudio,
        IProgress<PackageDownloadProgress>? progress,
        Action<string>? statusCallback,
        CancellationToken ct)
    {
        var appSettings = _settingsManager.Prop;
        string? versionGuid = await EnsureDeployedAsync(settings, isStudio, progress, statusCallback, ct).ConfigureAwait(false);
        string versionDir = !string.IsNullOrWhiteSpace(versionGuid)
            ? _paths.GetVersionDirectory(versionGuid)
            : ResolveFallbackVersionDir(isStudio);

        if (!string.IsNullOrWhiteSpace(versionDir) && Directory.Exists(versionDir))
        {
            if (!IsVersionInstallationComplete(versionDir, isStudio))
            {
                TryRepairFromCachedDownloads(versionDir, isStudio);
            }

            EnsureAppSettingsXml(versionDir);
            if (!isStudio)
            {
                TryRecoverMissingWebView2Loader(versionDir);
            }

            statusCallback?.Invoke(LocalizationService.Instance.GetString("Str_Bootstrapper_StatusApplyingMods"));
            await ApplyFastFlagsAndModsAsync(appSettings, versionDir, ct).ConfigureAwait(false);
        }

        if (appSettings.CleanerOptions != CleanerOptions.Never)
        {
            try
            {
                var cleaner = new CacheCleaner(_paths, _logger);
                cleaner.ExecuteCleanup(CleanerPolicyMapper.FromSettings(appSettings));
            }
            catch (Exception ex)
            {
                _logger.Warn("BootstrapperOrchestrator", $"Cache cleanup skipped: {ex.Message}");
            }
        }

        return versionGuid;
    }

    public Task<LaunchResult> LaunchAccountAsync(RobloxAccount account, LaunchTarget target, CancellationToken ct = default)
    {
        return LaunchAccountAsync(account, target, skipDeploymentCheck: false, ct);
    }

    public async Task<LaunchResult> LaunchAccountAsync(
        RobloxAccount account,
        LaunchTarget target,
        bool skipDeploymentCheck,
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
        var resolution = await coreLauncher.ResolveAccountLaunchTargetAsync(account.Cookie, target, ct).ConfigureAwait(false);
        if (!resolution.Success)
        {
            string? errorMessage = resolution.NotInGameTargetName != null
                ? LocalizationService.Instance.Format("Str_Accounts_FollowUserNotInGameFormat", resolution.NotInGameTargetName)
                : resolution.ErrorMessage;
            return new LaunchResult(false, string.Empty, resolution.Target, DateTimeOffset.UtcNow, null, errorMessage);
        }

        string authTicket = await _authService.GenerateAuthTicketAsync(account.Cookie, ct).ConfigureAwait(false);
        string? effectiveChannel = !string.IsNullOrWhiteSpace(appSettings.Channel) &&
                                   !string.Equals(appSettings.Channel, "production", StringComparison.OrdinalIgnoreCase) &&
                                   !string.Equals(appSettings.Channel, "live", StringComparison.OrdinalIgnoreCase)
            ? appSettings.Channel.Trim()
            : null;

        var builder = new PlaceLauncherUriBuilder
        {
            PlaceId = resolution.PlaceId,
            JobId = resolution.JobId,
            LinkCode = resolution.LinkCode,
            UserId = resolution.FollowUserId,
            AuthTicket = authTicket,
            Channel = effectiveChannel,
            LaunchTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        string uri = builder.BuildProtocolUri();
        var launchSettings = new LaunchSettings([uri]);
        var result = await LaunchWithProgressInternalAsync(launchSettings, null, null, skipDeploymentCheck, account.Username, ct).ConfigureAwait(false);

        if (result.Success)
        {
            account.LastLaunchedAt = DateTimeOffset.UtcNow;
        }

        return result with { Target = resolution.Target };
    }

    private async Task<string?> EnsureDeployedAsync(
        LaunchSettings settings,
        bool isStudio,
        IProgress<PackageDownloadProgress>? progress,
        Action<string>? statusCallback,
        CancellationToken ct)
    {
        string exeName = isStudio ? "RobloxStudioBeta.exe" : "RobloxPlayerBeta.exe";
        string existingGuid = isStudio
            ? _robloxStateManager.Prop.Studio.VersionGuid
            : _robloxStateManager.Prop.Player.VersionGuid;

        bool force = settings.ForceFlag.Active || _stateManager.Prop.ForceReinstall;
        if (!force && !string.IsNullOrWhiteSpace(existingGuid))
        {
            string existingDir = _paths.GetVersionDirectory(existingGuid);
            string existingExe = Path.Combine(existingDir, exeName);
            if (settings.BypassUpdateCheck && File.Exists(existingExe))
            {
                return existingGuid;
            }

            if (!_settingsManager.Prop.CheckForUpdates && IsVersionInstallationComplete(existingDir, isStudio))
            {
                return existingGuid;
            }
        }

        if (settings.BypassUpdateCheck)
        {
            return existingGuid;
        }

        try
        {
            statusCallback?.Invoke(LocalizationService.Instance.GetString("Str_Bootstrapper_StatusFindingCdn"));
            var mirrorSelector = new CdnMirrorSelector(_httpClient);
            string cdnBaseUrl = await mirrorSelector.ResolveOptimalCdnUrlAsync(ct).ConfigureAwait(false);

            string channel = !string.IsNullOrWhiteSpace(settings.ChannelFlag.Data)
                ? settings.ChannelFlag.Data
                : _settingsManager.Prop.Channel;

            string binaryType = isStudio ? "WindowsStudio64" : "WindowsPlayer";
            string clientVersionUrl = $"https://clientsettings.roblox.com/v2/client-version/{binaryType}" +
                (!string.Equals(channel, "production", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(channel, "live", StringComparison.OrdinalIgnoreCase)
                    ? $"/channel/{Uri.EscapeDataString(channel)}"
                    : string.Empty);

            statusCallback?.Invoke(LocalizationService.Instance.Format("Str_Bootstrapper_StatusCheckingVersionFormat", binaryType));
            using var resp = await _httpClient.GetAsync(clientVersionUrl, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return existingGuid;
            }

            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            string latestGuid = doc.RootElement.GetProperty("clientVersionUpload").GetString() ?? string.Empty;
            string latestVersion = doc.RootElement.TryGetProperty("version", out var vProp) ? vProp.GetString() ?? string.Empty : string.Empty;

            if (string.IsNullOrWhiteSpace(latestGuid))
            {
                return existingGuid;
            }

            string targetVersionDir = _paths.GetVersionDirectory(latestGuid);
            string targetExe = Path.Combine(targetVersionDir, exeName);

            if (!force && string.Equals(existingGuid, latestGuid, StringComparison.OrdinalIgnoreCase) && IsVersionInstallationComplete(targetVersionDir, isStudio))
            {
                return latestGuid;
            }

            statusCallback?.Invoke(LocalizationService.Instance.Format("Str_Bootstrapper_StatusDownloadingManifestFormat", latestGuid));
            var manifestParser = new DeploymentManifestParser();
            var packages = await manifestParser.FetchAndParseAsync(cdnBaseUrl, latestGuid, _httpClient, channel, ct: ct).ConfigureAwait(false);

            statusCallback?.Invoke(LocalizationService.Instance.Format("Str_Bootstrapper_StatusDownloadingCountFormat", packages.Count));
            var downloader = new PackageDownloader();
            var downloadedPaths = await downloader.DownloadPackagesAsync(
                _httpClient,
                cdnBaseUrl,
                latestGuid,
                packages,
                _paths.Downloads,
                progress,
                5,
                ct).ConfigureAwait(false);

            statusCallback?.Invoke(LocalizationService.Instance.GetString("Str_Bootstrapper_StatusExtracting"));
            var extractor = new PackageExtractor();
            Directory.CreateDirectory(targetVersionDir);

            for (int i = 0; i < packages.Count && i < downloadedPaths.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                string pkgPath = downloadedPaths[i];
                if (string.IsNullOrWhiteSpace(pkgPath) || !File.Exists(pkgPath)) continue;

                string pkgName = Path.GetFileName(pkgPath);
                string relSubdir = PackageExtractor.PackageDirectoryMap.TryGetValue(pkgName, out var mapped)
                    ? mapped
                    : string.Empty;

                if (pkgName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        await extractor.ExtractPackageAsync(pkgPath, targetVersionDir, relSubdir, null, ct).ConfigureAwait(false);
                    }
                    catch (Exception pkgEx) when (pkgEx is not OperationCanceledException)
                    {
                        _logger.Warn("BootstrapperOrchestrator", $"Package extraction failed for '{pkgName}': {pkgEx.Message}");
                    }
                }
            }

            EnsureAppSettingsXml(targetVersionDir);

            if (!File.Exists(targetExe))
            {
                return existingGuid;
            }

            if (isStudio)
            {
                _robloxStateManager.Prop.Studio.VersionGuid = latestGuid;
                _robloxStateManager.Prop.Studio.Version = latestVersion;
            }
            else
            {
                _robloxStateManager.Prop.Player.VersionGuid = latestGuid;
                _robloxStateManager.Prop.Player.Version = latestVersion;
            }

            _robloxStateManager.Save();
            _stateManager.Prop.ForceReinstall = false;
            _stateManager.Save();
            return latestGuid;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn("BootstrapperOrchestrator", $"Update/deploy step fell back to local version: {ex.Message}");
            return existingGuid;
        }
    }

    private async Task ApplyFastFlagsAndModsAsync(Settings appSettings, string versionDir, CancellationToken ct)
    {
        try
        {
            if (appSettings.UseFastFlagManager)
            {
                string modClientSettings = _paths.GetClientSettingsPath();
                if (File.Exists(modClientSettings))
                {
                    string versionClientSettings = _paths.GetClientSettingsPath(Path.GetFileName(versionDir));
                    Directory.CreateDirectory(Path.GetDirectoryName(versionClientSettings)!);
                    string jsonContent = await File.ReadAllTextAsync(modClientSettings, System.Text.Encoding.UTF8, ct).ConfigureAwait(false);
                    await File.WriteAllTextAsync(versionClientSettings, jsonContent, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct).ConfigureAwait(false);
                }
            }

            var modEngine = new ModOverlayEngine();
            var modResult = await modEngine.ApplyModificationsAsync(
                _paths.Modifications,
                versionDir,
                _robloxStateManager.Prop.ModManifest,
                robloxDomain: appSettings.RobloxDomain,
                ct: ct).ConfigureAwait(false);

            if (modResult.Success)
            {
                _robloxStateManager.Prop.ModManifest = modResult.ManifestedFiles.ToList();
                _robloxStateManager.Save();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn("BootstrapperOrchestrator", $"Mod/FastFlag application error: {ex.Message}");
        }
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

    private string ResolveFallbackVersionDir(bool isStudio)
    {
        string exeName = isStudio ? "RobloxStudioBeta.exe" : "RobloxPlayerBeta.exe";
        string? incompleteFallback = null;

        foreach (var root in new[] { _paths.Versions, _paths.RobloxVersions })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.GetDirectories(root).OrderByDescending(Directory.GetLastWriteTimeUtc))
            {
                if (IsVersionInstallationComplete(dir, isStudio))
                    return dir;

                if (incompleteFallback == null && File.Exists(Path.Combine(dir, exeName)))
                    incompleteFallback = dir;
            }
        }

        return incompleteFallback ?? string.Empty;
    }

    private static bool IsVersionInstallationComplete(string versionDir, bool isStudio)
    {
        if (string.IsNullOrWhiteSpace(versionDir) || !Directory.Exists(versionDir))
            return false;

        string exeName = isStudio ? "RobloxStudioBeta.exe" : "RobloxPlayerBeta.exe";
        if (!File.Exists(Path.Combine(versionDir, exeName)))
            return false;

        if (!isStudio)
        {
            string appSettingsPath = Path.Combine(versionDir, "AppSettings.xml");
            if (!File.Exists(Path.Combine(versionDir, "WebView2Loader.dll")) ||
                !File.Exists(appSettingsPath) ||
                !File.Exists(Path.Combine(versionDir, "ssl", "cacert.pem")) ||
                !Directory.Exists(Path.Combine(versionDir, "PlatformContent", "pc", "textures")) ||
                !Directory.Exists(Path.Combine(versionDir, "PlatformContent", "pc", "terrain")) ||
                !Directory.Exists(Path.Combine(versionDir, "PlatformContent", "pc", "fonts")) ||
                !Directory.Exists(Path.Combine(versionDir, "content", "configs")) ||
                !Directory.Exists(Path.Combine(versionDir, "content", "fonts")) ||
                !Directory.Exists(Path.Combine(versionDir, "content", "sky")) ||
                !Directory.Exists(Path.Combine(versionDir, "content", "sounds")) ||
                !Directory.Exists(Path.Combine(versionDir, "content", "textures")) ||
                !Directory.Exists(Path.Combine(versionDir, "ExtraContent", "LuaPackages")) ||
                !Directory.Exists(Path.Combine(versionDir, "ExtraContent", "translations")) ||
                !Directory.Exists(Path.Combine(versionDir, "shaders")))
            {
                return false;
            }

            try
            {
                if (!File.ReadAllText(appSettingsPath).Contains("http://www.", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            catch
            {
                return false;
            }
        }

        return true;
    }

    private void TryRepairFromCachedDownloads(string versionDir, bool isStudio)
    {
        if (isStudio || !Directory.Exists(_paths.Downloads)) return;

        try
        {
            var extractor = new PackageExtractor();
            foreach (var (pkgName, relSubdir) in PackageExtractor.PackageDirectoryMap)
            {
                if (string.Equals(pkgName, "WebView2RuntimeInstaller.zip", StringComparison.OrdinalIgnoreCase))
                    continue;

                string cachedZip = Path.Combine(_paths.Downloads, pkgName);
                if (!File.Exists(cachedZip))
                    continue;

                try
                {
                    extractor.ExtractPackageAsync(cachedZip, versionDir, relSubdir, null, CancellationToken.None)
                        .GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    _logger.Warn("BootstrapperOrchestrator", $"Cached repair skipped '{pkgName}': {ex.Message}");
                }
            }

            string targetCacert = Path.Combine(versionDir, "ssl", "cacert.pem");
            string rootCacert = Path.Combine(versionDir, "cacert.pem");
            if (!File.Exists(targetCacert) && File.Exists(rootCacert))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(targetCacert)!);
                File.Copy(rootCacert, targetCacert, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            _logger.Warn("BootstrapperOrchestrator", $"Cached repair failed: {ex.Message}");
        }
    }

    private void EnsureAppSettingsXml(string versionDir)
    {
        try
        {
            ModOverlayEngine.EnsureAppSettingsXml(versionDir, _settingsManager.Prop.RobloxDomain);
        }
        catch (Exception ex)
        {
            _logger.Warn("BootstrapperOrchestrator", $"Failed to write AppSettings.xml: {ex.Message}");
        }
    }

    private void TryRecoverMissingWebView2Loader(string versionDir)
    {
        try
        {
            string targetDll = Path.Combine(versionDir, "WebView2Loader.dll");
            if (File.Exists(targetDll)) return;

            string webViewZip = Path.Combine(_paths.Downloads, "WebView2.zip");
            if (File.Exists(webViewZip))
            {
                var extractor = new PackageExtractor();
                extractor.ExtractZipFile(webViewZip, versionDir);
                if (File.Exists(targetDll)) return;
            }

            foreach (var root in new[] { _paths.Versions, _paths.RobloxVersions })
            {
                if (!Directory.Exists(root)) continue;
                foreach (var dir in Directory.GetDirectories(root).OrderByDescending(Directory.GetLastWriteTimeUtc))
                {
                    string candidateDll = Path.Combine(dir, "WebView2Loader.dll");
                    if (File.Exists(candidateDll))
                    {
                        File.Copy(candidateDll, targetDll, overwrite: true);
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warn("BootstrapperOrchestrator", $"Failed to recover WebView2Loader.dll: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

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
        _httpClient.Dispose();
    }
}
