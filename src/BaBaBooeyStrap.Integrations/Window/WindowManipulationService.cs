using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using BaBaBooeyStrap.Core.Interop;
using BaBaBooeyStrap.Core.Logging;

namespace BaBaBooeyStrap.Integrations.Window;

public sealed class WindowManipulationOptions
{
    public string? CustomTitleTemplate { get; init; }
    public IReadOnlyDictionary<string, string>? TitlePlaceholders { get; init; }
    public string? CustomIconPath { get; init; }
    public bool BorderlessFullscreen { get; init; }
    public uint TargetProcessId { get; init; }
    public bool SkipOwnProcess { get; init; } = true;
    public Func<nint, uint, string, string, bool>? WindowPredicate { get; init; }
}

public sealed class WindowManipulationService : IWindowManipulation
{
    private sealed record SavedWindowState(uint Style, uint ExStyle, User32.RECT Bounds);
    private sealed record OwnedIconPair(nint SmallIcon, nint LargeIcon);
    private readonly record struct PidCacheEntry(bool IsRoblox, DateTime ExpiresUtc);

    private const int MaxPidCacheEntries = 512;
    private static readonly TimeSpan PidCacheTtl = TimeSpan.FromSeconds(30);
    private const int MinimizedCoordThreshold = -32000;

    private static readonly IReadOnlyDictionary<string, string> EmptyPlaceholders =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private readonly IWin32WindowInterop _interop;
    private readonly WindowManipulationOptions _options;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<nint, SavedWindowState> _savedStates = new();
    private readonly ConcurrentDictionary<nint, OwnedIconPair> _ownedIcons = new();
    private readonly ConcurrentDictionary<nint, byte> _borderlessAppliedWindows = new();
    private readonly ConcurrentDictionary<nint, string> _originalTitles = new();
    private readonly ConcurrentDictionary<nint, string> _lastAppliedTitles = new();
    private readonly ConcurrentDictionary<nint, int> _inFlightTitleUpdates = new();
    private readonly ConcurrentDictionary<nint, uint> _windowPids = new();
    private readonly ConcurrentDictionary<uint, PidCacheEntry> _robloxPidCache = new();
    private readonly object _hookLock = new();
    private readonly object _titleLock = new();
    private readonly object _iconLock = new();
    private IReadOnlyDictionary<string, string>? _dynamicPlaceholders;

    private Thread? _hookThread;
    private uint _hookThreadId;
    private User32.WinEventDelegate? _winEventDelegate;
    private GCHandle _delegateHandle;
    private bool _hookRunning;
    private bool _disposed;

    public bool IsHookRunning
    {
        get { lock (_hookLock) return _hookRunning && !_disposed; }
    }

    public WindowManipulationService()
        : this(new Win32WindowInterop(), null, null)
    {
    }

    public WindowManipulationService(
        IWin32WindowInterop interop,
        WindowManipulationOptions? options = null,
        ILogger? logger = null)
    {
        _interop = interop ?? throw new ArgumentNullException(nameof(interop));
        _options = options ?? new WindowManipulationOptions();
        _logger = logger ?? Logger.Current;
        _dynamicPlaceholders = _options.TitlePlaceholders;
    }

