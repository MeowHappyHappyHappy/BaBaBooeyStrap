using BaBaBooeyStrap.Core.Models;

namespace BaBaBooeyStrap.Integrations.Window;

public sealed record ObservedRobloxWindow(
    int ProcessId,
    nint WindowHandle,
    string WindowTitle,
    bool IsMinimized,
    bool IsFocused,
    MonitorInfo? Monitor);

public interface IRunningInstanceTracker : IDisposable
{
    event EventHandler<IReadOnlyList<RunningRobloxInstance>>? InstancesChanged;
    event EventHandler<IReadOnlyList<RunningRobloxInstance>>? InstancesRefreshed { add { } remove { } }
    event EventHandler<RunningRobloxInstance?>? FocusedInstanceChanged;

    IReadOnlyList<RunningRobloxInstance> GetRunningInstances();
    RunningRobloxInstance? GetFocusedInstance();

    void Start();
    void Stop();
    void RefreshNow();

    void RegisterLaunchedAccount(int pid, RobloxAccount account);
    bool TryAssociateUserId(int pid, long userId);
    void MarkTitleManagedExternally(int pid, bool managed);
    void UpdateServerCode(int pid, string? serverCode) { }
    IReadOnlyDictionary<string, string> GetPlaceholdersForPid(int pid);
}
