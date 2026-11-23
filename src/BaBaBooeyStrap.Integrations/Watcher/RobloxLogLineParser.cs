using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BaBaBooeyStrap.Integrations.Watcher;

internal static partial class RobloxLogLineParser
{
    private const int MaxRpcJsonBytes = 16 * 1024;

    private static readonly JsonSerializerOptions RpcJsonOptions = new()
    {
        MaxDepth = 8
    };

    private static readonly HashSet<string> AllowedRpcCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "SetRichPresence",
        "SetLaunchData",
        "SetDetails"
    };

    [GeneratedRegex(@"^(?<ts>\d{4}-\d{2}-\d{2}T[\d:.]+Z),", RegexOptions.Compiled, 100)]
    private static partial Regex HeaderTimestampRegex();

    [GeneratedRegex(@"!\s*Joining game '(?<jobId>[0-9a-fA-F\-]{36}|[^']+)'\s+place\s+(?<placeId>\d+)\s+at\s+(?<serverIp>[0-9.]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase, 100)]
    private static partial Regex JoinGameRegex();

    [GeneratedRegex(@"universeid:(?<universeId>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase, 100)]
    private static partial Regex UniverseIdRegex();

    [GeneratedRegex(@"userid:(?<userId>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase, 100)]
    private static partial Regex UserIdRegex();

    [GeneratedRegex(@"referral_page:(?<referral>[^,\s]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase, 100)]
    private static partial Regex ReferralRegex();

    [GeneratedRegex(@"JoinTypeId(?:%22|"")?(?:%3a|:)(?<joinTypeId>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase, 100)]
    private static partial Regex TeleportJoinTypeRegex();

    [GeneratedRegex(@"UDMUX Address\s*=\s*(?<udmuxIp>[0-9.]+),\s*Port\s*=\s*(?<udmuxPort>\d+)(?:\s*\|\s*RCC Server Address\s*=\s*(?<rccIp>[0-9.]+),\s*Port\s*=\s*(?<rccPort>\d+))?", RegexOptions.Compiled | RegexOptions.IgnoreCase, 100)]
    private static partial Regex UdmuxRegex();

    [GeneratedRegex(@"serverId:\s*(?<serverIp>[0-9.]+)\|(?<serverPort>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase, 100)]
    private static partial Regex ServerIdRegex();

    [GeneratedRegex(@"Server Prefix:.+_(?<timestamp>\d{8}T\d{6}Z)_RCC_[0-9a-zA-Z]+", RegexOptions.Compiled, 100)]
    private static partial Regex ServerPrefixRegex();

    [GeneratedRegex(@"\[BloxstrapRPC\]\s*(?<json>\{.*\})\s*$", RegexOptions.Compiled, 100)]
    private static partial Regex BloxstrapRpcRegex();

    [GeneratedRegex(@"Time to disconnect replication data:(?:\s*(?<val>[\d.]+))?", RegexOptions.Compiled | RegexOptions.IgnoreCase, 100)]
    private static partial Regex DisconnectRegex();

    private static bool IsUserOutputSpoofed(string line) =>
        line.Contains("[FLog::Output]", StringComparison.OrdinalIgnoreCase);

    private static Match SafeMatch(Regex regex, string line)
    {
        try
        {
            return regex.Match(line);
        }
        catch (RegexMatchTimeoutException)
        {
            return Match.Empty;
        }
    }

    public static DateTime ParseTimestamp(string line)
    {
        var match = SafeMatch(HeaderTimestampRegex(), line);
        if (match.Success &&
            DateTime.TryParse(
                match.Groups["ts"].Value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTime parsed))
        {
            return parsed;
        }

        return DateTime.UtcNow;
    }

    public static bool TryParseTeleport(string line, out bool isReserved)
    {
        isReserved = false;
        if (IsUserOutputSpoofed(line) ||
            !line.Contains("doTeleport: joinScriptUrl", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var joinTypeMatch = SafeMatch(TeleportJoinTypeRegex(), line);
        if (joinTypeMatch.Success &&
            int.TryParse(joinTypeMatch.Groups["joinTypeId"].Value, out int joinTypeId) &&
            (joinTypeId == 4 || joinTypeId == 6))
        {
            isReserved = true;
        }

        return true;
    }

    public static bool TryParseJoinGame(string line, out string jobId, out string placeId, out string serverIp)
    {
        if (!line.Contains("Joining game '", StringComparison.OrdinalIgnoreCase))
        {
            jobId = string.Empty;
            placeId = string.Empty;
            serverIp = string.Empty;
            return false;
        }

        var match = SafeMatch(JoinGameRegex(), line);
        string parsedIp = match.Success ? match.Groups["serverIp"].Value : string.Empty;
        if (!match.Success || !IPAddress.TryParse(parsedIp, out _))
        {
            jobId = string.Empty;
            placeId = string.Empty;
            serverIp = string.Empty;
            return false;
        }

        jobId = match.Groups["jobId"].Value;
        placeId = match.Groups["placeId"].Value;
        serverIp = parsedIp;
        return true;
    }

    public static bool TryParseJoinMetadata(
        string line,
        out string? universeId,
        out string? userId,
        out bool isPrivateServer)
    {
        universeId = null;
        userId = null;
        isPrivateServer = false;

        if (!line.Contains("game_join_loadtime", StringComparison.OrdinalIgnoreCase) &&
            !line.Contains("universeid:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var univMatch = SafeMatch(UniverseIdRegex(), line);
        if (univMatch.Success)
        {
            universeId = univMatch.Groups["universeId"].Value;
        }

        var userMatch = SafeMatch(UserIdRegex(), line);
        if (userMatch.Success)
        {
            userId = userMatch.Groups["userId"].Value;
        }

        var refMatch = SafeMatch(ReferralRegex(), line);
        if (refMatch.Success)
        {
            string referral = refMatch.Groups["referral"].Value;
            if (referral.Contains("RequestPrivateGame", StringComparison.OrdinalIgnoreCase) ||
                referral.Contains("GameDetailPageJSHybridEvent", StringComparison.OrdinalIgnoreCase))
            {
                isPrivateServer = true;
            }
        }

        return true;
    }

    public static bool TryParseUdmux(string line, out string rccIp, out string udmuxIp, out int? udmuxPort)
    {
        if (!line.Contains("UDMUX Address", StringComparison.OrdinalIgnoreCase))
        {
            rccIp = string.Empty;
            udmuxIp = string.Empty;
            udmuxPort = null;
            return false;
        }

        var match = SafeMatch(UdmuxRegex(), line);
        string parsedUdmuxIp = match.Success ? match.Groups["udmuxIp"].Value : string.Empty;
        string parsedRccIp = match.Success ? match.Groups["rccIp"].Value : string.Empty;
        if (!match.Success ||
            !IPAddress.TryParse(parsedUdmuxIp, out _) ||
            (!string.IsNullOrEmpty(parsedRccIp) && !IPAddress.TryParse(parsedRccIp, out _)))
        {
            rccIp = string.Empty;
            udmuxIp = string.Empty;
            udmuxPort = null;
            return false;
        }

        rccIp = parsedRccIp;
        udmuxIp = parsedUdmuxIp;
        udmuxPort = int.TryParse(match.Groups["udmuxPort"].Value, out int parsedPort) ? parsedPort : null;
        return true;
    }

    public static bool TryParseServerId(string line, out string serverIp, out int? serverPort)
    {
        if (IsUserOutputSpoofed(line) ||
            !line.Contains("serverId:", StringComparison.OrdinalIgnoreCase))
        {
            serverIp = string.Empty;
            serverPort = null;
            return false;
        }

        var match = SafeMatch(ServerIdRegex(), line);
        string parsedIp = match.Success ? match.Groups["serverIp"].Value : string.Empty;
        if (!match.Success || !IPAddress.TryParse(parsedIp, out _))
        {
            serverIp = string.Empty;
            serverPort = null;
            return false;
        }

        serverIp = parsedIp;
        serverPort = int.TryParse(match.Groups["serverPort"].Value, out int parsedPort) ? parsedPort : null;
        return true;
    }

    public static bool TryParseServerPrefixTimestamp(string line, out DateTime serverStart)
    {
        if (!IsUserOutputSpoofed(line) &&
            line.Contains("Server Prefix:", StringComparison.Ordinal))
        {
            var match = SafeMatch(ServerPrefixRegex(), line);
            if (match.Success &&
                DateTime.TryParseExact(
                    match.Groups["timestamp"].Value,
                    "yyyyMMdd'T'HHmmss'Z'",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out serverStart))
            {
                return true;
            }
        }

        serverStart = default;
        return false;
    }

    public static bool TryExtractBloxstrapRpcJson(string line, out string rawJson)
    {
        if (!line.Contains("[BloxstrapRPC]", StringComparison.Ordinal))
        {
            rawJson = string.Empty;
            return false;
        }

        var match = SafeMatch(BloxstrapRpcRegex(), line);
        if (!match.Success)
        {
            rawJson = string.Empty;
            return false;
        }

        rawJson = match.Groups["json"].Value;
        return true;
    }

    public static bool TryParseBloxstrapRpcMessage(
        string rawJson,
        out BloxstrapRpcMessage? parsed,
        out string? parseError)
    {
        if (string.IsNullOrWhiteSpace(rawJson) || Encoding.UTF8.GetByteCount(rawJson) > MaxRpcJsonBytes)
        {
            parsed = null;
            parseError = "RPC payload is empty or exceeds maximum allowed size.";
            return false;
        }

        try
        {
            parsed = JsonSerializer.Deserialize<BloxstrapRpcMessage>(rawJson, RpcJsonOptions);
            parseError = null;
            if (parsed == null || string.IsNullOrWhiteSpace(parsed.Command))
            {
                return false;
            }

            if (!AllowedRpcCommands.Contains(parsed.Command))
            {
                parsed = null;
                parseError = "Unsupported BloxstrapRPC command.";
                return false;
            }

            return true;
        }
        catch (JsonException ex)
        {
            parsed = null;
            parseError = ex.Message;
            return false;
        }
    }

    public static bool IsDisconnectLine(string line) =>
        !IsUserOutputSpoofed(line) &&
        line.Contains("Time to disconnect replication data:", StringComparison.OrdinalIgnoreCase) &&
        SafeMatch(DisconnectRegex(), line).Success;

    public static bool IsAppCloseLine(string line) =>
        !IsUserOutputSpoofed(line) &&
        line.Contains("leaveUGCGameInternal", StringComparison.OrdinalIgnoreCase);
}
