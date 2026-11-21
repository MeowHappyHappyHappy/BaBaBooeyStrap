namespace BaBaBooeyStrap.Integrations.Watcher;

public record GameJoinEventArgs(
    string PlaceId,
    string? UniverseId,
    string JobId,
    string ServerIp,
    int ServerPort,
    bool IsUdmux,
    DateTime Timestamp
);

public record GameLeaveEventArgs(
    DateTime Timestamp
);

public record BloxstrapRpcEventArgs(
    string Command,
    string RawJson,
    DateTime Timestamp
);

public interface IActivityWatcher : IDisposable
{
    event EventHandler<GameJoinEventArgs> GameJoined;
    event EventHandler<GameLeaveEventArgs> GameLeft;
    event EventHandler<BloxstrapRpcEventArgs> BloxstrapRpcReceived;

    bool IsWatching { get; }
    void StartWatching(string logDirectoryOrFilePath);
    void StopWatching();
}
