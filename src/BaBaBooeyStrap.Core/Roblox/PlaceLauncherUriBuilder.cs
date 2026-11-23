using System.Text;

namespace BaBaBooeyStrap.Core.Roblox;

public enum PlaceLauncherRequestType
{
    RequestGame,
    RequestGameJob,
    RequestPrivateGame,
    RequestFollowUser
}

public sealed class PlaceLauncherUriBuilder
{
    public const int MaxLaunchUriLength = 16384;

    public long PlaceId { get; set; }
    public string? JobId { get; set; }
    public string? AccessCode { get; set; }
    public string? LinkCode { get; set; }
    public long? UserId { get; set; }
    public string? AuthTicket { get; set; }
    public string? LaunchData { get; set; }
    public string RobloxLocale { get; set; } = "en_us";
    public string GameLocale { get; set; } = "en_us";
    public string? BrowserTrackerId { get; set; }
    public string? Channel { get; set; }
    public long? LaunchTimeMs { get; set; }
    public string BasePlaceLauncherUrl { get; set; } = "https://assetgame.roblox.com/game/PlaceLauncher.ashx";

    public string BuildPlaceLauncherUrl()
    {
        if (!IsAllowedPlaceLauncherBaseUrl(BasePlaceLauncherUrl))
        {
            throw new InvalidOperationException($"Untrusted PlaceLauncher URL: '{BasePlaceLauncherUrl}'.");
        }

        var sb = new StringBuilder(BasePlaceLauncherUrl.Trim());
        sb.Append('?');

        if (!string.IsNullOrWhiteSpace(AccessCode))
        {
            sb.Append("request=RequestPrivateGame");
            sb.Append("&placeId=").Append(PlaceId);
            sb.Append("&accessCode=").Append(Uri.EscapeDataString(AccessCode));
        }
        else if (!string.IsNullOrWhiteSpace(LinkCode))
        {
            sb.Append("request=RequestPrivateGame");
            sb.Append("&placeId=").Append(PlaceId);
            sb.Append("&linkCode=").Append(Uri.EscapeDataString(LinkCode));
        }
        else if (!string.IsNullOrWhiteSpace(JobId))
        {
            sb.Append("request=RequestGameJob");
            sb.Append("&placeId=").Append(PlaceId);
            sb.Append("&gameId=").Append(Uri.EscapeDataString(JobId));
        }
        else if (UserId.HasValue && UserId.Value > 0)
        {
            sb.Append("request=RequestFollowUser");
            sb.Append("&userId=").Append(UserId.Value);
        }
        else
        {
            sb.Append("request=RequestGame");
            sb.Append("&placeId=").Append(PlaceId);
        }

        if (!string.IsNullOrWhiteSpace(LaunchData))
        {
            sb.Append("&launchData=").Append(Uri.EscapeDataString(LaunchData));
        }

        return sb.ToString();
    }

    public string BuildProtocolUri()
    {
        string robloxLocale = ValidateProtocolToken(RobloxLocale, nameof(RobloxLocale));
        string gameLocale = ValidateProtocolToken(GameLocale, nameof(GameLocale));

        var sb = new StringBuilder();
        sb.Append("roblox-player:1+launchmode:play");

        if (!string.IsNullOrEmpty(AuthTicket))
        {
            sb.Append("+gameinfo:").Append(Uri.EscapeDataString(AuthTicket));
        }

        long launchTime = LaunchTimeMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        sb.Append("+launchtime:").Append(launchTime);

        string innerUrl = BuildPlaceLauncherUrl();
        sb.Append("+placelauncherurl:").Append(Uri.EscapeDataString(innerUrl));

        sb.Append("+robloxLocale:").Append(robloxLocale);
        sb.Append("+gameLocale:").Append(gameLocale);

        if (!string.IsNullOrWhiteSpace(BrowserTrackerId))
        {
            sb.Append("+browserTrackerId:").Append(ValidateProtocolToken(BrowserTrackerId, nameof(BrowserTrackerId)));
        }

        if (!string.IsNullOrWhiteSpace(Channel))
        {
            sb.Append("+channel:").Append(ValidateProtocolToken(Channel, nameof(Channel)));
        }

        string uri = sb.ToString();

        if (uri.Length > MaxLaunchUriLength)
        {
            throw new InvalidOperationException($"Launch URI length ({uri.Length}) exceeds maximum allowed length ({MaxLaunchUriLength}).");
        }

        if (uri.Any(char.IsWhiteSpace) || uri.Any(char.IsControl) || uri.Contains('"'))
        {
            throw new InvalidOperationException("Launch URI must not contain whitespace, control characters, or double quotes.");
        }

        return uri;
    }

