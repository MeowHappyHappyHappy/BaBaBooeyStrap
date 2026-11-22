using System.Diagnostics;
using System.Text.Json;
using BaBaBooeyStrap.Core.Logging;
using BaBaBooeyStrap.Core.Models;

namespace BaBaBooeyStrap.Bootstrapper.Settings;

public sealed class GlobalSettingsGuardState
{
    public bool OriginalExisted { get; set; }
    public string? OriginalContent { get; set; }
    public bool OriginalReadOnly { get; set; }
    public bool ProfileCurrentlyApplied { get; set; }
    public List<int> ProtectedPids { get; set; } = [];
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
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly IGlobalSettingsManager _globalSettingsManager;
    private readonly string _stateFilePath;
    private readonly Func<IReadOnlyList<int>>? _customRunningPidsProvider;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _launchGate = new(1, 1);
    private readonly object _stateLock = new();
    private readonly Dictionary<int, CancellationTokenSource> _monitoredPids = new();

    private GlobalSettingsGuardState? _state;
    private int _activeSessionCount;
    private bool _disposed;

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
        await _launchGate.WaitAsync(ct).ConfigureAwait(false);
        lock (_stateLock)
        {
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

            _globalSettingsManager.ApplyProfile(profile);
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
                    if (pid > 0 && !_state!.ProtectedPids.Contains(pid) && IsPidAlive(pid))
                    {
                        _state.ProtectedPids.Add(pid);
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
        lock (_stateLock)
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var cts in _monitoredPids.Values)
            {
                try { cts.Cancel(); } catch { }
                try { cts.Dispose(); } catch { }
            }
            _monitoredPids.Clear();
        }

        _launchGate.Dispose();
    }

    private void EndLaunchSession()
    {
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
            }
        }
        finally
        {
            try { _launchGate.Release(); } catch { }
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
        if (_state is null || _state.ProtectedPids.Count == 0) return;

        var aliveSet = GetAlivePidSet();
        if (aliveSet is not null)
        {
            _state.ProtectedPids.RemoveAll(pid => !aliveSet.Contains(pid));
        }
        else
        {
            _state.ProtectedPids.RemoveAll(pid => !IsRealProcessAlive(pid));
        }
    }

    private HashSet<int>? GetAlivePidSet()
    {
        if (_customRunningPidsProvider is null) return null;
        return _customRunningPidsProvider().ToHashSet();
    }

    private bool IsPidAlive(int pid)
    {
        if (_customRunningPidsProvider is not null)
        {
            return _customRunningPidsProvider().Contains(pid);
        }
        return IsRealProcessAlive(pid);
    }

    private static bool IsRealProcessAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var proc = Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private void StartMonitoringMissingPidsLocked()
    {
        if (_state is null) return;

        foreach (int pid in _state.ProtectedPids)
        {
            if (_monitoredPids.ContainsKey(pid)) continue;

            var cts = new CancellationTokenSource();
            _monitoredPids[pid] = cts;
            _ = MonitorPidExitAsync(pid, cts.Token);
        }
    }

    private async Task MonitorPidExitAsync(int pid, CancellationToken ct)
    {
        try
        {
            if (_customRunningPidsProvider is not null)
            {
                while (!ct.IsCancellationRequested && _customRunningPidsProvider().Contains(pid))
                {
                    await Task.Delay(30, ct).ConfigureAwait(false);
                }
            }
            else
            {
                try
                {
                    using var proc = Process.GetProcessById(pid);
                    await proc.WaitForExitAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                {
                    // Process already exited
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.Debug("GlobalSettingsGuard", $"PID {pid} monitor stopped: {ex.Message}");
        }

        lock (_stateLock)
        {
            if (_monitoredPids.TryGetValue(pid, out var cts))
            {
                _monitoredPids.Remove(pid);
                cts.Dispose();
            }

            if (_disposed || _state is null) return;

            _state.ProtectedPids.Remove(pid);
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
    }

    private GlobalSettingsGuardState? TryLoadState()
    {
        try
        {
            if (!File.Exists(_stateFilePath)) return null;
            string json = File.ReadAllText(_stateFilePath);
            if (string.IsNullOrWhiteSpace(json)) return null;
            return JsonSerializer.Deserialize<GlobalSettingsGuardState>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.Warn("GlobalSettingsGuard", $"Failed to load guard state file: {ex.Message}");
            return null;
        }
    }

    private void PersistStateLocked()
    {
        if (_state is null) return;

        try
        {
            string? dir = Path.GetDirectoryName(_stateFilePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = JsonSerializer.Serialize(_state, JsonOptions);
            string tempPath = $"{_stateFilePath}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _stateFilePath, overwrite: true);
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