    public void StartHook()
    {
        lock (_hookLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_hookRunning) return;

            using var readySignal = new ManualResetEventSlim(false);
            Exception? startupError = null;
            uint initializedThreadId = 0;
            bool startupAborted = false;

            User32.WinEventDelegate localDelegate = HandleWinEvent;
            GCHandle localHandle = GCHandle.Alloc(localDelegate, GCHandleType.Normal);
            _winEventDelegate = localDelegate;
            _delegateHandle = localHandle;

            Thread? createdThread = null;
            createdThread = new Thread(() =>
            {
                nint createHook = nint.Zero;
                nint showHook = nint.Zero;
                nint destroyHook = nint.Zero;
                nint nameChangeHook = nint.Zero;

                try
                {
                    uint currentThreadId = _interop.GetCurrentThreadId();

                    uint flags = User32.WINEVENT_OUTOFCONTEXT;
                    if (_options.SkipOwnProcess)
                    {
                        flags |= User32.WINEVENT_SKIPOWNPROCESS;
                    }

                    createHook = _interop.SetWinEventHook(
                        User32.EVENT_OBJECT_CREATE,
                        User32.EVENT_OBJECT_CREATE,
                        localDelegate,
                        _options.TargetProcessId,
                        0,
                        flags);

                    showHook = _interop.SetWinEventHook(
                        User32.EVENT_OBJECT_SHOW,
                        User32.EVENT_OBJECT_SHOW,
                        localDelegate,
                        _options.TargetProcessId,
                        0,
                        flags);

                    destroyHook = _interop.SetWinEventHook(
                        User32.EVENT_OBJECT_DESTROY,
                        User32.EVENT_OBJECT_DESTROY,
                        localDelegate,
                        _options.TargetProcessId,
                        0,
                        flags);

                    nameChangeHook = _interop.SetWinEventHook(
                        User32.EVENT_OBJECT_NAMECHANGE,
                        User32.EVENT_OBJECT_NAMECHANGE,
                        localDelegate,
                        _options.TargetProcessId,
                        0,
                        flags);

                    if (createHook == nint.Zero &&
                        showHook == nint.Zero &&
                        destroyHook == nint.Zero &&
                        nameChangeHook == nint.Zero)
                    {
                        _logger.Debug("WindowManipulationService", "All SetWinEventHook calls returned zero handles.");
                    }

                    // Ensure the Win32 message queue is created on this thread before StopHook() can PostThreadMessage(WM_QUIT).
                    _interop.PeekMessage(out _, nint.Zero, 0, 0, User32.PM_NOREMOVE);

                    Volatile.Write(ref initializedThreadId, currentThreadId);
                    try { readySignal.Set(); } catch (ObjectDisposedException) { }

                    while (!Volatile.Read(ref startupAborted) &&
                           _interop.GetMessage(out var msg, nint.Zero, 0, 0) > 0)
                    {
                        _interop.TranslateMessage(ref msg);
                        _interop.DispatchMessage(ref msg);
                    }
                }
                catch (Exception ex)
                {
                    startupError = ex;
                    try { readySignal.Set(); } catch (ObjectDisposedException) { }
                }
                finally
                {
                    if (createHook != nint.Zero) _interop.UnhookWinEvent(createHook);
                    if (showHook != nint.Zero) _interop.UnhookWinEvent(showHook);
                    if (destroyHook != nint.Zero) _interop.UnhookWinEvent(destroyHook);
                    if (nameChangeHook != nint.Zero) _interop.UnhookWinEvent(nameChangeHook);

                    if (localHandle.IsAllocated)
                    {
                        localHandle.Free();
                    }

                    lock (_hookLock)
                    {
                        if (ReferenceEquals(_winEventDelegate, localDelegate))
                        {
                            _winEventDelegate = null;
                        }
                        if (_delegateHandle == localHandle)
                        {
                            _delegateHandle = default;
                        }
                        if (ReferenceEquals(_hookThread, createdThread))
                        {
                            _hookThread = null;
                            _hookThreadId = 0;
                            _hookRunning = false;
                        }
                    }
                }
            })
            {
                IsBackground = true,
                Name = "BaBaBooeyStrap.WinEventHookLoop"
            };

            _hookThread = createdThread;
            _hookThread.Start();
            bool signaled = readySignal.Wait(TimeSpan.FromSeconds(3));

            if (!signaled)
            {
                Volatile.Write(ref startupAborted, true);
                _hookRunning = false;
                _hookThread = null;
                _hookThreadId = 0;

                uint lateThreadId = Volatile.Read(ref initializedThreadId);
                if (lateThreadId != 0)
                {
                    _interop.PostThreadMessage(lateThreadId, User32.WM_QUIT, nint.Zero, nint.Zero);
                }

                if (startupError != null)
                {
                    throw new InvalidOperationException("Failed to start WinEventHook thread.", startupError);
                }

                throw new InvalidOperationException("Failed to initialize WinEventHook thread message queue within timeout.");
            }

            if (startupError != null)
            {
                _hookRunning = false;
                _hookThread = null;
                _hookThreadId = 0;
                throw new InvalidOperationException("Failed to start WinEventHook thread.", startupError);
            }

            _hookThreadId = Volatile.Read(ref initializedThreadId);
            _hookRunning = true;
        }

