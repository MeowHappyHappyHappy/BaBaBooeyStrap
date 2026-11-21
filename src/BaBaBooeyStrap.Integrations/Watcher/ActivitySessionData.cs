namespace BaBaBooeyStrap.Integrations.Watcher;

public enum ServerType
{
    Public,
    Private,
    Reserved
}

public sealed class ActivityWatcherOptions
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(25);
    public TimeSpan RpcRateLimit { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan DirectoryScanWindow { get; init; } = TimeSpan.FromSeconds(15);
    public Func<string, bool>? ExcludedLogFilePredicate { get; init; }
    public bool LockToFirstSelectedLogFile { get; init; } = false;
}

public sealed class ActivitySessionData
{
    public string PlaceId { get; set; } = string.Empty;
    public string? UniverseId { get; set; }
    public string? UserId { get; set; }
    public string JobId { get; set; } = string.Empty;
    public string ServerIp { get; set; } = string.Empty;
    public int ServerPort { get; set; }
    public bool IsUdmux { get; set; }
    public bool IsTeleport { get; set; }
    public ServerType ServerType { get; set; } = ServerType.Public;
    public string? AccessCode { get; set; }
    public DateTime TimeJoined { get; set; }
    public DateTime? TimeLeft { get; set; }
    public DateTime? ServerStartTime { get; set; }
    public string? RpcLaunchData { get; set; }

    public string GetDeepLinkUrl(string baseUrl = "roblox://experiences/start")
    {
        if (string.IsNullOrWhiteSpace(PlaceId))
        {
            return string.Empty;
        }

        var url = $"{baseUrl}?placeId={Uri.EscapeDataString(PlaceId)}";
        if (ServerType == ServerType.Private && !string.IsNullOrWhiteSpace(AccessCode))
        {
            url += $"&accessCode={Uri.EscapeDataString(AccessCode)}";
        }
        else if (!string.IsNullOrWhiteSpace(JobId))
        {
            url += $"&gameInstanceId={Uri.EscapeDataString(JobId)}";
        }

        if (!string.IsNullOrWhiteSpace(RpcLaunchData))
        {
            url += $"&launchData={Uri.EscapeDataString(RpcLaunchData)}";
        }

        return url;
    }
}
