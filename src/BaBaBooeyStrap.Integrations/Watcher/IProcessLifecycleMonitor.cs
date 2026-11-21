namespace BaBaBooeyStrap.Integrations.Watcher;

public interface IProcessLifecycleMonitor
{
    IReadOnlyList<int> GetChildProcessIds(int parentPid);
    bool TryGetProcessStartTimeUtc(int pid, out DateTime startTimeUtc);
    bool IsProcessRunning(int pid, DateTime? expectedStartTimeUtc = null);
    bool TerminateProcessSafely(int pid, DateTime? expectedStartTimeUtc, TimeSpan gracefulTimeout);
    Task WaitForProcessExitAsync(int pid, DateTime? expectedStartTimeUtc, CancellationToken ct = default);
}