        ScanExistingWindows();
    }

    public void StopHook()
    {
        Thread? threadToJoin;
        uint threadId;

        lock (_hookLock)
        {
            if (!_hookRunning && _hookThread == null) return;
            _hookRunning = false;
            threadToJoin = _hookThread;
            threadId = _hookThreadId;
            _hookThread = null;
            _hookThreadId = 0;
        }

        if (threadId != 0)
        {
            _interop.PostThreadMessage(threadId, User32.WM_QUIT, nint.Zero, nint.Zero);
        }

        if (threadToJoin != null && threadToJoin.IsAlive)
        {
            threadToJoin.Join(TimeSpan.FromSeconds(2));
        }
    }

    public void HandleWinEvent(
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
            if (hwnd == nint.Zero || idObject != User32.OBJID_WINDOW || idChild != User32.CHILDID_SELF)
            {
                return;
            }

            if (eventType == User32.EVENT_OBJECT_DESTROY)
            {
                _savedStates.TryRemove(hwnd, out _);
                _borderlessAppliedWindows.TryRemove(hwnd, out _);
                if (_windowPids.TryRemove(hwnd, out uint trackedPid))
                {
                    _robloxPidCache.TryRemove(trackedPid, out _);
                }
                lock (_titleLock)
                {
                    _originalTitles.TryRemove(hwnd, out _);
                    _lastAppliedTitles.TryRemove(hwnd, out _);
                    _inFlightTitleUpdates.TryRemove(hwnd, out _);
                }
                ReleaseOwnedIcons(hwnd);
                return;
            }

            if (eventType != User32.EVENT_OBJECT_CREATE &&
                eventType != User32.EVENT_OBJECT_SHOW &&
                eventType != User32.EVENT_OBJECT_NAMECHANGE)
            {
                return;
            }

            ApplyConfiguredRulesToWindow(hwnd, eventType != User32.EVENT_OBJECT_NAMECHANGE);
        }
        catch (Exception ex)
        {
            // Never allow managed exceptions to unwind across WinEventHook native callback boundary
            _logger.Debug("WindowManipulationService", $"WinEvent handler suppressed error: {ex.Message}");
        }
    }

    private void ScanExistingWindows()
    {
        try
        {
            uint ownPid = (uint)Environment.ProcessId;
            _interop.EnumWindows((hwnd, _) =>
            {
                try
                {
                    if (_disposed) return false;
                    if (_options.SkipOwnProcess)
                    {
                        _interop.GetWindowThreadProcessId(hwnd, out uint pid);
                        if (pid == ownPid) return true;
                    }

                    ApplyConfiguredRulesToWindow(hwnd, applyIconAndBorderless: true);
                }
                catch (Exception ex)
                {
                    _logger.Debug("WindowManipulationService", $"Initial window scan error: {ex.Message}");
                }
                return true;
            });
        }
        catch (Exception ex)
        {
            _logger.Debug("WindowManipulationService", $"EnumWindows failed: {ex.Message}");
        }
    }

    private void ApplyConfiguredRulesToWindow(nint hwnd, bool applyIconAndBorderless)
    {
        if (!_interop.IsWindow(hwnd) || !_interop.IsWindowVisible(hwnd))
        {
            return;
        }

        _interop.GetWindowThreadProcessId(hwnd, out uint pid);
        if (_options.TargetProcessId != 0 && pid != _options.TargetProcessId)
        {
            return;
        }

        string className = _interop.GetClassName(hwnd);
        string windowTitle = _interop.GetWindowText(hwnd);

        if (!IsTargetWindow(hwnd, pid, className, windowTitle))
        {
            return;
        }

        if (pid != 0)
        {
            _windowPids[hwnd] = pid;
        }

        if (!string.IsNullOrWhiteSpace(_options.CustomTitleTemplate))
        {
            var placeholders = Volatile.Read(ref _dynamicPlaceholders) ?? EmptyPlaceholders;
            SetCustomTitle(hwnd, _options.CustomTitleTemplate, placeholders);
        }

        if (applyIconAndBorderless)
        {
            if (!string.IsNullOrWhiteSpace(_options.CustomIconPath) && !_ownedIcons.ContainsKey(hwnd))
            {
                SetCustomIcon(hwnd, _options.CustomIconPath);
            }

            if (_options.BorderlessFullscreen && !_borderlessAppliedWindows.ContainsKey(hwnd))
            {
                if (SetBorderlessFullscreen(hwnd, true))
                {
                    _borderlessAppliedWindows[hwnd] = 0;
                }
            }
        }
    }

    public void UpdateTitlePlaceholders(IReadOnlyDictionary<string, string> placeholders)
    {
        ArgumentNullException.ThrowIfNull(placeholders);
        var snapshot = new Dictionary<string, string>(placeholders, StringComparer.OrdinalIgnoreCase);
        Volatile.Write(ref _dynamicPlaceholders, snapshot);

        if (_disposed || string.IsNullOrWhiteSpace(_options.CustomTitleTemplate))
        {
            return;
        }

        foreach (nint hwnd in _windowPids.Keys.ToList())
        {
            if (!IsValidWindowHandle(hwnd)) continue;

            _interop.GetWindowThreadProcessId(hwnd, out uint actualPid);
            if (_windowPids.TryGetValue(hwnd, out uint trackedPid) && trackedPid != 0 && actualPid != trackedPid)
            {
                continue;
            }
            if (_options.TargetProcessId != 0 && actualPid != _options.TargetProcessId)
            {
                continue;
            }

            SetCustomTitle(hwnd, _options.CustomTitleTemplate, snapshot);
        }
    }

    public bool SetCustomTitle(nint hWnd, string template, IReadOnlyDictionary<string, string> placeholders)
    {
        if (!IsValidWindowHandle(hWnd)) return false;
        if (string.IsNullOrWhiteSpace(template)) return false;

        string currentTitle = _interop.GetWindowText(hWnd);
        string formatted;
        bool hadPreviousApplied;
        string? previousApplied;

        lock (_titleLock)
        {
            string originalTitle;
            bool isInFlight = _inFlightTitleUpdates.TryGetValue(hWnd, out int inFlightCount) && inFlightCount > 0;
            hadPreviousApplied = _lastAppliedTitles.TryGetValue(hWnd, out previousApplied);

            if (hadPreviousApplied && string.Equals(currentTitle, previousApplied, StringComparison.Ordinal))
            {
                originalTitle = _originalTitles.GetOrAdd(hWnd, currentTitle);
            }
            else if (isInFlight && _originalTitles.TryGetValue(hWnd, out string? existingOriginal))
            {
                originalTitle = existingOriginal;
            }
            else
            {
                originalTitle = WindowTitleFormatter.ExtractOriginalTitle(currentTitle);
                _originalTitles[hWnd] = originalTitle;
            }

            var effectivePlaceholders = BuildEffectivePlaceholders(placeholders, originalTitle);
            formatted = WindowTitleFormatter.Format(template, effectivePlaceholders);
            if (string.IsNullOrWhiteSpace(formatted))
            {
                return false;
            }

            // Re-entrancy prevention guard against EVENT_OBJECT_NAMECHANGE loops
            if (string.Equals(currentTitle, formatted, StringComparison.Ordinal))
            {
                _lastAppliedTitles[hWnd] = formatted;
                return true;
            }

            // Record applied title BEFORE calling SetWindowText to close the synchronous/concurrent
            // EVENT_OBJECT_NAMECHANGE TOCTOU window while SetWindowText is in flight.
            _lastAppliedTitles[hWnd] = formatted;
            _inFlightTitleUpdates[hWnd] = inFlightCount + 1;
        }

        bool updated = false;
        try
        {
            updated = _interop.SetWindowText(hWnd, formatted);
            return updated;
        }
        finally
        {
            lock (_titleLock)
            {
                if (_inFlightTitleUpdates.TryGetValue(hWnd, out int count))
                {
                    if (count <= 1)
                    {
                        _inFlightTitleUpdates.TryRemove(hWnd, out _);
                    }
                    else
                    {
                        _inFlightTitleUpdates[hWnd] = count - 1;
                    }
                }

                if (!updated &&
                    _lastAppliedTitles.TryGetValue(hWnd, out string? currentLastApplied) &&
                    string.Equals(currentLastApplied, formatted, StringComparison.Ordinal))
                {
                    if (hadPreviousApplied && previousApplied != null)
                    {
                        _lastAppliedTitles[hWnd] = previousApplied;
                    }
                    else
                    {
                        _lastAppliedTitles.TryRemove(hWnd, out _);
                    }
                }
            }
        }
    }

    public bool SetCustomIcon(nint hWnd, string iconPath)
    {
        if (!IsValidWindowHandle(hWnd)) return false;
        if (string.IsNullOrWhiteSpace(iconPath) || !File.Exists(iconPath)) return false;

        nint hSmall = nint.Zero;
        nint hLarge = nint.Zero;

        string ext = Path.GetExtension(iconPath).ToLowerInvariant();
        if (ext == ".ico")
        {
            if (!HasValidIcoHeader(iconPath)) return false;

            int smW = Math.Max(16, _interop.GetSystemMetrics(User32.SM_CXSMICON));
            int smH = Math.Max(16, _interop.GetSystemMetrics(User32.SM_CYSMICON));
            int lgW = Math.Max(32, _interop.GetSystemMetrics(User32.SM_CXICON));
            int lgH = Math.Max(32, _interop.GetSystemMetrics(User32.SM_CYICON));

            hSmall = _interop.LoadImageIcon(iconPath, smW, smH);
            hLarge = _interop.LoadImageIcon(iconPath, lgW, lgH);
        }
        else if (ext is ".exe" or ".dll")
        {
            if (!HasValidPeHeader(iconPath)) return false;
            (hSmall, hLarge) = _interop.ExtractFirstIconPair(iconPath);
        }
        else
        {
            return false;
        }

        if (hSmall == nint.Zero && hLarge == nint.Zero)
        {
            return false;
        }

        _interop.SendMessageTimeout(hWnd, User32.WM_SETICON, User32.ICON_SMALL, hSmall, 200);
        _interop.SendMessageTimeout(hWnd, User32.WM_SETICON, User32.ICON_BIG, hLarge, 200);

        var newPair = new OwnedIconPair(hSmall, hLarge);
        OwnedIconPair? oldPairToDestroy = null;
        lock (_iconLock)
        {
            if (_disposed)
            {
                oldPairToDestroy = newPair;
            }
            else if (_ownedIcons.TryGetValue(hWnd, out var oldPair))
            {
                _ownedIcons[hWnd] = newPair;
                oldPairToDestroy = oldPair;
            }
            else
            {
                _ownedIcons[hWnd] = newPair;
            }
        }

        if (oldPairToDestroy != null)
        {
            DestroyIconPair(oldPairToDestroy, _disposed ? null : newPair);
            if (_disposed) return false;
        }

        return true;
    }

    public bool ResetIcon(nint hWnd)
    {
        if (!IsValidWindowHandle(hWnd)) return false;

        _interop.SendMessageTimeout(hWnd, User32.WM_SETICON, User32.ICON_SMALL, nint.Zero, 200);
        _interop.SendMessageTimeout(hWnd, User32.WM_SETICON, User32.ICON_BIG, nint.Zero, 200);
        ReleaseOwnedIcons(hWnd);
        return true;
    }

    public bool SetBorderlessFullscreen(nint hWnd, bool enabled)
    {
        if (!IsValidWindowHandle(hWnd)) return false;

        if (enabled)
        {
            var monitorRect = _interop.GetMonitorRectFromWindow(hWnd);
            return SetBorderlessFullscreen(hWnd, true, monitorRect.Left, monitorRect.Top, monitorRect.Width, monitorRect.Height);
        }

        return SetBorderlessFullscreen(hWnd, false, 0, 0, 0, 0);
    }

    public bool SetBorderlessFullscreen(nint hWnd, bool enabled, int x, int y, int cx, int cy)
    {
        if (!IsValidWindowHandle(hWnd)) return false;

        if (enabled && (_interop.IsZoomed(hWnd) || _interop.IsIconic(hWnd)))
        {
            _interop.ShowWindow(hWnd, User32.SW_RESTORE);
        }

        uint currentStyle = unchecked((uint)_interop.GetWindowLongPtr(hWnd, User32.GWL_STYLE).ToInt64());
        uint currentExStyle = unchecked((uint)_interop.GetWindowLongPtr(hWnd, User32.GWL_EXSTYLE).ToInt64());

        if (enabled)
        {
            bool isIconic = _interop.IsIconic(hWnd);
            User32.RECT currentBounds = default;
            bool hasValidBounds = isIconic
                ? (_interop.TryGetNormalWindowRect(hWnd, out currentBounds) && IsValidWindowBounds(currentBounds))
                : (_interop.GetWindowRect(hWnd, out currentBounds) && IsValidWindowBounds(currentBounds));

            if (hasValidBounds)
            {
                _savedStates.TryAdd(hWnd, new SavedWindowState(currentStyle, currentExStyle, currentBounds));
            }

            const uint removeStyleMask = User32.WS_CAPTION | User32.WS_THICKFRAME | User32.WS_MINIMIZEBOX | User32.WS_MAXIMIZEBOX | User32.WS_SYSMENU;
            const uint removeExStyleMask = User32.WS_EX_DLGMODALFRAME | User32.WS_EX_CLIENTEDGE | User32.WS_EX_STATICEDGE | User32.WS_EX_WINDOWEDGE;

            uint newStyle = (currentStyle & ~removeStyleMask) | User32.WS_POPUP;
            uint newExStyle = currentExStyle & ~removeExStyleMask;

            _interop.SetWindowLongPtr(hWnd, User32.GWL_STYLE, unchecked((nint)newStyle));
            _interop.SetWindowLongPtr(hWnd, User32.GWL_EXSTYLE, unchecked((nint)newExStyle));

            bool applied = _interop.SetWindowPos(
                hWnd,
                nint.Zero,
                x,
                y,
                cx,
                cy,
                User32.SWP_FRAMECHANGED | User32.SWP_NOZORDER | User32.SWP_SHOWWINDOW);

            if (applied)
            {
                _borderlessAppliedWindows[hWnd] = 0;
            }
            return applied;
        }

        _borderlessAppliedWindows.TryRemove(hWnd, out _);

        if (_savedStates.TryRemove(hWnd, out var saved))
        {
            _interop.SetWindowLongPtr(hWnd, User32.GWL_STYLE, unchecked((nint)saved.Style));
            _interop.SetWindowLongPtr(hWnd, User32.GWL_EXSTYLE, unchecked((nint)saved.ExStyle));

            if (IsValidWindowBounds(saved.Bounds))
            {
                return _interop.SetWindowPos(
                    hWnd,
                    nint.Zero,
                    saved.Bounds.Left,
                    saved.Bounds.Top,
                    saved.Bounds.Width,
                    saved.Bounds.Height,
                    User32.SWP_FRAMECHANGED | User32.SWP_NOZORDER | User32.SWP_SHOWWINDOW);
            }

            return _interop.SetWindowPos(
                hWnd,
                nint.Zero,
                0,
                0,
                0,
                0,
                User32.SWP_FRAMECHANGED | User32.SWP_NOZORDER | User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_SHOWWINDOW);
        }

        const uint restoreStyleMask = User32.WS_CAPTION | User32.WS_THICKFRAME | User32.WS_SYSMENU | User32.WS_MINIMIZEBOX | User32.WS_MAXIMIZEBOX;
        uint fallbackStyle = (currentStyle & ~User32.WS_POPUP) | restoreStyleMask;
        _interop.SetWindowLongPtr(hWnd, User32.GWL_STYLE, unchecked((nint)fallbackStyle));

        return _interop.SetWindowPos(
            hWnd,
            nint.Zero,
            0,
            0,
            0,
            0,
            User32.SWP_FRAMECHANGED | User32.SWP_NOZORDER | User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_SHOWWINDOW);
    }

    private bool IsValidWindowHandle(nint hWnd)
    {
        if (hWnd == nint.Zero) return false;
        return _interop.IsWindow(hWnd);
    }

    private bool IsTargetWindow(nint hwnd, uint pid, string className, string windowTitle)
    {
        if (_options.WindowPredicate != null)
        {
            return _options.WindowPredicate(hwnd, pid, className, windowTitle);
        }

        if (!string.Equals(className, "WINDOWSCLIENT", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return IsRobloxPlayerProcess(pid);
    }

    private static bool IsValidWindowBounds(User32.RECT rect) =>
        rect.Left > MinimizedCoordThreshold &&
        rect.Top > MinimizedCoordThreshold &&
        rect.Width > 0 &&
        rect.Height > 0;

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

        if (_robloxPidCache.Count >= MaxPidCacheEntries)
        {
            PrunePidCache(now);
        }

        _robloxPidCache[pid] = (new PidCacheEntry(isRoblox, now.Add(PidCacheTtl)));
        return isRoblox;
    }

    private void PrunePidCache(DateTime now)
    {
        foreach (var kvp in _robloxPidCache)
        {
            if (kvp.Value.ExpiresUtc <= now)
            {
                _robloxPidCache.TryRemove(kvp.Key, out _);
            }
        }

        if (_robloxPidCache.Count >= MaxPidCacheEntries)
        {
            foreach (var key in _robloxPidCache.Keys.Take(_robloxPidCache.Count - MaxPidCacheEntries / 2))
            {
                _robloxPidCache.TryRemove(key, out _);
            }
        }
    }

    private static IReadOnlyDictionary<string, string> BuildEffectivePlaceholders(
        IReadOnlyDictionary<string, string>? configured,
        string originalTitle)
    {
        var dict = configured != null
            ? new Dictionary<string, string>(configured, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!dict.ContainsKey("OriginalTitle"))
        {
            dict["OriginalTitle"] = originalTitle;
        }
        return dict;
    }

    private void ReleaseOwnedIcons(nint hWnd)
    {
        OwnedIconPair? pair;
        lock (_iconLock)
        {
            _ownedIcons.TryRemove(hWnd, out pair);
        }

        if (pair != null)
        {
            DestroyIconPair(pair, null);
        }
    }

    private void DestroyIconPair(OwnedIconPair oldPair, OwnedIconPair? keepPair)
    {
        if (oldPair.SmallIcon != nint.Zero &&
            oldPair.SmallIcon != keepPair?.SmallIcon &&
            oldPair.SmallIcon != keepPair?.LargeIcon)
        {
            _interop.DestroyIcon(oldPair.SmallIcon);
        }

        if (oldPair.LargeIcon != nint.Zero &&
            oldPair.LargeIcon != oldPair.SmallIcon &&
            oldPair.LargeIcon != keepPair?.SmallIcon &&
            oldPair.LargeIcon != keepPair?.LargeIcon)
        {
            _interop.DestroyIcon(oldPair.LargeIcon);
        }
    }

    private static bool HasValidIcoHeader(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < 6) return false;
            Span<byte> header = stackalloc byte[4];
            if (fs.Read(header) < 4) return false;
            // ICONDIR reserved=0x0000, type=0x0001 (little-endian: 00 00 01 00)
            return header[0] == 0x00 && header[1] == 0x00 && header[2] == 0x01 && header[3] == 0x00;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasValidPeHeader(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < 2) return false;
            Span<byte> header = stackalloc byte[2];
            if (fs.Read(header) < 2) return false;
            return header[0] == 0x4D && header[1] == 0x5A; // MZ
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        lock (_hookLock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        StopHook();

        foreach (var hWnd in _ownedIcons.Keys.ToList())
        {
            if (_interop.IsWindow(hWnd))
            {
                _interop.SendMessageTimeout(hWnd, User32.WM_SETICON, User32.ICON_SMALL, nint.Zero, 200);
                _interop.SendMessageTimeout(hWnd, User32.WM_SETICON, User32.ICON_BIG, nint.Zero, 200);
            }
            ReleaseOwnedIcons(hWnd);
        }
        _savedStates.Clear();
        _borderlessAppliedWindows.Clear();
        _windowPids.Clear();
        lock (_titleLock)
        {
            _originalTitles.Clear();
            _lastAppliedTitles.Clear();
            _inFlightTitleUpdates.Clear();
        }
        _robloxPidCache.Clear();

        GC.SuppressFinalize(this);
    }
}
