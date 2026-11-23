using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using BaBaBooeyStrap.Core.Logging;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Storage;

namespace BaBaBooeyStrap.Bootstrapper.Settings;

public sealed record ProtectedProcessIdentity(int Pid, DateTime? StartTimeUtc, string? ProcessName);

public sealed class GlobalSettingsGuardState
{
    public bool OriginalExisted { get; set; }
    public string? OriginalContent { get; set; }
    public bool OriginalReadOnly { get; set; }
    public bool ProfileCurrentlyApplied { get; set; }
    public List<int> ProtectedPids { get; set; } = [];
    public List<ProtectedProcessIdentity> ProtectedProcesses { get; set; } = [];
    public DateTimeOffset CapturedAtUtc { get; set; }
}

public interface IGlobalSettingsGuard : IDisposable
{
    bool IsProtectionActive { get; }
    IReadOnlyCollection<int> ProtectedPids { get; }

    void RecoverOnStartup();
    Task<IAsyncDisposable> BeginLaunchSessionAsync(CancellationToken ct = default);
    void EnsureBackupCaptured();
    void ApplyProfileForLaunch(RobloxClientSettingsProfile profile);
    void RestoreOriginalAndProtect(IEnumerable<int> pidsToProtect, bool enableReadOnlyProtection);
    void UpdateOriginalContentFromCurrentFile();
    void RefreshProtectedPids();
}

public sealed class GlobalSettingsGuard : IGlobalSettingsGuard
{
    private const string StateFileName = "global_settings_guard.json";
    private static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(1);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly IGlobalSettingsManager _globalSettingsManager;
    private readonly string _stateFilePath;
    private readonly Func<IReadOnlyList<int>>? _customRunningPidsProvider;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _launchGate = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly object _stateLock = new();
    private readonly Dictionary<int, CancellationTokenSource> _monitoredPids = new();

    private GlobalSettingsGuardState? _state;
    private int _activeSessionCount;
    private int _gateDisposed;
    private volatile bool _disposed;

    public GlobalSettingsGuard(
        IGlobalSettingsManager globalSettingsManager,
        string stateDirectory,
        Func<IReadOnlyList<int>>? getRunningRobloxPids = null,
        ILogger? logger = null)
    {
        _globalSettingsManager = globalSettingsManager ?? throw new ArgumentNullException(nameof(globalSettingsManager));
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        _stateFilePath = Path.Combine(stateDirectory, StateFileName);
        _customRunningPidsProvider = getRunningRobloxPids;
        _logger = logger ?? Logger.Current;
    }

    public bool IsProtectionActive
    {
        get
        {
            lock (_stateLock)
            {
                return _state is { ProtectedPids.Count: > 0 } && _globalSettingsManager.ReadOnlyLocked;
            }
        }
    }

    public IReadOnlyCollection<int> ProtectedPids
    {
        get
        {
            lock (_stateLock)
            {
                return _state?.ProtectedPids.ToArray() ?? Array.Empty<int>();
            }
        }
    }

    public void RecoverOnStartup()
    {
        lock (_stateLock)
        {
            if (_disposed) return;

            var loaded = TryLoadState();
            if (loaded is null) return;

            _state = loaded;

            if (_state.ProfileCurrentlyApplied)
            {
                RestoreOriginalContentLocked();
                _state.ProfileCurrentlyApplied = false;
                _logger.Info("GlobalSettingsGuard", "Recovered GlobalBasicSettings_13.xml from interrupted profile launch.");
            }

            PruneDeadPidsLocked();

            if (_state.ProtectedPids.Count > 0)
            {
                if (File.Exists(_globalSettingsManager.FilePath))
                {
                    SetReadOnlySafely(true);
                }

                PersistStateLocked();
                StartMonitoringMissingPidsLocked();
                _logger.Info("GlobalSettingsGuard", $"Resumed settings ReadOnly protection for {_state.ProtectedPids.Count} running Roblox client(s).");
            }
            else
            {
                ReleaseProtectionAndDeleteStateLocked();
            }
        }
    }

    public async Task<IAsyncDisposable> BeginLaunchSessionAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        CancellationTokenSource linkedCts;
        try
        {
            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
        }
        catch (ObjectDisposedException)
        {
            throw new ObjectDisposedException(nameof(GlobalSettingsGuard));
        }

