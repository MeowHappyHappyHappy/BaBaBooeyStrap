namespace BaBaBooeyStrap.MultiInstance.Abstractions;

/// <summary>
/// Snapshot of the multi-instance controller state and running Roblox processes.
/// </summary>
public sealed record MultiInstanceStatus(
    bool Enabled,
    bool OwnsMutex,
    bool WaitingForMutex,
    IReadOnlyList<int> RunningRobloxPids,
    string? LastError
);
