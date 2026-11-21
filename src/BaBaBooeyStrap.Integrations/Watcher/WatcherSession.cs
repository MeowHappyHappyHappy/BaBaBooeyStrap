using System.Diagnostics;
using BaBaBooeyStrap.Core.Logging;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Integrations.Discord;
using BaBaBooeyStrap.Integrations.Window;

namespace BaBaBooeyStrap.Integrations.Watcher;

public sealed class WatcherSession : IDisposable
{
    private readonly WatcherData _data;
    private readonly IActivityWatcher _activityWatcher;
    private readonly IProcessLifecycleMonitor _processMonitor;
    private readonly IDiscordRpcService? _discordRpc;
    private readonly IWindowManipulation? _windowManipulation;
    private readonly ILogger _logger;
    private readonly Dictionary<int, DateTime> _registeredAutocloseStartTimes = new();
    private readonly TimeSpan _gracefulCloseTimeout;
    private bool _disposed;

    public WatcherSession(
        WatcherData data,
        IActivityWatcher activityWatcher,
        IProcessLifecycleMonitor? processMonitor = null,
        IDiscordRpcService? discordRpc = null,
        IWindowManipulation? windowManipulation = null,
        ILogger? logger = null,
        TimeSpan? gracefulCloseTimeout = null)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
        _activityWatcher = activityWatcher ?? throw new ArgumentNullException(nameof(activityWatcher));
        _processMonitor = processMonitor ?? new ProcessLifecycleMonitor();
        _discordRpc = discordRpc;
        _windowManipulation = windowManipulation;
        _logger = logger ?? Logger.Current;
        _gracefulCloseTimeout = gracefulCloseTimeout ?? TimeSpan.FromSeconds(2);
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        DateTime? mainStartTime = null;
        if (_processMonitor.TryGetProcessStartTimeUtc(_data.ProcessId, out var startUtc))
        {
            mainStartTime = startUtc;
        }

        if (_data.AutoclosePids != null)
        {
            foreach (int pid in _data.AutoclosePids)
            {
                if (_processMonitor.TryGetProcessStartTimeUtc(pid, out var autoStart))
                {
                    _registeredAutocloseStartTimes[pid] = autoStart;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(_data.LogFile))
        {
            _activityWatcher.StartWatching(_data.LogFile);
        }

        try
        {
            var visitedPids = new HashSet<int> { _data.ProcessId };
            int currentPid = _data.ProcessId;
            DateTime? currentStartTime = mainStartTime;

            while (true)
            {
                await _processMonitor.WaitForProcessExitAsync(currentPid, currentStartTime, ct).ConfigureAwait(false);

                int nextRobloxChildPid = FindRunningRobloxChildPid(currentPid, visitedPids);
                if (nextRobloxChildPid <= 0)
                {
                    break;
                }

                visitedPids.Add(nextRobloxChildPid);
                currentPid = nextRobloxChildPid;
                currentStartTime = _processMonitor.TryGetProcessStartTimeUtc(nextRobloxChildPid, out var childStart)
                    ? childStart
                    : null;
            }
        }
        finally
        {
            CleanupOnExit();
        }
    }

    private int FindRunningRobloxChildPid(int parentPid, HashSet<int> visitedPids)
    {
        foreach (int childPid in _processMonitor.GetChildProcessIds(parentPid))
        {
            if (visitedPids.Contains(childPid))
            {
                continue;
            }

            if (IsRunningRobloxPlayerProcess(childPid))
            {
                return childPid;
            }
        }

        return 0;
    }

    private static bool IsRunningRobloxPlayerProcess(int pid)
    {
        if (pid <= 0) return false;

        try
        {
            using var proc = Process.GetProcessById(pid);
            return !proc.HasExited && proc.ProcessName.Equals("RobloxPlayerBeta", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void CleanupOnExit()
    {
        try { _activityWatcher.StopWatching(); } catch { }
        try { _windowManipulation?.StopHook(); } catch { }

        if (_discordRpc != null)
        {
            try { _discordRpc.ClearPresenceAsync().GetAwaiter().GetResult(); } catch { }
            try { _discordRpc.Disconnect(); } catch { }
        }

        var pidsToClose = new Dictionary<int, DateTime?>();

        if (_data.AutoclosePids != null)
        {
            foreach (int pid in _data.AutoclosePids)
            {
                _registeredAutocloseStartTimes.TryGetValue(pid, out var recordedStart);
                pidsToClose[pid] = recordedStart == default ? null : recordedStart;
            }
        }

        foreach (int childPid in _processMonitor.GetChildProcessIds(_data.ProcessId))
        {
            if (IsRunningRobloxPlayerProcess(childPid))
            {
                continue;
            }

            if (!pidsToClose.ContainsKey(childPid))
            {
                pidsToClose[childPid] = _processMonitor.TryGetProcessStartTimeUtc(childPid, out var childStart)
                    ? childStart
                    : null;
            }
        }

        foreach (var (pid, expectedStart) in pidsToClose)
        {
            try
            {
                _processMonitor.TerminateProcessSafely(pid, expectedStart, _gracefulCloseTimeout);
            }
            catch (Exception ex)
            {
                _logger.Debug("WatcherSession", $"Failed to close PID {pid}: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _activityWatcher.Dispose();
        GC.SuppressFinalize(this);
    }
}
