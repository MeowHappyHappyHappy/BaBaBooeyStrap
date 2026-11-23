using System.Collections.Concurrent;
using System.Diagnostics;
using BaBaBooeyStrap.Core.Logging;
using BaBaBooeyStrap.Core.Models;

namespace BaBaBooeyStrap.Integrations.Custom;

public sealed class CustomIntegrationRunner : ICustomIntegrationRunner
{
    private readonly record struct TrackedIntegrationProcess(int Pid, DateTime? StartTimeUtc);

    private static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(1);

    private readonly ILogger _logger;
    private readonly Func<ProcessStartInfo, int?> _processStarter;
    private readonly bool _usesDefaultStarter;
    private readonly Action<int>? _customProcessTerminator;
    private readonly Func<int, CancellationToken, Task> _delayAsync;
    private readonly ConcurrentDictionary<int, ConcurrentBag<TrackedIntegrationProcess>> _trackedByRobloxPid = new();
    private readonly ConcurrentDictionary<int, DateTime?> _launchedStartTimes = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private int _disposed;

    public CustomIntegrationRunner(
        ILogger? logger = null,
        Func<ProcessStartInfo, int?>? processStarter = null,
        Action<int>? processTerminator = null,
        Func<int, CancellationToken, Task>? delayAsync = null)
    {
        _logger = logger ?? Logger.Current;
        _usesDefaultStarter = processStarter == null;
        _processStarter = processStarter ?? StartProcessDefault;
        _customProcessTerminator = processTerminator;
        _delayAsync = delayAsync ?? ((ms, ct) => Task.Delay(ms, ct));
    }

