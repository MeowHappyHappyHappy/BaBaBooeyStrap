using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BaBaBooeyStrap.Integrations.Watcher;

internal static partial class RobloxLogLineParser
{
    [GeneratedRegex(@"^(?<ts>\d{4}-\d{2}-\d{2}T[\d:.]+Z),", RegexOptions.Compiled)]
    private static partial Regex HeaderTimestampRegex();

    [GeneratedRegex(@"!\s*Joining game '(?<jobId>[0-9a-fA-F\-]{36}|[^']+)'\s+place\s+(?<placeId>\d+)\s+at\s+(?<serverIp>[0-9.]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex JoinGameRegex();

    [GeneratedRegex(@"universeid:(?<universeId>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex UniverseIdRegex();

    [GeneratedRegex(@"userid:(?<userId>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex UserIdRegex();

    [GeneratedRegex(@"referral_page:(?<referral>[^,\s]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex ReferralRegex();

    [GeneratedRegex(@"JoinTypeId(?:%22|"")?(?:%3a|:)(?<joinTypeId>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex TeleportJoinTypeRegex();

    [GeneratedRegex(@"UDMUX Address\s*=\s*(?<udmuxIp>[0-9.]+),\s*Port\s*=\s*(?<udmuxPort>\d+)(?:\s*\|\s*RCC Server Address\s*=\s*(?<rccIp>[0-9.]+),\s*Port\s*=\s*(?<rccPort>\d+))?", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex UdmuxRegex();

    [GeneratedRegex(@"serverId:\s*(?<serverIp>[0-9.]+)\|(?<serverPort>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex ServerIdRegex();

    [GeneratedRegex(@"Server Prefix:.+_(?<timestamp>\d{8}T\d{6}Z)_RCC_[0-9a-zA-Z]+", RegexOptions.Compiled)]
    private static partial Regex ServerPrefixRegex();

    [GeneratedRegex(@"\[BloxstrapRPC\]\s*(?<json>\{.*\})\s*$", RegexOptions.Compiled)]
    private static partial Regex BloxstrapRpcRegex();

    [GeneratedRegex(@"Time to disconnect replication data:(?:\s*(?<val>[\d.]+))?", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex DisconnectRegex();

    public static DateTime ParseTimestamp(string line)
    {
        var match = HeaderTimestampRegex().Match(line);
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
        if (!line.Contains("doTeleport: joinScriptUrl", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var joinTypeMatch = TeleportJoinTypeRegex().Match(line);
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
        var match = JoinGameRegex().Match(line);
        if (!match.Success)
        {
            jobId = string.Empty;
            placeId = string.Empty;
            serverIp = string.Empty;
            return false;
        }

        jobId = match.Groups["jobId"].Value;
        placeId = match.Groups["placeId"].Value;
        serverIp = match.Groups["serverIp"].Value;
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

        var univMatch = UniverseIdRegex().Match(line);
        if (univMatch.Success)
        {
            universeId = univMatch.Groups["universeId"].Value;
        }

        var userMatch = UserIdRegex().Match(line);
        if (userMatch.Success)
        {
            userId = userMatch.Groups["userId"].Value;
        }

        var refMatch = ReferralRegex().Match(line);
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
        var match = UdmuxRegex().Match(line);
        if (!match.Success)
        {
            rccIp = string.Empty;
            udmuxIp = string.Empty;
            udmuxPort = null;
            return false;
        }

        rccIp = match.Groups["rccIp"].Value;
        udmuxIp = match.Groups["udmuxIp"].Value;
        udmuxPort = int.TryParse(match.Groups["udmuxPort"].Value, out int parsedPort) ? parsedPort : null;
        return true;
    }

    public static bool TryParseServerId(string line, out string serverIp, out int? serverPort)
    {
        var match = ServerIdRegex().Match(line);
        if (!match.Success)
        {
            serverIp = string.Empty;
            serverPort = null;
            return false;
        }

        serverIp = match.Groups["serverIp"].Value;
        serverPort = int.TryParse(match.Groups["serverPort"].Value, out int parsedPort) ? parsedPort : null;
        return true;
    }

    public static bool TryParseServerPrefixTimestamp(string line, out DateTime serverStart)
    {
        var match = ServerPrefixRegex().Match(line);
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

        serverStart = default;
        return false;
    }

    public static bool TryExtractBloxstrapRpcJson(string line, out string rawJson)
    {
        var match = BloxstrapRpcRegex().Match(line);
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
        try
        {
            parsed = JsonSerializer.Deserialize<BloxstrapRpcMessage>(rawJson);
            parseError = null;
            return parsed != null && !string.IsNullOrWhiteSpace(parsed.Command);
        }
        catch (JsonException ex)
        {
            parsed = null;
            parseError = ex.Message;
            return false;
        }
    }

    public static bool IsDisconnectLine(string line) => DisconnectRegex().IsMatch(line);

    public static bool IsAppCloseLine(string line) =>
        line.Contains("leaveUGCGameInternal", StringComparison.OrdinalIgnoreCase);
}
