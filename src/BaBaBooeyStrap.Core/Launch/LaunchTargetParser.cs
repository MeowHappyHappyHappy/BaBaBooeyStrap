using System.Collections.Specialized;
using System.Text.RegularExpressions;
using System.Web;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Models.Enums;
using BaBaBooeyStrap.Core.Roblox;

namespace BaBaBooeyStrap.Core.Launch;

internal static partial class LaunchTargetParser
{
    [GeneratedRegex(@"/games/(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GamesPathRegex();

    [GeneratedRegex(@"/users/(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UsersPathRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_]{3,20}$", RegexOptions.CultureInvariant)]
    private static partial Regex UsernameRegex();

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
