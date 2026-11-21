using System.Diagnostics;
using BaBaBooeyStrap.Core.Logging;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Models.Enums;
using BaBaBooeyStrap.Core.Roblox;
using BaBaBooeyStrap.Core.Storage;
using Microsoft.Win32;

namespace BaBaBooeyStrap.Core.Launch;

public class RobloxLauncher : IRobloxLauncher
{
    private readonly IPaths _paths;
    private readonly IRobloxAuthService _authService;
    private readonly IJsonManager<RobloxState> _robloxStateManager;
    private readonly ILogger _logger;
    private readonly Func<ProcessStartInfo, Process?> _processStarter;
    private readonly Func<LaunchSettings, CancellationToken, Task>? _preLaunchHook;

    public RobloxLauncher(
        IPaths? paths = null,
        IRobloxAuthService? authService = null,
        IJsonManager<RobloxState>? robloxStateManager = null,
        ILogger? logger = null,
        Func<ProcessStartInfo, Process?>? processStarter = null,
        Func<LaunchSettings, CancellationToken, Task>? preLaunchHook = null)
    {
        _paths = paths ?? Paths.Current;
        _authService = authService ?? new RobloxAuthService();
        _robloxStateManager = robloxStateManager ?? new JsonManager<RobloxState>(_paths.RobloxStateFile);
        _logger = logger ?? Logger.Current;
        _processStarter = processStarter ?? Process.Start;
        _preLaunchHook = preLaunchHook;
    }

    public virtual async Task<LaunchResult> LaunchPlayerAsync(LaunchSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ct.ThrowIfCancellationRequested();

        if (_preLaunchHook != null)
        {
            await _preLaunchHook(settings, ct).ConfigureAwait(false);
        }

        string normalizedArgs = NormalizeLaunchArgs(settings.RobloxLaunchArgs);
        LaunchTarget parsedTarget = ParseTargetFromUri(normalizedArgs);

        if (settings.NoLaunchFlag.Active)
        {
            _logger.Info("RobloxLauncher", "NoLaunch flag active; skipping process spawn.");
            return new LaunchResult(true, normalizedArgs, parsedTarget, DateTimeOffset.UtcNow, null, null);
        }

        bool isStudio = settings.RobloxLaunchMode is LaunchMode.Studio or LaunchMode.StudioAuth;
        string? exePath = ResolveExecutablePath(isStudio, settings.VersionFlag.Data);

        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            string missingTarget = isStudio ? "RobloxStudioBeta.exe" : "RobloxPlayerBeta.exe";
            return new LaunchResult(
                false,
                normalizedArgs,
                parsedTarget,
                DateTimeOffset.UtcNow,
                null,
                $"{missingTarget} could not be located in installed versions.");
        }

