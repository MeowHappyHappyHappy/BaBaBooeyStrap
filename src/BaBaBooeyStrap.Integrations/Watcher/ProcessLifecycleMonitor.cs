using System.Diagnostics;
using System.Runtime.InteropServices;
using BaBaBooeyStrap.Core.Interop;

namespace BaBaBooeyStrap.Integrations.Watcher;

public sealed class ProcessLifecycleMonitor : IProcessLifecycleMonitor
{
    private static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(1);

    public IReadOnlyList<int> GetChildProcessIds(int parentPid)
    {
        if (parentPid <= 0) return Array.Empty<int>();

        var childrenByParent = new Dictionary<uint, List<uint>>();
        nint snapshot = Kernel32.CreateToolhelp32Snapshot(Kernel32.TH32CS_SNAPPROCESS, 0);
        if (snapshot == Kernel32.INVALID_HANDLE_VALUE || snapshot == nint.Zero)
        {
            return Array.Empty<int>();
        }

        try
        {
            var entry = new Kernel32.PROCESSENTRY32W
            {
                dwSize = (uint)Marshal.SizeOf<Kernel32.PROCESSENTRY32W>(),
                szExeFile = string.Empty
            };

            if (Kernel32.Process32FirstW(snapshot, ref entry))
            {
                do
                {
                    if (!childrenByParent.TryGetValue(entry.th32ParentProcessID, out var list))
                    {
                        list = new List<uint>();
                        childrenByParent[entry.th32ParentProcessID] = list;
                    }
                    list.Add(entry.th32ProcessID);
                }
                while (Kernel32.Process32NextW(snapshot, ref entry));
            }
        }
        finally
        {
            Kernel32.CloseHandle(snapshot);
        }

        // BFS to collect all transitive descendants
        var result = new List<int>();
        var visited = new HashSet<uint> { (uint)parentPid };
        var queue = new Queue<uint>();
        queue.Enqueue((uint)parentPid);

        while (queue.Count > 0)
        {
            uint current = queue.Dequeue();
            if (!childrenByParent.TryGetValue(current, out var directChildren)) continue;

            foreach (uint child in directChildren)
            {
                if (visited.Add(child) && child <= int.MaxValue)
                {
                    result.Add((int)child);
                    queue.Enqueue(child);
                }
            }
        }

        return result;
    }

    public bool TryGetProcessStartTimeUtc(int pid, out DateTime startTimeUtc)
    {
        startTimeUtc = default;
        if (pid <= 0) return false;

        try
        {
            using var proc = Process.GetProcessById(pid);
            if (proc.HasExited) return false;
            startTimeUtc = proc.StartTime.ToUniversalTime();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool IsProcessRunning(int pid, DateTime? expectedStartTimeUtc = null)
    {
        if (pid <= 0) return false;

        try
        {
            using var proc = Process.GetProcessById(pid);
            if (proc.HasExited) return false;

            if (expectedStartTimeUtc.HasValue)
            {
                DateTime actual = proc.StartTime.ToUniversalTime();
                if ((actual - expectedStartTimeUtc.Value).Duration() > StartTimeTolerance)
                {
                    return false; // PID reused by an unrelated process
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool TerminateProcessSafely(int pid, DateTime? expectedStartTimeUtc, TimeSpan gracefulTimeout)
    {
        if (pid <= 0) return false;

        try
        {
            using var proc = Process.GetProcessById(pid);
            if (proc.HasExited) return true;

            if (expectedStartTimeUtc.HasValue)
            {
                DateTime actual = proc.StartTime.ToUniversalTime();
                if ((actual - expectedStartTimeUtc.Value).Duration() > StartTimeTolerance)
                {
                    // Guard against PID reuse
                    return false;
                }
            }

            try
            {
                if (proc.MainWindowHandle != nint.Zero)
                {
                    proc.CloseMainWindow();
                    int waitMs = Math.Max(0, (int)gracefulTimeout.TotalMilliseconds);
                    if (waitMs > 0 && proc.WaitForExit(waitMs))
                    {
                        return true;
                    }
                }
            }
            catch { }

            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(1000);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task WaitForProcessExitAsync(int pid, DateTime? expectedStartTimeUtc, CancellationToken ct = default)
    {
        if (pid <= 0) return;

        while (!ct.IsCancellationRequested && IsProcessRunning(pid, expectedStartTimeUtc))
        {
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
    }
}
