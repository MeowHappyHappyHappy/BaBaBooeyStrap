namespace BaBaBooeyStrap.MultiInstance.Abstractions;

/// <summary>
/// Abstraction for managing Roblox multi-instance preemptive singleton mutex and event.
/// </summary>
public interface IMultiInstanceController : IDisposable
{
    /// <summary>
    /// Enables preemptive mutex holding on a dedicated background STA thread.
    /// </summary>
    MultiInstanceStatus Enable();

    /// <summary>
    /// Disables preemptive mutex holding and releases all handles.
    /// </summary>
    MultiInstanceStatus Disable();

    /// <summary>
    /// Gets the current status of the controller and running Roblox client PIDs.
    /// </summary>
    MultiInstanceStatus GetStatus();
}
