using System.Diagnostics;
using BaBaBooeyStrap.Core.Interop;
using BaBaBooeyStrap.MultiInstance.Abstractions;

namespace BaBaBooeyStrap.MultiInstance;

/// <summary>
/// Supervises running Roblox client processes, enforces anti-cheat launch throttling delays,
/// and probes for client main window visibility.
/// </summary>
public sealed class ProcessSupervisor : IProcessSupervisor
{
    public static readonly TimeSpan DefaultLaunchDelay = TimeSpan.FromMilliseconds(1500);
    public static readonly TimeSpan MinimumDelayFloor = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan DefaultWindowWaitTimeout = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan DefaultWindowPollInterval = TimeSpan.FromMilliseconds(250);

    private readonly Func<IReadOnlyList<ClientWindowInfo>>? _clientWindowProbe;
    private readonly List<int> _trackedPids = new();
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _throttleSemaphore = new(1, 1);
    private DateTime _lastLaunchTimeUtc = DateTime.MinValue;

    /// <summary>
    /// Initializes a process supervisor. Optionally accepts a client window probe delegate for deterministic testing.
    /// </summary>
    public ProcessSupervisor(Func<IReadOnlyList<ClientWindowInfo>>? clientWindowProbe = null)
    {
        _clientWindowProbe = clientWindowProbe;
    }

    /// <summary>
    /// Gets a snapshot of currently tracked process IDs.
    /// </summary>
    public IReadOnlyList<int> TrackedPids
    {
        get
        {
            lock (_stateLock)
            {
                return _trackedPids.ToList().AsReadOnly();
            }
        }
    }