    public async Task<IReadOnlyList<int>> LaunchIntegrationsAsync(
        IEnumerable<CustomIntegration> integrations,
        bool preLaunchOnly,
        int? trackedRobloxPid = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(integrations);
        if (Volatile.Read(ref _disposed) != 0) return Array.Empty<int>();

        var matching = integrations
            .Where(i => i != null && i.PreLaunch == preLaunchOnly && !string.IsNullOrWhiteSpace(i.Location))
            .ToList();

        if (matching.Count == 0)
        {
            return Array.Empty<int>();
        }

        CancellationTokenSource? linkedCts = null;
        try
        {
            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
        }
        catch (ObjectDisposedException)
        {
            return Array.Empty<int>();
        }

        using var _ = linkedCts;
        CancellationToken effectiveCt = linkedCts.Token;

        var autoClosePids = new List<int>();
        foreach (var entry in matching)
        {
            effectiveCt.ThrowIfCancellationRequested();

            if (!TryResolveLocalExecutablePath(entry.Location, out string fullPath, out string rejectReason))
            {
                _logger.Warn("CustomIntegrationRunner", $"Skipping custom integration '{entry.Name}': {rejectReason}");
                continue;
            }

            if (!File.Exists(fullPath))
            {
                _logger.Warn("CustomIntegrationRunner", $"Skipping custom integration '{entry.Name}': file not found at '{fullPath}'.");
                continue;
            }

            int delayMs = Math.Clamp(entry.Delay, 0, 60000);
            if (delayMs > 0)
            {
                await _delayAsync(delayMs, effectiveCt).ConfigureAwait(false);
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = fullPath,
                    Arguments = entry.LaunchArgs ?? string.Empty,
                    UseShellExecute = false
                };

                string? dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    psi.WorkingDirectory = dir;
                }

                int? pid;
                DateTime? startTimeUtc = null;
                if (_usesDefaultStarter)
                {
                    (pid, startTimeUtc) = StartProcessDefaultWithTime(psi);
                }
                else
                {
                    pid = _processStarter(psi);
                }

                if (pid.HasValue && pid.Value > 0 && entry.AutoClose)
                {
                    _launchedStartTimes[pid.Value] = startTimeUtc;
                    autoClosePids.Add(pid.Value);
                    if (trackedRobloxPid.HasValue && trackedRobloxPid.Value > 0)
                    {
                        var bag = _trackedByRobloxPid.GetOrAdd(trackedRobloxPid.Value, _ => new ConcurrentBag<TrackedIntegrationProcess>());
                        bag.Add(new TrackedIntegrationProcess(pid.Value, startTimeUtc));
                    }
                }

                _logger.Info("CustomIntegrationRunner", $"Launched custom integration '{entry.Name}' (PID: {pid?.ToString() ?? "none"}).");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Warn("CustomIntegrationRunner", $"Failed to launch custom integration '{entry.Name}': {ex.Message}");
            }
        }

        return autoClosePids;
    }

    public void BindPreLaunchedToPid(IReadOnlyList<int> integrationPids, int robloxPid)
    {
        if (Volatile.Read(ref _disposed) != 0 || robloxPid <= 0 || integrationPids == null || integrationPids.Count == 0)
        {
            return;
        }

        var bag = _trackedByRobloxPid.GetOrAdd(robloxPid, _ => new ConcurrentBag<TrackedIntegrationProcess>());
        foreach (int pid in integrationPids)
        {
            if (pid > 0)
            {
                _launchedStartTimes.TryGetValue(pid, out DateTime? startTimeUtc);
                bag.Add(new TrackedIntegrationProcess(pid, startTimeUtc));
            }
        }
    }

    public int CloseIntegrationsForPid(int robloxPid)
    {
        if (!_trackedByRobloxPid.TryRemove(robloxPid, out var bag))
        {
            return 0;
        }

        var seen = new HashSet<int>();
        int closed = 0;
        foreach (var entry in bag)
        {
            if (entry.Pid <= 0 || !seen.Add(entry.Pid)) continue;
            DateTime? expectedStart = entry.StartTimeUtc;
            if (!expectedStart.HasValue && _launchedStartTimes.TryGetValue(entry.Pid, out var knownStart))
            {
                expectedStart = knownStart;
            }
            _launchedStartTimes.TryRemove(entry.Pid, out _);

            try
            {
                if (_customProcessTerminator != null)
                {
                    _customProcessTerminator(entry.Pid);
                    closed++;
                }
                else if (TerminateProcessDefault(entry.Pid, expectedStart))
                {
                    closed++;
                }
            }
            catch
            {
                // Best-effort process cleanup
            }
        }
        return closed;
    }

    public int CloseIntegrationPids(IEnumerable<int> integrationPids)
    {
        if (integrationPids == null) return 0;

        int closed = 0;
        foreach (int pid in integrationPids.Distinct())
        {
            if (pid <= 0) continue;

            _launchedStartTimes.TryRemove(pid, out DateTime? expectedStart);
            try
            {
                if (_customProcessTerminator != null)
                {
                    _customProcessTerminator(pid);
                    closed++;
                }
                else if (TerminateProcessDefault(pid, expectedStart))
                {
                    closed++;
                }
            }
            catch
            {
            }
        }

        return closed;
    }

    public int CloseAllAutoCloseIntegrations()
    {
        int total = 0;
        foreach (int robloxPid in _trackedByRobloxPid.Keys.ToList())
        {
            total += CloseIntegrationsForPid(robloxPid);
        }
        _launchedStartTimes.Clear();
        return total;
    }

    public static bool TryResolveLocalExecutablePath(string? rawLocation, out string fullPath, out string reason)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(rawLocation))
        {
            reason = "empty path.";
            return false;
        }

        string trimmed = rawLocation.Trim();
        if (trimmed.Contains('\0'))
        {
            reason = "path contains invalid characters.";
            return false;
        }

        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal) ||
            trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            reason = "UNC/network share paths are not allowed.";
            return false;
        }

        if (trimmed.Contains("://", StringComparison.Ordinal))
        {
            reason = "URI schemes are not allowed.";
            return false;
        }

        if (!Path.IsPathFullyQualified(trimmed))
        {
            reason = "executable path must be a rooted local path.";
            return false;
        }

        try
        {
            string normalized = Path.GetFullPath(trimmed);
            if (normalized.Length < 3 ||
                !char.IsAsciiLetter(normalized[0]) ||
                normalized[1] != ':' ||
                normalized[2] != '\\')
            {
                reason = "executable path must reside on a local drive letter.";
                return false;
            }

            fullPath = normalized;
            reason = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            reason = $"invalid path ({ex.Message}).";
            return false;
        }
    }

    private static int? StartProcessDefault(ProcessStartInfo psi) => StartProcessDefaultWithTime(psi).Pid;

    private static (int? Pid, DateTime? StartTimeUtc) StartProcessDefaultWithTime(ProcessStartInfo psi)
    {
        using var process = Process.Start(psi);
        if (process == null) return (null, null);

        int pid;
        try
        {
            pid = process.Id;
        }
        catch
        {
            return (null, null);
        }

        DateTime? startTimeUtc = null;
        try
        {
            startTimeUtc = process.StartTime.ToUniversalTime();
        }
        catch
        {
        }

        return (pid, startTimeUtc);
    }

    private static bool TerminateProcessDefault(int pid, DateTime? expectedStartTimeUtc)
    {
        if (!expectedStartTimeUtc.HasValue)
        {
            return false;
        }

        try
        {
            using var proc = Process.GetProcessById(pid);
            if (proc.HasExited) return true;

            DateTime actualStartUtc = proc.StartTime.ToUniversalTime();
            if ((actualStartUtc - expectedStartTimeUtc.Value).Duration() > StartTimeTolerance)
            {
                // Guard against terminating an unrelated recycled PID.
                return false;
            }

            if (proc.MainWindowHandle != nint.Zero)
            {
                proc.CloseMainWindow();
                proc.WaitForExit(1500);
            }

            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: false);
            }

            return true;
        }
        catch
        {
            // Process already exited or inaccessible
            return false;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try { _disposeCts.Cancel(); } catch { }
        CloseAllAutoCloseIntegrations();
        try { _disposeCts.Dispose(); } catch { }
    }
}
