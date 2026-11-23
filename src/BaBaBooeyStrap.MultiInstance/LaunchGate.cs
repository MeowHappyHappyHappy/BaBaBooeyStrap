using BaBaBooeyStrap.MultiInstance.Abstractions;

namespace BaBaBooeyStrap.MultiInstance;

/// <summary>
/// Pre-launch validation gate ensuring mutex ownership before triggering Roblox launches.
/// </summary>
public sealed class LaunchGate : ILaunchGate
{
    private readonly IMultiInstanceController? _controller;

    /// <summary>
    /// Initializes a LaunchGate instance with an optional controller for dependency injection.
    /// </summary>
    public LaunchGate(IMultiInstanceController? controller = null)
    {
        _controller = controller;
    }

    /// <inheritdoc/>
    public LaunchGateResult VerifyLaunchReadiness(bool multiInstanceEnabled) =>
        VerifyLaunchReadiness(_controller, multiInstanceEnabled);

    /// <inheritdoc/>
    public bool EnsureMutexOwnership() =>
        EnsureMutexOwnership(_controller);

    /// <inheritdoc/>
    public MultiInstanceStatus GetDiagnosticStatus() =>
        _controller == null
            ? new MultiInstanceStatus(false, false, false, Array.Empty<int>(), "Multi-instance controller is not configured.")
            : GetDiagnosticStatus(_controller);

    /// <inheritdoc/>
    string ILaunchGate.BuildDiagnosticBlockedMessage(MultiInstanceStatus status) =>
        BuildDiagnosticBlockedMessageStatic(status);

    /// <summary>
    /// Validates whether Roblox can be launched safely without single-instance collision or takeover.
    /// </summary>
    public static LaunchGateResult VerifyLaunchReadiness(IMultiInstanceController? controller, bool multiInstanceEnabled)
    {
        if (!multiInstanceEnabled)
        {
            return new LaunchGateResult(
                CanLaunch: true,
                WarningMessage: null,
                Status: new MultiInstanceStatus(false, false, false, Array.Empty<int>(), null));
        }

        if (controller == null)
        {
            return new LaunchGateResult(
                CanLaunch: false,
                WarningMessage: "Multi-instance controller is not configured.",
                Status: new MultiInstanceStatus(false, false, false, Array.Empty<int>(), "Controller is null"));
        }

        var status = controller.GetStatus();
        if (status.OwnsMutex)
        {
            return new LaunchGateResult(CanLaunch: true, WarningMessage: null, Status: status);
        }

        if (status.WaitingForMutex)
        {
            var waitingMessage = BuildDiagnosticBlockedMessageStatic(status);
            return new LaunchGateResult(CanLaunch: false, WarningMessage: waitingMessage, Status: status);
        }

        // Attempt recovery by enabling
        var newStatus = controller.Enable();
        if (newStatus.OwnsMutex)
        {
            return new LaunchGateResult(CanLaunch: true, WarningMessage: null, Status: newStatus);
        }

        var message = BuildDiagnosticBlockedMessageStatic(newStatus);
        return new LaunchGateResult(CanLaunch: false, WarningMessage: message, Status: newStatus);
    }

    /// <summary>
    /// Ensures that the controller owns the singleton mutex, attempting to enable it if not already owned.
    /// </summary>
    public static bool EnsureMutexOwnership(IMultiInstanceController? controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        var status = controller.GetStatus();
        if (!status.OwnsMutex)
        {
            status = controller.Enable();
        }
        return status.OwnsMutex;
    }

    /// <summary>
    /// Retrieves current diagnostic status from the controller.
    /// </summary>
    public static MultiInstanceStatus GetDiagnosticStatus(IMultiInstanceController? controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        return controller.GetStatus();
    }

    /// <summary>
    /// Builds a formatted diagnostic message describing why launch is blocked.
    /// </summary>
    public static string BuildDiagnosticBlockedMessage(MultiInstanceStatus status) =>
        BuildDiagnosticBlockedMessageStatic(status);

    private static string BuildDiagnosticBlockedMessageStatic(MultiInstanceStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        var pidsText = status.RunningRobloxPids.Count > 0
            ? string.Join(", ", status.RunningRobloxPids)
            : "none";
        var lastErrorText = string.IsNullOrWhiteSpace(status.LastError)
            ? "none"
            : status.LastError;

        string summary;
        if (!string.IsNullOrWhiteSpace(status.LastError))
        {
            summary = $"Cannot guarantee multi-instance launch: Roblox singleton mutex error ({status.LastError}).";
        }
        else if (status.WaitingForMutex)
        {
            summary = "Roblox singleton mutex is currently held by another application. Launching now may result in single-instance takeover.";
        }
        else
        {
            summary = "Failed to acquire Roblox singleton mutex. Multi-instance launch blocked.";
        }

        return $"{summary} [OwnsMutex={status.OwnsMutex}, WaitingForMutex={status.WaitingForMutex}, RunningRobloxPids={pidsText}, LastError={lastErrorText}]";
    }
}
