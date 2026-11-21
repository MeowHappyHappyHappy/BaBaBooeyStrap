using System.Collections.Specialized;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Web;
using BaBaBooeyStrap.Core.Logging;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Models.Enums;
using BaBaBooeyStrap.Core.Roblox;
using BaBaBooeyStrap.Core.Storage;
using Microsoft.Win32;

namespace BaBaBooeyStrap.Core.Launch;

public partial class RobloxLauncher : IRobloxLauncher
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

    public static string NormalizeLaunchArgs(string? rawArgs)
    {
        if (string.IsNullOrWhiteSpace(rawArgs)) return string.Empty;
        string trimmed = rawArgs.Trim();

        if (trimmed.StartsWith("roblox://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var uri = new Uri(trimmed);
                var query = ParseSimpleQuery(uri.Query);
                if (query.TryGetValue("placeId", out var pidStr) && long.TryParse(pidStr, out long placeId) && placeId > 0)
                {
                    var builder = new PlaceLauncherUriBuilder { PlaceId = placeId };
                    if (query.TryGetValue("gameInstanceId", out var jobId)) builder.JobId = jobId;
                    if (query.TryGetValue("linkCode", out var linkCode)) builder.LinkCode = linkCode;
                    if (query.TryGetValue("accessCode", out var accessCode)) builder.AccessCode = accessCode;
                    if (query.TryGetValue("launchData", out var launchData)) builder.LaunchData = launchData;
                    return builder.BuildProtocolUri();
                }
            }
            catch { }
        }

        return trimmed;
    }

    public static LaunchTarget ParseTargetFromUri(string launchUri)
    {
        if (string.IsNullOrWhiteSpace(launchUri))
        {
            return new LaunchTarget(LaunchTargetKind.Standard, null);
        }

        if (launchUri.StartsWith("roblox-player:1+", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var parsed = PlaceLauncherUriBuilder.Parse(launchUri);
                if (parsed.UserId is > 0)
                {
                    return new LaunchTarget(
                        LaunchTargetKind.FollowUser,
                        parsed.PlaceId > 0 ? parsed.PlaceId : null,
                        parsed.JobId,
                        parsed.LinkCode,
                        UserId: parsed.UserId);
                }

                var kind = !string.IsNullOrWhiteSpace(parsed.LinkCode) || !string.IsNullOrWhiteSpace(parsed.AccessCode)
                    ? LaunchTargetKind.PrivateServer
                    : LaunchTargetKind.Standard;
                return new LaunchTarget(kind, parsed.PlaceId > 0 ? parsed.PlaceId : null, parsed.JobId, parsed.LinkCode);
            }
            catch { }
        }

        return new LaunchTarget(LaunchTargetKind.Standard, null);
    }

    [GeneratedRegex(@"/games/(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GamesPathRegex();

    [GeneratedRegex(@"/users/(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UsersPathRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_]{3,20}$", RegexOptions.CultureInvariant)]
    private static partial Regex UsernameRegex();

    /// <summary>
    /// Parses a raw PlaceId, standard Roblox game URL, VIP <c>privateServerLinkCode=</c> URL, <c>/share?code=...&amp;type=Server</c> URL, or Follow User target.
    /// </summary>
    public static LaunchTarget ParseTargetInput(string input) => ParseTargetInput(input, followUserMode: false);

    /// <summary>
    /// Parses a target input, optionally interpreting plain numeric IDs and bare usernames as Follow User targets when <paramref name="followUserMode"/> is true.
    /// </summary>
    public static LaunchTarget ParseTargetInput(string input, bool followUserMode)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new ArgumentException("Target input cannot be empty.", nameof(input));
        }

        var trimmed = input.Trim();

        if (trimmed.StartsWith("user:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("follow:", StringComparison.OrdinalIgnoreCase))
        {
            int colonIdx = trimmed.IndexOf(':');
            string rest = trimmed[(colonIdx + 1)..].Trim();
            if (long.TryParse(rest, out var prefixedUid) && prefixedUid > 0)
            {
                return new LaunchTarget(LaunchTargetKind.FollowUser, null, UserId: prefixedUid);
            }

            string cleanUsername = rest.TrimStart('@').Trim();
            if (UsernameRegex().IsMatch(cleanUsername))
            {
                return new LaunchTarget(LaunchTargetKind.FollowUser, null, Username: cleanUsername);
            }

            throw new ArgumentException($"Invalid Roblox user target: '{input}'.", nameof(input));
        }

        if (trimmed.StartsWith('@'))
        {
            string uname = trimmed[1..].Trim();
            if (UsernameRegex().IsMatch(uname))
            {
                return new LaunchTarget(LaunchTargetKind.FollowUser, null, Username: uname);
            }

            throw new ArgumentException($"Invalid Roblox username: '{input}'.", nameof(input));
        }

        if (followUserMode)
        {
            if (long.TryParse(trimmed, out var rawUserId) && rawUserId > 0)
            {
                return new LaunchTarget(LaunchTargetKind.FollowUser, null, UserId: rawUserId);
            }

            if (!trimmed.Contains('/') && !trimmed.Contains(':') && UsernameRegex().IsMatch(trimmed))
            {
                return new LaunchTarget(LaunchTargetKind.FollowUser, null, Username: trimmed);
            }
        }
        else if (long.TryParse(trimmed, out var rawPlaceId) && rawPlaceId > 0)
        {
            return new LaunchTarget(LaunchTargetKind.Standard, rawPlaceId);
        }

        var normalizedInput = trimmed.StartsWith("/", StringComparison.Ordinal)
            ? $"https://www.roblox.com{trimmed}"
            : trimmed;

        if (!Uri.TryCreate(normalizedInput, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException($"Invalid Roblox target input: '{input}'.", nameof(input));
        }

        var userMatch = UsersPathRegex().Match(uri.AbsolutePath);
        if (userMatch.Success && long.TryParse(userMatch.Groups[1].Value, out var profileUserId) && profileUserId > 0)
        {
            return new LaunchTarget(LaunchTargetKind.FollowUser, null, UserId: profileUserId);
        }

        var query = HttpUtility.ParseQueryString(uri.Query);

        if (uri.AbsolutePath.Contains("/share", StringComparison.OrdinalIgnoreCase))
        {
            var shareCode = query["code"];
            if (string.IsNullOrWhiteSpace(shareCode))
            {
                throw new ArgumentException("Share link URL is missing the 'code' query parameter.", nameof(input));
            }

            var shareType = query["type"];
            if (!string.IsNullOrWhiteSpace(shareType) &&
                !string.Equals(shareType, "Server", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Unsupported share link type '{shareType}'. Expected 'Server'.", nameof(input));
            }

            return new LaunchTarget(LaunchTargetKind.ShareLink, PlaceId: null, ShareCode: shareCode.Trim());
        }

        var placeId = ExtractPlaceId(uri, query);
        if (placeId is null or <= 0)
        {
            throw new ArgumentException($"Could not extract a valid PlaceId from '{input}'.", nameof(input));
        }

        var linkCode = query["privateServerLinkCode"];
        if (!string.IsNullOrWhiteSpace(linkCode))
        {
            return new LaunchTarget(LaunchTargetKind.PrivateServer, placeId.Value, LinkCode: linkCode.Trim());
        }

        var jobId = query["gameInstanceId"] ?? query["jobId"] ?? query["gameId"];
        return new LaunchTarget(
            LaunchTargetKind.Standard,
            placeId.Value,
            JobId: string.IsNullOrWhiteSpace(jobId) ? null : jobId.Trim());
    }

    /// <summary>
    /// Combines a raw target input with an optional JobId for standard PlaceId/game URL launches.
    /// </summary>
    public static string BuildEffectiveTargetInput(string rawTarget, string? optionalJobId) =>
        BuildEffectiveTargetInput(rawTarget, optionalJobId, followUserMode: false);

    /// <summary>
    /// Combines a raw target input with an optional JobId or normalizes a Follow User target.
    /// </summary>
    public static string BuildEffectiveTargetInput(string rawTarget, string? optionalJobId, bool followUserMode)
    {
        if (followUserMode)
        {
            string trimmed = rawTarget?.Trim() ?? string.Empty;
            string effectiveRaw;
            if (long.TryParse(trimmed, out var uid) && uid > 0)
            {
                effectiveRaw = $"user:{uid}";
            }
            else if (!trimmed.StartsWith("user:", StringComparison.OrdinalIgnoreCase) &&
                     !trimmed.StartsWith("follow:", StringComparison.OrdinalIgnoreCase) &&
                     !trimmed.StartsWith('@') &&
                     !trimmed.Contains('/') &&
                     !trimmed.Contains(':') &&
                     UsernameRegex().IsMatch(trimmed))
            {
                effectiveRaw = $"@{trimmed}";
            }
            else
            {
                effectiveRaw = trimmed;
            }

            _ = ParseTargetInput(effectiveRaw, followUserMode: true);
            return effectiveRaw;
        }

        var parsed = ParseTargetInput(rawTarget, followUserMode: false);
        var trimmedJobId = optionalJobId?.Trim();

        if (!string.IsNullOrWhiteSpace(trimmedJobId) &&
            parsed.Kind == LaunchTargetKind.Standard &&
            parsed.PlaceId is > 0 &&
            string.IsNullOrWhiteSpace(parsed.JobId))
        {
            return $"https://www.roblox.com/games/{parsed.PlaceId.Value}?gameInstanceId={Uri.EscapeDataString(trimmedJobId)}";
        }

        return rawTarget.Trim();
    }

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

    private static long? ExtractPlaceId(Uri uri, NameValueCollection query)
    {
        var match = GamesPathRegex().Match(uri.AbsolutePath);
        if (match.Success && long.TryParse(match.Groups[1].Value, out var pathPlaceId))
        {
            return pathPlaceId;
        }

        var queryPlaceId = query["placeId"];
        if (!string.IsNullOrWhiteSpace(queryPlaceId) && long.TryParse(queryPlaceId, out var parsedQueryPlaceId))
        {
            return parsedQueryPlaceId;
        }

        return null;
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

    private static Dictionary<string, string> ParseSimpleQuery(string query)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(query)) return dict;
        string trimmed = query.TrimStart('?');
        foreach (var pair in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int idx = pair.IndexOf('=');
            if (idx > 0)
            {
                string key = Uri.UnescapeDataString(pair[..idx]);
                string val = Uri.UnescapeDataString(pair[(idx + 1)..]);
                dict[key] = val;
            }
        }
        return dict;
    }
}
