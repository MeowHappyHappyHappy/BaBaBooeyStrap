using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using BaBaBooeyStrap.Core.Interop;
using BaBaBooeyStrap.Core.Logging;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Integrations.Matchmaking;

namespace BaBaBooeyStrap.Integrations.Window;

public sealed class RunningInstanceTracker : IRunningInstanceTracker
{
    private sealed class AccountRegistration
    {
        public Guid? AccountId { get; set; }
        public long? UserId { get; set; }
        public string? Username { get; set; }
        public string? DisplayName { get; set; }
        public string? Notes { get; set; }
        public bool IsExternal { get; set; }
        public DateTimeOffset RegisteredAtUtc { get; set; }
    }

    private readonly record struct PidCacheEntry(bool IsRoblox, DateTime ExpiresUtc);

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PendingRegistrationGrace = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan PidCacheTtl = TimeSpan.FromSeconds(30);

    private readonly Func<Settings> _getSettings;
    private readonly IWin32WindowInterop _interop;
    private readonly IMonitorProvider _monitorProvider;
    private readonly Func<long, RobloxAccount?>? _accountLookupByUserId;
    private readonly Func<IReadOnlyList<ObservedRobloxWindow>>? _windowProbe;
    private readonly ILogger _logger;

    private readonly object _stateLock = new();
    private readonly Dictionary<int, AccountRegistration> _registrations = new();
    private readonly Dictionary<int, int> _pidSlots = new();
    private readonly Dictionary<int, string> _pidServerCodes = new();
    private readonly Dictionary<int, DateTimeOffset> _firstSeenUtc = new();
    private readonly HashSet<int> _externallyManagedTitlePids = new();
    private readonly Dictionary<nint, string> _originalTitles = new();
    private readonly Dictionary<nint, string> _lastAppliedTitles = new();
    private readonly ConcurrentDictionary<uint, PidCacheEntry> _robloxPidCache = new();

    private List<RunningRobloxInstance> _currentInstances = new();
    private RunningRobloxInstance? _currentFocused;

    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;
    private SemaphoreSlim? _refreshSignal;

    private Thread? _hookThread;
    private uint _hookThreadId;
    private User32.WinEventDelegate? _winEventDelegate;
    private GCHandle _delegateHandle;
    private bool _running;
    private bool _disposed;

    public event EventHandler<IReadOnlyList<RunningRobloxInstance>>? InstancesChanged;
    public event EventHandler<IReadOnlyList<RunningRobloxInstance>>? InstancesRefreshed;
    public event EventHandler<RunningRobloxInstance?>? FocusedInstanceChanged;

    public RunningInstanceTracker(
        Func<Settings> getSettings,
        IWin32WindowInterop? interop = null,
        IMonitorProvider? monitorProvider = null,
        Func<long, RobloxAccount?>? accountLookupByUserId = null,
        Func<IReadOnlyList<ObservedRobloxWindow>>? windowProbe = null,
        ILogger? logger = null)
    {
        _getSettings = getSettings ?? throw new ArgumentNullException(nameof(getSettings));
        _interop = interop ?? new Win32WindowInterop();
        _monitorProvider = monitorProvider ?? new Win32MonitorProvider();
        _accountLookupByUserId = accountLookupByUserId;
        _windowProbe = windowProbe;
        _logger = logger ?? Logger.Current;
    }

    public IReadOnlyList<RunningRobloxInstance> GetRunningInstances()
    {
        lock (_stateLock)
        {
            return _currentInstances.ToList().AsReadOnly();
        }
    }

    public RunningRobloxInstance? GetFocusedInstance()
    {
        lock (_stateLock)
        {
            return _currentFocused;
        }
    }

    public void Start()
    {
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running) return;
            _running = true;

            _refreshSignal = new SemaphoreSlim(0, 1);
            _pollCts = new CancellationTokenSource();
            var token = _pollCts.Token;
            var signal = _refreshSignal;
            _pollTask = Task.Run(() => PollLoopAsync(signal, token), token);

