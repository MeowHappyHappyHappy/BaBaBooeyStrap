using System.Collections.Concurrent;
using System.Diagnostics;
using BaBaBooeyStrap.Core.Logging;
using BaBaBooeyStrap.Core.Models;

namespace BaBaBooeyStrap.Integrations.Custom;

public sealed class CustomIntegrationRunner : ICustomIntegrationRunner
{
    private readonly ILogger _logger;
    private readonly Func<ProcessStartInfo, int?> _processStarter;
    private readonly Action<int> _processTerminator;
    private readonly Func<int, CancellationToken, Task> _delayAsync;
    private readonly ConcurrentDictionary<int, ConcurrentBag<int>> _trackedByRobloxPid = new();
    private bool _disposed;

    public CustomIntegrationRunner(
        ILogger? logger = null,
        Func<ProcessStartInfo, int?>? processStarter = null,
        Action<int>? processTerminator = null,
        Func<int, CancellationToken, Task>? delayAsync = null)
    {
        _logger = logger ?? Logger.Current;
        _processStarter = processStarter ?? StartProcessDefault;
        _processTerminator = processTerminator ?? TerminateProcessDefault;
        _delayAsync = delayAsync ?? ((ms, ct) => Task.Delay(ms, ct));
    }

    public async Task<IReadOnlyList<int>> LaunchIntegrationsAsync(
        IEnumerable<CustomIntegration> integrations,
        bool preLaunchOnly,
        int? trackedRobloxPid = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(integrations);
        if (_disposed) return Array.Empty<int>();

        var matching = integrations
            .Where(i => i != null && i.PreLaunch == preLaunchOnly && !string.IsNullOrWhiteSpace(i.Location))
            .ToList();

        if (matching.Count == 0)
        {
            return Array.Empty<int>();
        }

        var autoClosePids = new List<int>();
        foreach (var entry in matching)
        {
            ct.ThrowIfCancellationRequested();

            string location = entry.Location.Trim();
            if (Path.IsPathRooted(location) && !File.Exists(location))
            {
                _logger.Warn("CustomIntegrationRunner", $"Skipping custom integration '{entry.Name}': file not found at '{location}'.");
                continue;
            }

            int delayMs = Math.Clamp(entry.Delay, 0, 60000);
            if (delayMs > 0)
            {
                await _delayAsync(delayMs, ct).ConfigureAwait(false);
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = location,
                    Arguments = entry.LaunchArgs ?? string.Empty,
                    UseShellExecute = true
                };

                if (Path.IsPathRooted(location))
                {
                    string? dir = Path.GetDirectoryName(location);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    {
                        psi.WorkingDirectory = dir;
                    }
                }

                int? pid = _processStarter(psi);
                if (pid.HasValue && pid.Value > 0 && entry.AutoClose)
                {
                    autoClosePids.Add(pid.Value);
                    if (trackedRobloxPid.HasValue && trackedRobloxPid.Value > 0)
                    {
                        var bag = _trackedByRobloxPid.GetOrAdd(trackedRobloxPid.Value, _ => new ConcurrentBag<int>());
                        bag.Add(pid.Value);
                    }
                }

                _logger.Info("CustomIntegrationRunner", $"Launched custom integration '{entry.Name}' (PID: {pid?.ToString() ?? "shell"}).");
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
        if (_disposed || robloxPid <= 0 || integrationPids == null || integrationPids.Count == 0)
        {
            return;
        }

        var bag = _trackedByRobloxPid.GetOrAdd(robloxPid, _ => new ConcurrentBag<int>());
        foreach (int pid in integrationPids)
        {
            if (pid > 0)
            {
                bag.Add(pid);
            }
        }
    }

    public int CloseIntegrationsForPid(int robloxPid)
    {
        if (!_trackedByRobloxPid.TryRemove(robloxPid, out var bag))
        {
            return 0;
        }

        int closed = 0;
        foreach (int pid in bag.Distinct())
        {
            if (pid <= 0) continue;
            try
            {
                _processTerminator(pid);
                closed++;
            }
            catch
            {
                // Best-effort process cleanup
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
        return total;
    }

    private static int? StartProcessDefault(ProcessStartInfo psi)
    {
        using var process = Process.Start(psi);
        if (process == null) return null;
        try
        {
            return process.Id;
        }
        catch
        {
            return null;
        }
    }

    private static void TerminateProcessDefault(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            if (proc.HasExited) return;

            if (proc.MainWindowHandle != nint.Zero)
            {
                proc.CloseMainWindow();
                proc.WaitForExit(1500);
            }

            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: false);
            }
        }
        catch
        {
            // Process already exited or inaccessible
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CloseAllAutoCloseIntegrations();
    }
}
