namespace BaBaBooeyStrap.MultiInstance.Abstractions;

/// <summary>
/// Information about a detected client window.
/// </summary>
public sealed record ClientWindowInfo(
    int ProcessId,
    nint MainWindowHandle,
    bool IsVisible
);

/// <summary>
/// Outcome of probing for a newly launched client's visible main window.
/// </summary>
public sealed record ClientWindowWaitResult(
    bool WindowReady,
    int? ProcessId,
    nint MainWindowHandle,
    bool TimedOut
);

/// <summary>
/// Abstraction for tracking Roblox client processes, window readiness probing, and launch throttling.
/// </summary>
public interface IProcessSupervisor
{
    /// <summary>
    /// Gets a snapshot of currently tracked process IDs.
    /// </summary>
    IReadOnlyList<int> TrackedPids { get; }

    /// <summary>
    /// Registers a client process ID for tracking.
    /// </summary>
    void RegisterProcess(int pid);

    /// <summary>
    /// Unregisters a client process ID from tracking.
    /// </summary>
    bool UnregisterProcess(int pid);

    /// <summary>
    /// Clears all tracked process IDs.
    /// </summary>
    void ClearTrackedProcesses();

    /// <summary>
    /// Enumerates currently running RobloxPlayerBeta process IDs, sorted ascending.
    /// </summary>
    IReadOnlyList<int> GetRunningRobloxPids();

    /// <summary>
    /// Captures currently running RobloxPlayerBeta process IDs into a set.
    /// </summary>
    IReadOnlySet<int> CaptureRunningClientPids();

    /// <summary>
    /// Throttles successive client launches with a safety floor (minimum 500ms, default 1500ms).
    /// </summary>
    Task<TimeSpan> ThrottleLaunchAsync(TimeSpan? minDelay = null, CancellationToken ct = default);

    /// <summary>
    /// Probes for a specific process's visible main window within a timeout period.
    /// </summary>
    Task<nint> WaitForNewClientMainWindowAsync(
        int pid,
        TimeSpan timeout,
        Func<nint, bool>? windowValidator = null,
        CancellationToken ct = default);

    /// <summary>
    /// Probes for a newly spawned client's visible main window not in the previous PIDs set.
    /// </summary>
    Task<ClientWindowWaitResult> WaitForNewClientMainWindowAsync(
        IReadOnlySet<int> previousPids,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        CancellationToken ct = default);
}