        using var _ = linkedCts;
        try
        {
            await _launchGate.WaitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            throw new ObjectDisposedException(nameof(GlobalSettingsGuard));
        }
        catch (OperationCanceledException) when (_disposed && !ct.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(GlobalSettingsGuard));
        }

        lock (_stateLock)
        {
            if (_disposed)
            {
                try { _launchGate.Release(); } catch { }
                throw new ObjectDisposedException(nameof(GlobalSettingsGuard));
            }
            _activeSessionCount++;
        }

        return new LaunchSessionScope(this);
    }

    public void EnsureBackupCaptured()
    {
        lock (_stateLock)
        {
            if (_disposed) return;

            _state ??= TryLoadState();
            if (_state is not null)
            {
                return;
            }

            var backup = _globalSettingsManager.CaptureBackupState();
            _state = new GlobalSettingsGuardState
            {
                OriginalExisted = backup.Existed,
                OriginalContent = backup.Content,
                OriginalReadOnly = _globalSettingsManager.ReadOnlyLocked,
                ProfileCurrentlyApplied = false,
                ProtectedPids = [],
                ProtectedProcesses = [],
                CapturedAtUtc = DateTimeOffset.UtcNow
            };

            PersistStateLocked();
        }
    }

    public void ApplyProfileForLaunch(RobloxClientSettingsProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!profile.HasOverrides) return;

        lock (_stateLock)
        {
            if (_disposed) return;

            EnsureBackupCapturedLocked();
            _state!.ProfileCurrentlyApplied = true;
            PersistStateLocked();

            try
            {
                _globalSettingsManager.ApplyProfile(profile);
            }
            catch
            {
                try
                {
                    RestoreOriginalContentLocked();
                }
                catch (Exception restoreEx)
                {
                    _logger.Warn("GlobalSettingsGuard", $"Failed to roll back GlobalBasicSettings_13.xml after profile error: {restoreEx.Message}");
                }

                _state.ProfileCurrentlyApplied = false;
                PersistStateLocked();
                throw;
            }
        }
    }

    public void RestoreOriginalAndProtect(IEnumerable<int> pidsToProtect, bool enableReadOnlyProtection)
    {
        ArgumentNullException.ThrowIfNull(pidsToProtect);

        lock (_stateLock)
        {
            if (_disposed) return;

            if (_state is not null && _state.ProfileCurrentlyApplied)
            {
                RestoreOriginalContentLocked();
                _state.ProfileCurrentlyApplied = false;
            }

            if (enableReadOnlyProtection)
            {
                EnsureBackupCapturedLocked();
                foreach (int pid in pidsToProtect)
                {
                    if (pid <= 0 || _state!.ProtectedProcesses.Any(p => p.Pid == pid))
                    {
                        continue;
                    }

                    if (_customRunningPidsProvider is not null)
                    {
                        if (_customRunningPidsProvider().Contains(pid))
                        {
                            _state.ProtectedProcesses.Add(new ProtectedProcessIdentity(pid, null, null));
                        }
                    }
                    else if (TryReadRealProcessIdentity(pid, out var identity))
                    {
                        _state.ProtectedProcesses.Add(identity);
                    }
                }

                PruneDeadPidsLocked();

                if (_state!.ProtectedPids.Count > 0)
                {
                    if (File.Exists(_globalSettingsManager.FilePath))
                    {
                        SetReadOnlySafely(true);
                    }

                    PersistStateLocked();
                    StartMonitoringMissingPidsLocked();
                }
                else
                {
                    ReleaseProtectionIfIdleLocked();
                }
            }
            else if (_state is not null)
            {
                PruneDeadPidsLocked();
                if (_state.ProtectedPids.Count > 0)
                {
                    if (File.Exists(_globalSettingsManager.FilePath))
                    {
                        SetReadOnlySafely(true);
                    }
                    PersistStateLocked();
                }
                else
                {
                    ReleaseProtectionIfIdleLocked();
                }
            }
        }
    }

    public void UpdateOriginalContentFromCurrentFile()
    {
        lock (_stateLock)
        {
            if (_disposed || _state is null || _state.ProfileCurrentlyApplied)
            {
                return;
            }

            var backup = _globalSettingsManager.CaptureBackupState();
            _state.OriginalExisted = backup.Existed;
            _state.OriginalContent = backup.Content;
            PersistStateLocked();
        }
    }

    public void RefreshProtectedPids()
    {
        lock (_stateLock)
        {
            if (_disposed || _state is null) return;

            PruneDeadPidsLocked();
            if (_state.ProtectedPids.Count == 0 && !_state.ProfileCurrentlyApplied && _activeSessionCount == 0)
            {
                ReleaseProtectionAndDeleteStateLocked();
            }
            else
            {
                PersistStateLocked();
            }
        }
    }

    public void Dispose()
    {
        bool disposeGateNow;
        lock (_stateLock)
        {
            if (_disposed) return;
            _disposed = true;

            try { _disposeCts.Cancel(); } catch { }

            foreach (var cts in _monitoredPids.Values)
            {
                try { cts.Cancel(); } catch { }
                try { cts.Dispose(); } catch { }
            }
            _monitoredPids.Clear();

            disposeGateNow = _activeSessionCount == 0;
        }

        if (disposeGateNow && Interlocked.Exchange(ref _gateDisposed, 1) == 0)
        {
            try { _launchGate.Dispose(); } catch { }
            try { _disposeCts.Dispose(); } catch { }
        }
    }

    private void EndLaunchSession()
    {
        bool disposeGateNow = false;
        try
        {
            lock (_stateLock)
            {
                if (_activeSessionCount > 0)
                {
                    _activeSessionCount--;
                }

                if (!_disposed && _state is not null)
                {
                    PruneDeadPidsLocked();
                    if (_state.ProtectedPids.Count == 0 && !_state.ProfileCurrentlyApplied && _activeSessionCount == 0)
                    {
                        ReleaseProtectionAndDeleteStateLocked();
                    }
                }

                if (_disposed && _activeSessionCount == 0)
                {
                    disposeGateNow = true;
                }
            }
        }
        finally
        {
            try { _launchGate.Release(); } catch { }
            if (disposeGateNow && Interlocked.Exchange(ref _gateDisposed, 1) == 0)
            {
                try { _launchGate.Dispose(); } catch { }
                try { _disposeCts.Dispose(); } catch { }
            }
        }
    }

    private void EnsureBackupCapturedLocked()
    {
        _state ??= TryLoadState();
        if (_state is not null) return;

        var backup = _globalSettingsManager.CaptureBackupState();
        _state = new GlobalSettingsGuardState
        {
            OriginalExisted = backup.Existed,
            OriginalContent = backup.Content,
            OriginalReadOnly = _globalSettingsManager.ReadOnlyLocked,
            ProfileCurrentlyApplied = false,
            ProtectedPids = [],
            ProtectedProcesses = [],
            CapturedAtUtc = DateTimeOffset.UtcNow
        };
        PersistStateLocked();
    }

    private void RestoreOriginalContentLocked()
    {
        if (_state is null) return;
        _globalSettingsManager.RestoreBackupState(new GlobalSettingsBackup(_state.OriginalExisted, _state.OriginalContent));
    }

    private void ReleaseProtectionIfIdleLocked()
    {
        if (_state is null) return;
        if (_state.ProtectedPids.Count == 0 && !_state.ProfileCurrentlyApplied && _activeSessionCount <= 1)
        {
            // If called inside active session before Dispose, keep state until EndLaunchSession unless _activeSessionCount == 0
            if (_activeSessionCount == 0)
            {
                ReleaseProtectionAndDeleteStateLocked();
            }
            else
            {
                PersistStateLocked();
            }
        }
    }

    private void ReleaseProtectionAndDeleteStateLocked()
    {
        if (_state is null) return;

        if (File.Exists(_globalSettingsManager.FilePath))
        {
            SetReadOnlySafely(_state.OriginalReadOnly);
        }

        DeleteStateFile();
        _state = null;
    }

    private void SetReadOnlySafely(bool readOnly)
    {
        try
        {
            _globalSettingsManager.ReadOnlyLocked = readOnly;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warn("GlobalSettingsGuard", $"Failed to set ReadOnly={readOnly} on GlobalBasicSettings: {ex.Message}");
        }
    }

    private void PruneDeadPidsLocked()
    {
        if (_state is null) return;

        NormalizeStateCollectionsLocked(_state);
        if (_state.ProtectedProcesses.Count == 0) return;

        var aliveSet = GetAlivePidSet();
        if (aliveSet is not null)
        {
            _state.ProtectedProcesses.RemoveAll(p => !aliveSet.Contains(p.Pid));
        }
        else
        {
            _state.ProtectedProcesses.RemoveAll(p => !IsRealProcessAlive(p));
        }

        _state.ProtectedPids = _state.ProtectedProcesses.Select(p => p.Pid).Distinct().ToList();
    }

    private static void NormalizeStateCollectionsLocked(GlobalSettingsGuardState state)
    {
        state.ProtectedProcesses ??= [];
        state.ProtectedPids ??= [];

        if (state.ProtectedProcesses.Count == 0 && state.ProtectedPids.Count > 0)
        {
            foreach (int pid in state.ProtectedPids.Where(p => p > 0).Distinct())
            {
                state.ProtectedProcesses.Add(new ProtectedProcessIdentity(pid, null, null));
            }
        }

        state.ProtectedPids = state.ProtectedProcesses.Select(p => p.Pid).Where(p => p > 0).Distinct().ToList();
    }

    private HashSet<int>? GetAlivePidSet()
    {
        if (_customRunningPidsProvider is null) return null;
        return _customRunningPidsProvider().ToHashSet();
    }

    private static bool TryReadRealProcessIdentity(int pid, out ProtectedProcessIdentity identity)
    {
        identity = default!;
        if (pid <= 0) return false;

        try
        {
            using var proc = Process.GetProcessById(pid);
            if (proc.HasExited) return false;

            DateTime? startTimeUtc = null;
            string? processName = null;
            try { startTimeUtc = proc.StartTime.ToUniversalTime(); } catch { }
            try { processName = proc.ProcessName; } catch { }

            identity = new ProtectedProcessIdentity(pid, startTimeUtc, processName);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    private static bool IsRealProcessAlive(ProtectedProcessIdentity entry)
    {
        if (entry.Pid <= 0) return false;
        try
        {
            using var proc = Process.GetProcessById(entry.Pid);
            if (proc.HasExited) return false;

            if (entry.StartTimeUtc.HasValue)
            {
                DateTime actualStartUtc = proc.StartTime.ToUniversalTime();
                if ((actualStartUtc - entry.StartTimeUtc.Value).Duration() > StartTimeTolerance)
                {
                    return false;
                }
            }

            if (!string.IsNullOrWhiteSpace(entry.ProcessName) &&
                !string.Equals(proc.ProcessName, entry.ProcessName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    private void StartMonitoringMissingPidsLocked()
    {
        if (_state is null) return;

        NormalizeStateCollectionsLocked(_state);
        foreach (var entry in _state.ProtectedProcesses)
        {
            if (_monitoredPids.ContainsKey(entry.Pid)) continue;

            var cts = new CancellationTokenSource();
            _monitoredPids[entry.Pid] = cts;
            _ = MonitorPidExitAsync(entry, cts.Token);
        }
    }

    private async Task MonitorPidExitAsync(ProtectedProcessIdentity entry, CancellationToken ct)
    {
        try
        {
            if (_customRunningPidsProvider is not null)
            {
                while (!ct.IsCancellationRequested && _customRunningPidsProvider().Contains(entry.Pid))
                {
                    await Task.Delay(30, ct).ConfigureAwait(false);
                }
            }
            else
            {
                try
                {
                    using var proc = Process.GetProcessById(entry.Pid);
                    if (!proc.HasExited && MatchesIdentity(proc, entry))
                    {
                        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
                {
                    // Process already exited or inaccessible
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.Debug("GlobalSettingsGuard", $"PID {entry.Pid} monitor stopped: {ex.Message}");
        }

        lock (_stateLock)
        {
            if (_monitoredPids.TryGetValue(entry.Pid, out var cts))
            {
                _monitoredPids.Remove(entry.Pid);
                try { cts.Dispose(); } catch { }
            }

            if (_disposed || _state is null) return;

            try
            {
                _state.ProtectedProcesses.RemoveAll(p => p.Pid == entry.Pid);
                PruneDeadPidsLocked();

                if (_state.ProtectedPids.Count == 0 && !_state.ProfileCurrentlyApplied && _activeSessionCount == 0)
                {
                    ReleaseProtectionAndDeleteStateLocked();
                    _logger.Info("GlobalSettingsGuard", "All protected Roblox instances exited; lifted temporary ReadOnly lock on GlobalBasicSettings_13.xml.");
                }
                else
                {
                    PersistStateLocked();
                }
            }
            catch (Exception ex)
            {
                _logger.Warn("GlobalSettingsGuard", $"Cleanup after PID {entry.Pid} exit failed: {ex.Message}");
            }
        }
    }

    private static bool MatchesIdentity(Process proc, ProtectedProcessIdentity entry)
    {
        try
        {
            if (entry.StartTimeUtc.HasValue)
            {
                DateTime actualStartUtc = proc.StartTime.ToUniversalTime();
                if ((actualStartUtc - entry.StartTimeUtc.Value).Duration() > StartTimeTolerance)
                {
                    return false;
                }
            }

            if (!string.IsNullOrWhiteSpace(entry.ProcessName) &&
                !string.Equals(proc.ProcessName, entry.ProcessName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private GlobalSettingsGuardState? TryLoadState()
    {
        try
        {
            using var fileLock = CrossProcessFileLock.Acquire(_stateFilePath);
            if (!File.Exists(_stateFilePath)) return null;

            string json = File.ReadAllText(_stateFilePath);
            if (string.IsNullOrWhiteSpace(json)) return null;

            var state = JsonSerializer.Deserialize<GlobalSettingsGuardState>(json, JsonOptions);
            if (state is null)
            {
                QuarantineCorruptStateFile("deserialized to null");
                return null;
            }

            NormalizeStateCollectionsLocked(state);
            return state;
        }
        catch (JsonException ex)
        {
            _logger.Warn("GlobalSettingsGuard", $"Corrupt guard state file detected; quarantining: {ex.Message}");
            QuarantineCorruptStateFile(ex.Message);
            return null;
        }
        catch (Exception ex)
        {
            _logger.Warn("GlobalSettingsGuard", $"Failed to load guard state file: {ex.Message}");
            return null;
        }
    }

    private void QuarantineCorruptStateFile(string reason)
    {
        try
        {
            if (!File.Exists(_stateFilePath)) return;
            string backupPath = $"{_stateFilePath}.corrupted.{DateTime.UtcNow:yyyyMMddHHmmssfff}.{Guid.NewGuid():N}.bak";
            File.Move(_stateFilePath, backupPath, overwrite: false);
            _logger.Warn("GlobalSettingsGuard", $"Quarantined corrupt guard state ({reason}) to '{backupPath}'.");
        }
        catch (Exception ex)
        {
            _logger.Debug("GlobalSettingsGuard", $"Failed to quarantine corrupt guard state file: {ex.Message}");
        }
    }

    private void PersistStateLocked()
    {
        if (_state is null) return;

        try
        {
            NormalizeStateCollectionsLocked(_state);
            string json = JsonSerializer.Serialize(_state, JsonOptions);
            using var fileLock = CrossProcessFileLock.Acquire(_stateFilePath);
            CrossProcessFileLock.AtomicWrite(_stateFilePath, tempPath => File.WriteAllText(tempPath, json));
        }
        catch (Exception ex)
        {
            _logger.Warn("GlobalSettingsGuard", $"Failed to persist guard state: {ex.Message}");
        }
    }

    private void DeleteStateFile()
    {
        try
        {
            using var fileLock = CrossProcessFileLock.Acquire(_stateFilePath);
            if (File.Exists(_stateFilePath))
            {
                File.Delete(_stateFilePath);
            }
        }
        catch { }
    }

    private sealed class LaunchSessionScope : IAsyncDisposable
    {
        private GlobalSettingsGuard? _owner;

        public LaunchSessionScope(GlobalSettingsGuard owner)
        {
            _owner = owner;
        }

        public ValueTask DisposeAsync()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.EndLaunchSession();
            return ValueTask.CompletedTask;
        }
    }
}