    internal static bool IsAllowedPlaceLauncherBaseUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return uri.Host.Equals("roblox.com", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.EndsWith(".roblox.com", StringComparison.OrdinalIgnoreCase);
    }

    private static string ValidateProtocolToken(string value, string paramName)
    {
        string trimmed = value.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new InvalidOperationException($"Protocol token '{paramName}' cannot be empty.");
        }

        foreach (char ch in trimmed)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch) || ch is '"' or '+' or ':')
            {
                throw new InvalidOperationException($"Protocol token '{paramName}' contains invalid character '{ch}'.");
            }
        }

        return trimmed;
    }

    public static bool IsValidLaunchUri(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return false;
        if (!uri.StartsWith("roblox-player:1+", StringComparison.OrdinalIgnoreCase)) return false;
        if (uri.Length > MaxLaunchUriLength) return false;
        if (uri.Any(ch => char.IsWhiteSpace(ch) || char.IsControl(ch) || ch == '"')) return false;
        return true;
    }

    public static PlaceLauncherUriBuilder Parse(string uriOrCommandLine)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uriOrCommandLine);

        string target = uriOrCommandLine.Trim();
        int prefixIdx = target.IndexOf("roblox-player:1+", StringComparison.OrdinalIgnoreCase);
        if (prefixIdx >= 0)
        {
            target = target[prefixIdx..];
            int spaceIdx = target.IndexOf(' ');
            if (spaceIdx > 0)
            {
                target = target[..spaceIdx];
            }
            target = target.Trim('"', '\'');
        }

        var builder = new PlaceLauncherUriBuilder();
        string[] parts = target.Split('+');

        foreach (var part in parts)
        {
            int colonIdx = part.IndexOf(':');
            if (colonIdx <= 0) continue;

            string key = part[..colonIdx];
            string value = part[(colonIdx + 1)..];

            switch (key.ToLowerInvariant())
            {
                case "gameinfo":
                    builder.AuthTicket = Uri.UnescapeDataString(value);
                    break;
                case "launchtime":
                    if (long.TryParse(value, out long lt)) builder.LaunchTimeMs = lt;
                    break;
                case "placelauncherurl":
                    ParseInnerUrl(Uri.UnescapeDataString(value), builder);
                    break;
                case "robloxlocale":
                    builder.RobloxLocale = value;
                    break;
                case "gamelocale":
                    builder.GameLocale = value;
                    break;
                case "browsertrackerid":
                    builder.BrowserTrackerId = value;
                    break;
                case "channel":
                    builder.Channel = value;
                    break;
            }
        }

        return builder;
    }

    private static void ParseInnerUrl(string innerUrl, PlaceLauncherUriBuilder builder)
    {
        int qIdx = innerUrl.IndexOf('?');
        if (qIdx >= 0)
        {
            builder.BasePlaceLauncherUrl = innerUrl[..qIdx];
            string queryString = innerUrl[(qIdx + 1)..];
            var queryParams = ParseQueryString(queryString);

            if (queryParams.TryGetValue("placeId", out var placeIdStr) && long.TryParse(placeIdStr, out long pid))
            {
                builder.PlaceId = pid;
            }
            if (queryParams.TryGetValue("gameId", out var gameId))
            {
                builder.JobId = gameId;
            }
            if (queryParams.TryGetValue("accessCode", out var accessCode))
            {
                builder.AccessCode = accessCode;
            }
            if (queryParams.TryGetValue("linkCode", out var linkCode))
            {
                builder.LinkCode = linkCode;
            }
            if (queryParams.TryGetValue("userId", out var userIdStr) && long.TryParse(userIdStr, out long uid))
            {
                builder.UserId = uid;
            }
            if (queryParams.TryGetValue("launchData", out var launchData))
            {
                builder.LaunchData = launchData;
            }
        }
        else
        {
            builder.BasePlaceLauncherUrl = innerUrl;
        }
    }

    private static Dictionary<string, string> ParseQueryString(string queryString)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string[] pairs = queryString.Split('&');
        foreach (var pair in pairs)
        {
            int eqIdx = pair.IndexOf('=');
            if (eqIdx > 0)
            {
                string k = Uri.UnescapeDataString(pair[..eqIdx]);
                string v = Uri.UnescapeDataString(pair[(eqIdx + 1)..]);
                dict[k] = v;
            }
        }
        return dict;
    }
}