    /// <summary>
    /// Registers a client process ID. Throws <see cref="ArgumentOutOfRangeException"/> if PID &lt;= 0.
    /// </summary>
    public void RegisterProcess(int pid)
    {
        if (pid <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pid), "Process ID must be greater than zero.");
        }

        lock (_stateLock)
        {
            if (!_trackedPids.Contains(pid))
            {
                _trackedPids.Add(pid);
                _lastLaunchTimeUtc = DateTime.UtcNow;
            }
        }
    }

    /// <summary>
    /// Unregisters a client process ID. Returns true if removed, false otherwise.
    /// </summary>
    public bool UnregisterProcess(int pid)
    {
        lock (_stateLock)
        {
            return _trackedPids.Remove(pid);
        }
    }

    /// <summary>
    /// Clears all tracked client processes.
    /// </summary>
    public void ClearTrackedProcesses()
    {
        lock (_stateLock)
        {
            _trackedPids.Clear();
        }
    }

    /// <summary>
    /// Returns sorted ascending PIDs of all running RobloxPlayerBeta processes.
    /// </summary>
    public IReadOnlyList<int> GetRunningRobloxPids()
    {
        if (_clientWindowProbe != null)
        {
            return _clientWindowProbe()
                .Select(c => c.ProcessId)
                .Where(pid => pid > 0)
                .Distinct()
                .OrderBy(pid => pid)
                .ToList()
                .AsReadOnly();
        }

        var pids = new List<int>();
        var processes = Process.GetProcessesByName("RobloxPlayerBeta");
        foreach (var proc in processes)
        {
            try
            {
                pids.Add(proc.Id);
            }
            finally
            {
                proc.Dispose();
            }
        }

        return pids.Distinct().OrderBy(pid => pid).ToList().AsReadOnly();
    }

    /// <summary>
    /// Captures the set of currently running client PIDs.
    /// </summary>
    public IReadOnlySet<int> CaptureRunningClientPids()
    {
        return new HashSet<int>(GetRunningRobloxPids());
    }

    /// <summary>
    /// Enforces launch delay throttling with a 500ms safety floor (default 1500ms) serialized across calls.
    /// </summary>
    public async Task<TimeSpan> ThrottleLaunchAsync(TimeSpan? minDelay = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var requestedDelay = minDelay ?? DefaultLaunchDelay;
        var effectiveDelay = requestedDelay < MinimumDelayFloor ? MinimumDelayFloor : requestedDelay;

        await _throttleSemaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();

            var now = DateTime.UtcNow;
            var elapsedSinceLast = now - _lastLaunchTimeUtc;
            var delayNeeded = effectiveDelay - elapsedSinceLast;

            if (delayNeeded > TimeSpan.Zero)
            {
                await Task.Delay(delayNeeded, ct).ConfigureAwait(false);
            }

            _lastLaunchTimeUtc = DateTime.UtcNow;
            return delayNeeded > TimeSpan.Zero ? delayNeeded : TimeSpan.Zero;
        }
        finally
        {
            _throttleSemaphore.Release();
        }
    }

    /// <summary>
    /// Probes for a specific process's visible main window within the given timeout.
    /// </summary>
    public async Task<nint> WaitForNewClientMainWindowAsync(
        int pid,
        TimeSpan timeout,
        Func<nint, bool>? windowValidator = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (pid <= 0)
        {
            return nint.Zero;
        }

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();
            if (_clientWindowProbe != null)
            {
                var match = _clientWindowProbe().FirstOrDefault(c =>
                    c.ProcessId == pid &&
                    c.MainWindowHandle != nint.Zero &&
                    c.IsVisible &&
                    (windowValidator == null || windowValidator(c.MainWindowHandle)));

                if (match != null)
                {
                    return match.MainWindowHandle;
                }
            }
            else
            {
                var foundHwnd = FindMainWindowForProcess(pid, windowValidator);
                if (foundHwnd != nint.Zero)
                {
                    return foundHwnd;
                }
            }

            var remaining = timeout - sw.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            var pollDelay = remaining < TimeSpan.FromMilliseconds(50) ? remaining : TimeSpan.FromMilliseconds(50);
            await Task.Delay(pollDelay, ct).ConfigureAwait(false);
        }

        return nint.Zero;
    }

    /// <summary>
    /// Probes for a newly spawned client's visible main window that does not belong to <paramref name="previousPids"/>.
    /// </summary>
    public async Task<ClientWindowWaitResult> WaitForNewClientMainWindowAsync(
        IReadOnlySet<int> previousPids,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(previousPids);

        var effectiveTimeout = timeout ?? DefaultWindowWaitTimeout;
        var effectivePoll = pollInterval ?? DefaultWindowPollInterval;
        if (effectivePoll <= TimeSpan.Zero)
        {
            effectivePoll = DefaultWindowPollInterval;
        }

        var sw = Stopwatch.StartNew();

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var clients = _clientWindowProbe != null ? _clientWindowProbe() : DefaultGetClientWindows();
            foreach (var client in clients)
            {
                if (!previousPids.Contains(client.ProcessId) &&
                    client.MainWindowHandle != nint.Zero &&
                    client.IsVisible)
                {
                    return new ClientWindowWaitResult(
                        WindowReady: true,
                        ProcessId: client.ProcessId,
                        MainWindowHandle: client.MainWindowHandle,
                        TimedOut: false);
                }
            }

            if (sw.Elapsed >= effectiveTimeout)
            {
                return new ClientWindowWaitResult(
                    WindowReady: false,
                    ProcessId: null,
                    MainWindowHandle: nint.Zero,
                    TimedOut: true);
            }

            var remaining = effectiveTimeout - sw.Elapsed;
            var delay = remaining < effectivePoll ? remaining : effectivePoll;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Finds a visible main window HWND for a specified process ID.
    /// </summary>
    public static nint FindMainWindowForProcess(int targetPid, Func<nint, bool>? windowValidator = null)
    {
        if (targetPid <= 0)
        {
            return nint.Zero;
        }

        nint matchingHwnd = nint.Zero;

        User32.EnumWindows((hWnd, _) =>
        {
            User32.GetWindowThreadProcessId(hWnd, out uint windowPid);
            if (windowPid == targetPid && User32.IsWindowVisible(hWnd))
            {
                if (windowValidator == null || windowValidator(hWnd))
                {
                    matchingHwnd = hWnd;
                    return false;
                }
            }
            return true;
        }, nint.Zero);

        return matchingHwnd;
    }

    private static IReadOnlyList<ClientWindowInfo> DefaultGetClientWindows()
    {
        var list = new List<ClientWindowInfo>();
        var processes = Process.GetProcessesByName("RobloxPlayerBeta");
        foreach (var proc in processes)
        {
            try
            {
                proc.Refresh();
                var hwnd = proc.MainWindowHandle;
                var visible = hwnd != nint.Zero && User32.IsWindowVisible(hwnd);
                list.Add(new ClientWindowInfo(proc.Id, hwnd, visible));
            }
            catch
            {
                // Process may have exited between enumeration and query
            }
            finally
            {
                proc.Dispose();
            }
        }

        return list;
    }
}