        string arguments = BuildProcessArguments(normalizedArgs, settings);
        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = arguments,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? _paths.Base,
            UseShellExecute = false
        };

        try
        {
            using var proc = _processStarter(startInfo);
            int? pid = proc?.Id;
            _logger.Info("RobloxLauncher", $"Launched {Path.GetFileName(exePath)} (PID={pid?.ToString() ?? "unknown"}).");
            return new LaunchResult(true, normalizedArgs, parsedTarget, DateTimeOffset.UtcNow, pid, null);
        }
        catch (Exception ex)
        {
            _logger.Exception("RobloxLauncher", ex);
            return new LaunchResult(false, normalizedArgs, parsedTarget, DateTimeOffset.UtcNow, null, ex.Message);
        }
    }

    public sealed record ResolvedLaunchTargetDetails(
        bool Success,
        LaunchTarget Target,
        long PlaceId,
        string? JobId,
        string? LinkCode,
        long? FollowUserId,
        string? NotInGameTargetName,
        string? ErrorMessage
    );

    public async Task<ResolvedLaunchTargetDetails> ResolveAccountLaunchTargetAsync(
        string cookie,
        LaunchTarget target,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cookie);
        ArgumentNullException.ThrowIfNull(target);

        long resolvedPlaceId = target.PlaceId ?? 0;
        string? resolvedJobId = target.JobId;
        string? resolvedLinkCode = target.LinkCode;
        long? resolvedFollowUserId = null;
        LaunchTarget resolvedTarget = target;

        if (target.Kind == LaunchTargetKind.ShareLink && !string.IsNullOrWhiteSpace(target.ShareCode))
        {
            var resolved = await _authService.ResolveShareLinkAsync(cookie, target.ShareCode, ct).ConfigureAwait(false);
            resolvedPlaceId = resolved.PlaceId;
            resolvedLinkCode = resolved.LinkCode;
            resolvedTarget = target with { PlaceId = resolvedPlaceId, LinkCode = resolvedLinkCode };
        }
        else if (target.Kind == LaunchTargetKind.FollowUser)
        {
            long targetUserId;
            if (target.UserId is > 0)
            {
                targetUserId = target.UserId.Value;
            }
            else if (!string.IsNullOrWhiteSpace(target.Username))
            {
                targetUserId = await _authService.ResolveUserIdByUsernameAsync(target.Username, ct).ConfigureAwait(false);
            }
            else
            {
                return new ResolvedLaunchTargetDetails(
                    false,
                    resolvedTarget,
                    0,
                    null,
                    null,
                    null,
                    null,
                    "A valid target UserId or Username is required.");
            }

            string targetLabel = !string.IsNullOrWhiteSpace(target.Username)
                ? target.Username
                : targetUserId.ToString();

            var presence = await _authService.GetUserPresenceAsync(cookie, targetUserId, ct).ConfigureAwait(false);
            if (!presence.IsInGame)
            {
                resolvedTarget = target with { UserId = targetUserId };
                return new ResolvedLaunchTargetDetails(
                    false,
                    resolvedTarget,
                    0,
                    null,
                    null,
                    targetUserId,
                    targetLabel,
                    $"Target user ({targetLabel}) is not currently in a game.");
            }

            long effectivePlaceId = presence.PlaceId ?? presence.RootPlaceId ?? 0;
            if (effectivePlaceId > 0 && !string.IsNullOrWhiteSpace(presence.JobId))
            {
                resolvedPlaceId = effectivePlaceId;
                resolvedJobId = presence.JobId;
                resolvedFollowUserId = null;
                resolvedTarget = target with
                {
                    PlaceId = effectivePlaceId,
                    JobId = presence.JobId,
                    UserId = targetUserId
                };
            }
            else
            {
                resolvedPlaceId = effectivePlaceId;
                resolvedJobId = null;
                resolvedFollowUserId = targetUserId;
                resolvedTarget = target with
                {
                    PlaceId = effectivePlaceId > 0 ? effectivePlaceId : null,
                    UserId = targetUserId
                };
            }
        }

        if (resolvedPlaceId <= 0 && string.IsNullOrWhiteSpace(resolvedJobId) && resolvedFollowUserId is not > 0)
        {
            return new ResolvedLaunchTargetDetails(
                false,
                resolvedTarget,
                resolvedPlaceId,
                resolvedJobId,
                resolvedLinkCode,
                resolvedFollowUserId,
                null,
                "A valid PlaceId is required to launch.");
        }

        return new ResolvedLaunchTargetDetails(
            true,
            resolvedTarget,
            resolvedPlaceId,
            resolvedJobId,
            resolvedLinkCode,
            resolvedFollowUserId,
            null,
            null);
    }

    public virtual async Task<LaunchResult> LaunchAccountAsync(RobloxAccount account, LaunchTarget target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(target);
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(account.Cookie))
        {
            return new LaunchResult(false, string.Empty, target, DateTimeOffset.UtcNow, null, "Account cookie is empty.");
        }

        var resolution = await ResolveAccountLaunchTargetAsync(account.Cookie, target, ct).ConfigureAwait(false);
        if (!resolution.Success)
        {
            return new LaunchResult(false, string.Empty, resolution.Target, DateTimeOffset.UtcNow, null, resolution.ErrorMessage);
        }

        string authTicket = await _authService.GenerateAuthTicketAsync(account.Cookie, ct).ConfigureAwait(false);

        var uriBuilder = new PlaceLauncherUriBuilder
        {
            PlaceId = resolution.PlaceId,
            JobId = resolution.JobId,
            LinkCode = resolution.LinkCode,
            UserId = resolution.FollowUserId,
            AuthTicket = authTicket,
            LaunchTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        string protocolUri = uriBuilder.BuildProtocolUri();
        var launchSettings = new LaunchSettings([protocolUri]);
        var result = await LaunchPlayerAsync(launchSettings, ct).ConfigureAwait(false);

        if (result.Success)
        {
            account.LastLaunchedAt = DateTimeOffset.UtcNow;
        }

        return result with { Target = resolution.Target };
    }

    public string? ResolveExecutablePath(bool isStudio, string? preferredVersionGuid = null)
    {
        string exeName = isStudio ? "RobloxStudioBeta.exe" : "RobloxPlayerBeta.exe";

        if (!string.IsNullOrWhiteSpace(preferredVersionGuid))
        {
            string preferred = Path.Combine(_paths.GetVersionDirectory(preferredVersionGuid), exeName);
            if (File.Exists(preferred)) return preferred;
        }

        string? incompleteFallback = null;

        try
        {
            if (!_robloxStateManager.Loaded && File.Exists(_robloxStateManager.FileLocation))
            {
                _robloxStateManager.Load(alertFailure: false);
            }

            string guid = isStudio
                ? _robloxStateManager.Prop.Studio.VersionGuid
                : _robloxStateManager.Prop.Player.VersionGuid;

            if (!string.IsNullOrWhiteSpace(guid))
            {
                string versionDir = _paths.GetVersionDirectory(guid);
                string candidate = Path.Combine(versionDir, exeName);
                if (File.Exists(candidate))
                {
                    if (isStudio || File.Exists(Path.Combine(versionDir, "WebView2Loader.dll")))
                        return candidate;

                    incompleteFallback = candidate;
                }
            }
        }
        catch { }

        foreach (var root in new[] { _paths.Versions, _paths.RobloxVersions })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.GetDirectories(root).OrderByDescending(Directory.GetLastWriteTimeUtc))
            {
                string candidate = Path.Combine(dir, exeName);
                if (!File.Exists(candidate)) continue;

                if (isStudio || File.Exists(Path.Combine(dir, "WebView2Loader.dll")))
                    return candidate;

                incompleteFallback ??= candidate;
            }
        }

        return incompleteFallback;
    }

    public static string NormalizeLaunchArgs(string? rawArgs) =>
        LaunchTargetParser.NormalizeLaunchArgs(rawArgs);

    public static LaunchTarget ParseTargetFromUri(string launchUri) =>
        LaunchTargetParser.ParseTargetFromUri(launchUri);

    /// <summary>
    /// Parses a raw PlaceId, standard Roblox game URL, VIP <c>privateServerLinkCode=</c> URL, <c>/share?code=...&amp;type=Server</c> URL, or Follow User target.
    /// </summary>
    public static LaunchTarget ParseTargetInput(string input) =>
        LaunchTargetParser.ParseTargetInput(input, followUserMode: false);

    /// <summary>
    /// Parses a target input, optionally interpreting plain numeric IDs and bare usernames as Follow User targets when <paramref name="followUserMode"/> is true.
    /// </summary>
    public static LaunchTarget ParseTargetInput(string input, bool followUserMode) =>
        LaunchTargetParser.ParseTargetInput(input, followUserMode);

    /// <summary>
    /// Combines a raw target input with an optional JobId for standard PlaceId/game URL launches.
    /// </summary>
    public static string BuildEffectiveTargetInput(string rawTarget, string? optionalJobId) =>
        LaunchTargetParser.BuildEffectiveTargetInput(rawTarget, optionalJobId, followUserMode: false);

    /// <summary>
    /// Combines a raw target input with an optional JobId or normalizes a Follow User target.
    /// </summary>
    public static string BuildEffectiveTargetInput(string rawTarget, string? optionalJobId, bool followUserMode) =>
        LaunchTargetParser.BuildEffectiveTargetInput(rawTarget, optionalJobId, followUserMode);

    /// <summary>
    /// Checks Windows registry <c>HKCR\roblox</c> or <c>HKCR\roblox-player</c> (and HKCU fallback) to verify protocol registration.
    /// </summary>
    public static bool IsRobloxProtocolRegistered()
    {
        try
        {
            using var robloxKey = Registry.ClassesRoot.OpenSubKey("roblox");
            if (robloxKey is not null)
            {
                return true;
            }

            using var playerKey = Registry.ClassesRoot.OpenSubKey("roblox-player");
            if (playerKey is not null)
            {
                return true;
            }

            using var hkcuRobloxKey = Registry.CurrentUser.OpenSubKey(@"Software\Classes\roblox");
            if (hkcuRobloxKey is not null)
            {
                return true;
            }

            using var hkcuPlayerKey = Registry.CurrentUser.OpenSubKey(@"Software\Classes\roblox-player");
            return hkcuPlayerKey is not null;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildProcessArguments(string normalizedArgs, LaunchSettings settings)
    {
        if (string.IsNullOrWhiteSpace(normalizedArgs))
        {
            return settings.RobloxLaunchMode == LaunchMode.Studio ? string.Empty : "--app";
        }

        if (normalizedArgs.StartsWith("roblox-player:", StringComparison.OrdinalIgnoreCase) ||
            normalizedArgs.StartsWith("roblox-studio:", StringComparison.OrdinalIgnoreCase) ||
            normalizedArgs.StartsWith("roblox-studio-auth:", StringComparison.OrdinalIgnoreCase))
        {
            return $"\"{normalizedArgs}\"";
        }

        return normalizedArgs;
    }
}