            StartWinEventHookThread();
        }

        RefreshNow();
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        Task? pollTask;
        SemaphoreSlim? signal;
        Thread? hookThread;
        uint hookThreadId;

        lock (_stateLock)
        {
            if (!_running) return;
            _running = false;
            cts = _pollCts;
            _pollCts = null;
            pollTask = _pollTask;
            _pollTask = null;
            signal = _refreshSignal;
            _refreshSignal = null;
            hookThread = _hookThread;
            _hookThread = null;
            hookThreadId = _hookThreadId;
            _hookThreadId = 0;
        }

        if (hookThreadId != 0)
        {
            try { _interop.PostThreadMessage(hookThreadId, User32.WM_QUIT, nint.Zero, nint.Zero); } catch { }
        }

        if (hookThread != null && hookThread.IsAlive)
        {
            try { hookThread.Join(TimeSpan.FromSeconds(1)); } catch { }
        }

        if (cts != null)
        {
            try { cts.Cancel(); } catch { }
            if (pollTask != null)
            {
                try { pollTask.Wait(TimeSpan.FromSeconds(1)); } catch { }
            }
            try { cts.Dispose(); } catch { }
        }

        if (signal != null)
        {
            try { signal.Dispose(); } catch { }
        }
    }

    public void RegisterLaunchedAccount(int pid, RobloxAccount account)
    {
        if (pid <= 0) return;
        ArgumentNullException.ThrowIfNull(account);

        lock (_stateLock)
        {
            _firstSeenUtc.TryAdd(pid, DateTimeOffset.UtcNow);
            _registrations[pid] = new AccountRegistration
            {
                AccountId = account.Id,
                UserId = account.UserId > 0 ? account.UserId : null,
                Username = account.Username,
                DisplayName = string.IsNullOrWhiteSpace(account.DisplayName) ? account.Username : account.DisplayName,
                Notes = account.Notes,
                IsExternal = false,
                RegisteredAtUtc = DateTimeOffset.UtcNow
            };
        }

        RefreshNow();
    }

    public bool TryAssociateUserId(int pid, long userId)
    {
        if (pid <= 0 || userId <= 0 || _accountLookupByUserId == null)
        {
            return false;
        }

        RobloxAccount? account;
        try
        {
            account = _accountLookupByUserId(userId);
        }
        catch
        {
            return false;
        }

        if (account == null)
        {
            return false;
        }

        bool updated = false;
        lock (_stateLock)
        {
            if (_registrations.TryGetValue(pid, out var existing) &&
                !existing.IsExternal &&
                existing.UserId == account.UserId &&
                !string.IsNullOrWhiteSpace(existing.Username))
            {
                return false;
            }

            _firstSeenUtc.TryAdd(pid, DateTimeOffset.UtcNow);
            _registrations[pid] = new AccountRegistration
            {
                AccountId = account.Id,
                UserId = account.UserId,
                Username = account.Username,
                DisplayName = string.IsNullOrWhiteSpace(account.DisplayName) ? account.Username : account.DisplayName,
                Notes = account.Notes,
                IsExternal = false,
                RegisteredAtUtc = DateTimeOffset.UtcNow
            };
            updated = true;
        }

        if (updated)
        {
            RefreshNow();
        }

        return updated;
    }

    public void MarkTitleManagedExternally(int pid, bool managed)
    {
        if (pid <= 0) return;

        lock (_stateLock)
        {
            if (managed)
            {
                _externallyManagedTitlePids.Add(pid);
            }
            else
            {
                _externallyManagedTitlePids.Remove(pid);
            }
        }

        if (!managed)
        {
            RefreshNow();
        }
    }

    public void UpdateServerCode(int pid, string? serverCode)
    {
        if (pid <= 0) return;

        string normalized = NormalizeServerCode(serverCode);
        bool changed;

        lock (_stateLock)
        {
            _pidServerCodes.TryGetValue(pid, out string? existing);
            if (string.IsNullOrEmpty(normalized))
            {
                changed = _pidServerCodes.Remove(pid);
            }
            else if (!string.Equals(existing, normalized, StringComparison.Ordinal))
            {
                _pidServerCodes[pid] = normalized;
                changed = true;
            }
            else
            {
                changed = false;
            }
        }

        if (changed)
        {
            RefreshNow();
        }
    }

    public IReadOnlyDictionary<string, string> GetPlaceholdersForPid(int pid)
    {
        lock (_stateLock)
        {
            int slot = _pidSlots.TryGetValue(pid, out int assigned) ? assigned : 1;
            _registrations.TryGetValue(pid, out var reg);
            _pidServerCodes.TryGetValue(pid, out string? serverCode);
            return BuildPlaceholders(pid, slot, reg, serverCode: serverCode);
        }
    }

    public void RefreshNow()
    {
        if (_disposed) return;

        IReadOnlyList<ObservedRobloxWindow> observed;
        try
        {
            observed = _windowProbe != null ? _windowProbe() : ProbeRobloxWindows();
        }
        catch (Exception ex)
        {
            _logger.Debug("RunningInstanceTracker", $"Window probe failed: {ex.Message}");
            return;
        }

        Settings settings = _getSettings();
        IReadOnlyList<RunningRobloxInstance>? changedSnapshot = null;
        IReadOnlyList<RunningRobloxInstance> currentSnapshot;
        RunningRobloxInstance? focusedChange = null;
        bool focusChanged = false;
        var titleUpdates = new List<(nint HWnd, string TargetTitle)>();

        lock (_stateLock)
        {
            var observedPids = new HashSet<int>();
            var observedHwnds = new HashSet<nint>();
            var validObserved = new List<ObservedRobloxWindow>(observed.Count);
            var now = DateTimeOffset.UtcNow;

            foreach (var win in observed)
            {
                if (win.ProcessId <= 0 || win.WindowHandle == nint.Zero)
                {
                    continue;
                }

                if (!observedPids.Add(win.ProcessId))
                {
                    continue;
                }

                observedHwnds.Add(win.WindowHandle);
                validObserved.Add(win);
            }

            // Immediately release slots for any PID that no longer has an observed visible window
            foreach (int staleSlotPid in _pidSlots.Keys.Where(p => !observedPids.Contains(p)).ToList())
            {
                _pidSlots.Remove(staleSlotPid);
            }

            foreach (var win in validObserved)
            {
                _firstSeenUtc.TryAdd(win.ProcessId, now);
                EnsureSlotAssignedLocked(win.ProcessId);
            }

            CompactSlotsLocked();

            var newInstances = new List<RunningRobloxInstance>(validObserved.Count);
            foreach (var win in validObserved)
            {
                int slot = _pidSlots[win.ProcessId];
                var startedAt = _firstSeenUtc[win.ProcessId];
                _registrations.TryGetValue(win.ProcessId, out var reg);
                var monitor = win.Monitor ?? _monitorProvider.GetPrimaryMonitor();

                var instance = new RunningRobloxInstance(
                    ProcessId: win.ProcessId,
                    WindowHandle: win.WindowHandle,
                    SlotIndex: slot,
                    AccountId: reg?.AccountId,
                    UserId: reg?.UserId,
                    Username: reg?.Username,
                    DisplayName: reg?.DisplayName,
                    Notes: reg?.Notes,
                    IsExternal: reg?.IsExternal ?? true,
                    IsMinimized: win.IsMinimized,
                    IsFocused: win.IsFocused,
                    MonitorDeviceName: monitor.DeviceName,
                    MonitorIndex: monitor.Index,
                    StartedAtUtc: startedAt);

                newInstances.Add(instance);

                if (!_externallyManagedTitlePids.Contains(win.ProcessId))
                {
                    string? nextTitle = ComputeDesiredWindowTitleLocked(win.WindowHandle, win.WindowTitle, win.ProcessId, slot, reg, settings);
                    if (nextTitle != null)
                    {
                        titleUpdates.Add((win.WindowHandle, nextTitle));
                    }
                }
            }

            // Cleanup exited PIDs metadata (preserving recent pre-registrations within grace window)
            var trackedPids = _registrations.Keys
                .Concat(_firstSeenUtc.Keys)
                .Concat(_pidServerCodes.Keys)
                .Concat(_externallyManagedTitlePids)
                .Distinct()
                .ToList();

            foreach (int pid in trackedPids)
            {
                if (observedPids.Contains(pid))
                {
                    continue;
                }

                if (_registrations.TryGetValue(pid, out var reg) &&
                    (now - reg.RegisteredAtUtc) < PendingRegistrationGrace)
                {
                    continue;
                }

                _pidServerCodes.Remove(pid);
                _registrations.Remove(pid);
                _firstSeenUtc.Remove(pid);
                _externallyManagedTitlePids.Remove(pid);
            }

            foreach (nint staleHwnd in _originalTitles.Keys.Where(h => !observedHwnds.Contains(h)).ToList())
            {
                _originalTitles.Remove(staleHwnd);
                _lastAppliedTitles.Remove(staleHwnd);
            }

            newInstances.Sort((a, b) => a.SlotIndex.CompareTo(b.SlotIndex));
            var newFocused = newInstances.FirstOrDefault(i => i.IsFocused);

            if (!InstancesEqual(_currentInstances, newInstances))
            {
                _currentInstances = newInstances;
                changedSnapshot = newInstances.ToList().AsReadOnly();
            }

            currentSnapshot = changedSnapshot ?? _currentInstances.ToList().AsReadOnly();

            if (_currentFocused?.ProcessId != newFocused?.ProcessId ||
                _currentFocused?.WindowHandle != newFocused?.WindowHandle)
            {
                _currentFocused = newFocused;
                focusedChange = newFocused;
                focusChanged = true;
            }
        }

        // Apply Win32 SetWindowText outside state lock
        foreach (var (hwnd, targetTitle) in titleUpdates)
        {
            if (_interop.SetWindowText(hwnd, targetTitle))
            {
                lock (_stateLock)
                {
                    _lastAppliedTitles[hwnd] = targetTitle;
                }
            }
            else
            {
                _logger.Debug("RunningInstanceTracker", $"Failed to set window title for HWND 0x{hwnd:X}.");
            }
        }

        if (changedSnapshot != null)
        {
            InstancesChanged?.Invoke(this, changedSnapshot);
        }

        InstancesRefreshed?.Invoke(this, currentSnapshot);

        if (focusChanged)
        {
            FocusedInstanceChanged?.Invoke(this, focusedChange);
        }
    }

    private string? ComputeDesiredWindowTitleLocked(
        nint hwnd,
        string currentTitle,
        int pid,
        int slot,
        AccountRegistration? reg,
        Settings settings)
    {
        if (settings.AutoTagMultiInstanceWindowTitle)
        {
            string originalTitle = ResolveOriginalTitleLocked(hwnd, currentTitle);
            string template = string.IsNullOrWhiteSpace(settings.MultiInstanceWindowTitleTemplate)
                ? Settings.DefaultMultiInstanceWindowTitleTemplate
                : settings.MultiInstanceWindowTitleTemplate;

            _pidServerCodes.TryGetValue(pid, out string? serverCode);
            var placeholders = BuildPlaceholders(pid, slot, reg, originalTitle, serverCode);
            string formatted = WindowTitleFormatter.Format(template, placeholders);

            if (string.IsNullOrWhiteSpace(formatted) ||
                string.Equals(currentTitle, formatted, StringComparison.Ordinal))
            {
                if (string.Equals(currentTitle, formatted, StringComparison.Ordinal))
                {
                    _lastAppliedTitles[hwnd] = formatted;
                }
                return null;
            }

            return formatted;
        }

        if (_lastAppliedTitles.TryGetValue(hwnd, out string? previousApplied) &&
            string.Equals(currentTitle, previousApplied, StringComparison.Ordinal) &&
            _originalTitles.TryGetValue(hwnd, out string? original))
        {
            _lastAppliedTitles.Remove(hwnd);
            if (!string.Equals(currentTitle, original, StringComparison.Ordinal))
            {
                return original;
            }
        }

        return null;
    }

    private string ResolveOriginalTitleLocked(nint hwnd, string currentTitle)
    {
        if (_lastAppliedTitles.TryGetValue(hwnd, out string? lastApplied) &&
            string.Equals(currentTitle, lastApplied, StringComparison.Ordinal) &&
            _originalTitles.TryGetValue(hwnd, out string? stored))
        {
            return stored;
        }

        string safeTitle = string.IsNullOrWhiteSpace(currentTitle) ? "Roblox" : currentTitle;
        _originalTitles[hwnd] = safeTitle;
        return safeTitle;
    }

    private static Dictionary<string, string> BuildPlaceholders(
        int pid,
        int slot,
        AccountRegistration? reg,
        string originalTitle = "Roblox",
        string? serverCode = null)
    {
        string accountName = !string.IsNullOrWhiteSpace(reg?.Username) ? reg.Username : string.Empty;
        string displayName = !string.IsNullOrWhiteSpace(reg?.DisplayName) ? reg.DisplayName : accountName;
        string notes = reg?.Notes ?? string.Empty;
        string normalizedServer = NormalizeServerCode(serverCode);

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Index"] = slot.ToString(),
            ["Account"] = accountName,
            ["AccountName"] = accountName,
            ["DisplayName"] = displayName,
            ["Notes"] = notes,
            ["Server"] = normalizedServer,
            ["ServerCode"] = normalizedServer,
            ["Pid"] = pid.ToString(),
            ["OriginalTitle"] = string.IsNullOrWhiteSpace(originalTitle) ? "Roblox" : originalTitle
        };
    }

    public static string NormalizeServerCode(string? serverCode)
    {
        if (string.IsNullOrWhiteSpace(serverCode) ||
            string.Equals(serverCode.Trim(), "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        string iso = RoValraService.NormalizeCountryToIso(serverCode);
        if (iso.Length == 2 && char.IsAsciiLetter(iso[0]) && char.IsAsciiLetter(iso[1]))
        {
            return iso.ToUpperInvariant();
        }

        return string.Empty;
    }

    private int EnsureSlotAssignedLocked(int pid)
    {
        if (_pidSlots.TryGetValue(pid, out int existing))
        {
            return existing;
        }

        var used = _pidSlots.Values.ToHashSet();
        int candidate = 1;
        while (used.Contains(candidate))
        {
            candidate++;
        }

        _pidSlots[pid] = candidate;
        return candidate;
    }

    private void CompactSlotsLocked()
    {
        if (_pidSlots.Count == 0)
        {
            return;
        }

        var ordered = _pidSlots
            .OrderBy(kvp => kvp.Value)
            .ThenBy(kvp => _firstSeenUtc.TryGetValue(kvp.Key, out var started) ? started : DateTimeOffset.MaxValue)
            .ThenBy(kvp => kvp.Key)
            .Select(kvp => kvp.Key)
            .ToList();

        for (int i = 0; i < ordered.Count; i++)
        {
            _pidSlots[ordered[i]] = i + 1;
        }
    }

    private static bool InstancesEqual(IReadOnlyList<RunningRobloxInstance> a, IReadOnlyList<RunningRobloxInstance> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i]) return false;
        }
        return true;
    }

    private IReadOnlyList<ObservedRobloxWindow> ProbeRobloxWindows()
    {
        nint fgHwnd = _interop.GetForegroundWindow();
        var results = new List<ObservedRobloxWindow>();
        var seenPids = new HashSet<int>();

        _interop.EnumWindows((hwnd, _) =>
        {
            if (!_interop.IsWindow(hwnd) || !_interop.IsWindowVisible(hwnd))
            {
                return true;
            }

            _interop.GetWindowThreadProcessId(hwnd, out uint rawPid);
            if (rawPid == 0 || seenPids.Contains((int)rawPid))
            {
                return true;
            }

            string className = _interop.GetClassName(hwnd);
            if (!string.Equals(className, "WINDOWSCLIENT", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!IsRobloxPlayerProcess(rawPid))
            {
                return true;
            }

            seenPids.Add((int)rawPid);
            string title = _interop.GetWindowText(hwnd);
            bool minimized = _interop.IsIconic(hwnd);
            bool focused = hwnd == fgHwnd;
            var monitor = _monitorProvider.GetMonitorFromWindow(hwnd);
            results.Add(new ObservedRobloxWindow((int)rawPid, hwnd, title, minimized, focused, monitor));
            return true;
        });

        return results;
    }

    private bool IsRobloxPlayerProcess(uint pid)
    {
        if (pid == 0) return false;
        DateTime now = DateTime.UtcNow;
        if (_robloxPidCache.TryGetValue(pid, out var cached) && cached.ExpiresUtc > now)
        {
            return cached.IsRoblox;
        }

        bool isRoblox = false;
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            isRoblox = proc.ProcessName.Equals("RobloxPlayerBeta", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            isRoblox = false;
        }

        if (_robloxPidCache.Count >= 512)
        {
            foreach (var key in _robloxPidCache.Keys.Take(256))
            {
                _robloxPidCache.TryRemove(key, out _);
            }
        }

        _robloxPidCache[pid] = new PidCacheEntry(isRoblox, now.Add(PidCacheTtl));
        return isRoblox;
    }

    private async Task PollLoopAsync(SemaphoreSlim signal, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await signal.WaitAsync(PollInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (ct.IsCancellationRequested) break;
            RefreshNow();
        }
    }

    private void SignalImmediateRefresh()
    {
        try
        {
            var signal = _refreshSignal;
            if (signal != null && signal.CurrentCount == 0)
            {
                signal.Release();
            }
        }
        catch { }
    }

    private void StartWinEventHookThread()
    {
        using var ready = new ManualResetEventSlim(false);
        User32.WinEventDelegate localDelegate = HandleWinEvent;
        GCHandle localHandle = GCHandle.Alloc(localDelegate, GCHandleType.Normal);
        _winEventDelegate = localDelegate;
        _delegateHandle = localHandle;

        uint initThreadId = 0;
        var thread = new Thread(() =>
        {
            nint fgHook = nint.Zero;
            nint destroyHook = nint.Zero;
            nint nameChangeHook = nint.Zero;

            try
            {
                uint currentTid = _interop.GetCurrentThreadId();
                const uint flags = User32.WINEVENT_OUTOFCONTEXT | User32.WINEVENT_SKIPOWNPROCESS;

                fgHook = _interop.SetWinEventHook(
                    User32.EVENT_SYSTEM_FOREGROUND,
                    User32.EVENT_SYSTEM_FOREGROUND,
                    localDelegate,
                    0,
                    0,
                    flags);

                destroyHook = _interop.SetWinEventHook(
                    User32.EVENT_OBJECT_DESTROY,
                    User32.EVENT_OBJECT_DESTROY,
                    localDelegate,
                    0,
                    0,
                    flags);

                nameChangeHook = _interop.SetWinEventHook(
                    User32.EVENT_OBJECT_NAMECHANGE,
                    User32.EVENT_OBJECT_NAMECHANGE,
                    localDelegate,
                    0,
                    0,
                    flags);

                _interop.PeekMessage(out _, nint.Zero, 0, 0, User32.PM_NOREMOVE);
                Volatile.Write(ref initThreadId, currentTid);
                try { ready.Set(); } catch { }

                while (_interop.GetMessage(out var msg, nint.Zero, 0, 0) > 0)
                {
                    _interop.TranslateMessage(ref msg);
                    _interop.DispatchMessage(ref msg);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug("RunningInstanceTracker", $"WinEvent hook loop error: {ex.Message}");
                try { ready.Set(); } catch { }
            }
            finally
            {
                if (fgHook != nint.Zero) _interop.UnhookWinEvent(fgHook);
                if (destroyHook != nint.Zero) _interop.UnhookWinEvent(destroyHook);
                if (nameChangeHook != nint.Zero) _interop.UnhookWinEvent(nameChangeHook);
                if (localHandle.IsAllocated) localHandle.Free();

                lock (_stateLock)
                {
                    if (ReferenceEquals(_winEventDelegate, localDelegate)) _winEventDelegate = null;
                    if (_delegateHandle == localHandle) _delegateHandle = default;
                }
            }
        })
        {
            IsBackground = true,
            Name = "BaBaBooeyStrap.InstanceTrackerHook"
        };

        _hookThread = thread;
        thread.Start();
        ready.Wait(TimeSpan.FromSeconds(2));
        _hookThreadId = Volatile.Read(ref initThreadId);
    }

    private void HandleWinEvent(
        nint hWinEventHook,
        uint eventType,
        nint hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        try
        {
            if (eventType == User32.EVENT_SYSTEM_FOREGROUND)
            {
                SignalImmediateRefresh();
                return;
            }

            if ((eventType == User32.EVENT_OBJECT_DESTROY || eventType == User32.EVENT_OBJECT_NAMECHANGE) &&
                hwnd != nint.Zero &&
                idObject == User32.OBJID_WINDOW &&
                idChild == User32.CHILDID_SELF)
            {
                bool isKnown;
                lock (_stateLock)
                {
                    isKnown = _currentInstances.Any(i => i.WindowHandle == hwnd) || _originalTitles.ContainsKey(hwnd);
                }

                if (isKnown)
                {
                    SignalImmediateRefresh();
                }
            }
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        GC.SuppressFinalize(this);
    }
}
