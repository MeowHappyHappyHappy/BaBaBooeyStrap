namespace BaBaBooeyStrap.MultiInstance.Abstractions;

/// <summary>
/// Pre-launch validation result indicating whether client launch may proceed.
/// </summary>
public sealed record LaunchGateResult(
    bool CanLaunch,
    string? WarningMessage,
    MultiInstanceStatus Status
);

/// <summary>
/// Abstraction for validating multi-instance readiness and mutex ownership prior to client launch.
/// </summary>
public interface ILaunchGate
{
    /// <summary>
    /// Verifies whether the Roblox client launch is safe to proceed under the current multi-instance mode.
    /// </summary>
    LaunchGateResult VerifyLaunchReadiness(bool multiInstanceEnabled);

    /// <summary>
    /// Ensures that the multi-instance controller holds the singleton mutex, attempting to enable it if necessary.
    /// </summary>
    bool EnsureMutexOwnership();

    /// <summary>
    /// Obtains diagnostic status from the underlying controller.
    /// </summary>
    MultiInstanceStatus GetDiagnosticStatus();

    /// <summary>
    /// Formats a diagnostic warning message based on the current controller status.
    /// </summary>
    string BuildDiagnosticBlockedMessage(MultiInstanceStatus status);
}
