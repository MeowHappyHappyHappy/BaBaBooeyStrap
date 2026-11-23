using BaBaBooeyStrap.Core.Interop;
using BaBaBooeyStrap.Core.Logging;
using BaBaBooeyStrap.Core.Models;

namespace BaBaBooeyStrap.Integrations.Window;

public sealed class AccountWindowLayoutService
{
    private const int MinWindowWidth = 200;
    private const int MinWindowHeight = 150;
    private const int MinVisibleOverlapWidth = 100;
    private const int MinVisibleOverlapHeight = 80;
    private static readonly TimeSpan SaveDebounceInterval = TimeSpan.FromSeconds(3);

    private readonly IWin32WindowInterop _interop;
    private readonly IMonitorProvider _monitorProvider;
    private readonly ILogger _logger;
    private readonly object _lock = new();

    private readonly HashSet<(nint Hwnd, string AccountKey)> _restoredWindowKeys = new();
    private HashSet<nint> _activeTrackedHwnds = new();
    private bool _dirty;
    private DateTimeOffset _lastSaveUtc = DateTimeOffset.MinValue;

    public AccountWindowLayoutService(
        IWin32WindowInterop? interop = null,
        IMonitorProvider? monitorProvider = null,
        ILogger? logger = null)
    {
        _interop = interop ?? new Win32WindowInterop();
        _monitorProvider = monitorProvider ?? new Win32MonitorProvider();
        _logger = logger ?? Logger.Current;
    }

    public static string? GetAccountKey(Guid? accountId, long? userId)
    {
        if (accountId.HasValue && accountId.Value != Guid.Empty)
        {
            return accountId.Value.ToString("N");
        }

        if (userId.HasValue && userId.Value > 0)
        {
            return $"user:{userId.Value}";
        }

        return null;
    }

    public AccountWindowBounds? CaptureBounds(nint hwnd)
    {
        if (hwnd == nint.Zero || !_interop.IsWindow(hwnd))
        {
            return null;
        }

        // Ignore minimized state so we preserve the last normal/maximized bounds
        if (_interop.IsIconic(hwnd))
        {
            return null;
        }

        if (!_interop.TryGetNormalWindowRect(hwnd, out var rect))
        {
            return null;
        }

        if (rect.Width < MinWindowWidth || rect.Height < MinWindowHeight)
        {
            return null;
        }

        bool isMaximized = _interop.IsZoomed(hwnd);
        var monitor = _monitorProvider.GetMonitorFromWindow(hwnd);

        return new AccountWindowBounds
        {
            Left = rect.Left,
            Top = rect.Top,
            Width = rect.Width,
            Height = rect.Height,
            IsMaximized = isMaximized,
            MonitorDeviceName = monitor.DeviceName,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
    }

    public static User32.RECT ComputeRestoredRect(
        AccountWindowBounds saved,
        IReadOnlyList<MonitorInfo> monitors,
        MonitorInfo primaryMonitor)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(primaryMonitor);

        long savedWidthLong = Math.Clamp((long)saved.Width, MinWindowWidth, 32768L);
        long savedHeightLong = Math.Clamp((long)saved.Height, MinWindowHeight, 32768L);
        long savedRightLong = (long)saved.Left + savedWidthLong;
        long savedBottomLong = (long)saved.Top + savedHeightLong;

        bool canFitInInt32 = savedRightLong <= int.MaxValue && savedBottomLong <= int.MaxValue &&
                             saved.Left >= int.MinValue && saved.Top >= int.MinValue;

        if (canFitInInt32)
        {
            var savedRect = new User32.RECT
            {
                Left = saved.Left,
                Top = saved.Top,
                Right = (int)savedRightLong,
                Bottom = (int)savedBottomLong
            };

            bool targetMonitorExists = string.IsNullOrWhiteSpace(saved.MonitorDeviceName) ||
                monitors.Any(m => string.Equals(m.DeviceName, saved.MonitorDeviceName, StringComparison.OrdinalIgnoreCase));

            if (targetMonitorExists && IsVisibleOnAnyMonitor(savedRect, monitors))
            {
                return savedRect;
            }
        }

        var work = primaryMonitor.WorkArea;
        int workWidth = Math.Max(MinWindowWidth, work.Width);
        int workHeight = Math.Max(MinWindowHeight, work.Height);

        int clampedWidth = Math.Clamp((int)savedWidthLong, MinWindowWidth, workWidth);
        int clampedHeight = Math.Clamp((int)savedHeightLong, MinWindowHeight, workHeight);

        int left = work.Left + (workWidth - clampedWidth) / 2;
        int top = work.Top + (workHeight - clampedHeight) / 2;

        return new User32.RECT
        {
            Left = left,
            Top = top,
            Right = left + clampedWidth,
            Bottom = top + clampedHeight
        };
    }

    public bool TryRestoreWindow(nint hwnd, AccountWindowBounds saved, Settings settings)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(settings);

        if (hwnd == nint.Zero || !_interop.IsWindow(hwnd))
        {
            return false;
        }

        if (!settings.RestoreAccountWindowBounds || settings.FakeBorderlessFullscreen)
        {
            return false;
        }

        var monitors = _monitorProvider.GetMonitors();
        var primary = _monitorProvider.GetPrimaryMonitor();
        var targetRect = ComputeRestoredRect(saved, monitors, primary);

        if (_interop.IsZoomed(hwnd) || _interop.IsIconic(hwnd))
        {
            _interop.ShowWindow(hwnd, User32.SW_RESTORE);
        }

        bool moved = _interop.SetWindowPos(
            hwnd,
            nint.Zero,
            targetRect.Left,
            targetRect.Top,
            targetRect.Width,
            targetRect.Height,
            User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);

        if (saved.IsMaximized)
        {
            _interop.ShowWindow(hwnd, User32.SW_SHOWMAXIMIZED);
        }

        return moved;
    }

    public void ProcessInstances(
        IReadOnlyList<RunningRobloxInstance> instances,
        Settings settings,
        State state,
        Action saveState)
    {
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(saveState);

        bool canRestore = settings.RestoreAccountWindowBounds && !settings.FakeBorderlessFullscreen;
        var currentHwnds = new HashSet<nint>();
        var workItems = new List<(nint Hwnd, string AccountKey, AccountWindowBounds? SavedToRestore)>();

        lock (_lock)
        {
            state.AccountWindowBounds ??= new Dictionary<string, AccountWindowBounds>(StringComparer.OrdinalIgnoreCase);

            foreach (var inst in instances)
            {
                if (inst.WindowHandle == nint.Zero)
                {
                    continue;
                }

                currentHwnds.Add(inst.WindowHandle);
                string? accountKey = GetAccountKey(inst.AccountId, inst.UserId);
                if (accountKey == null)
                {
                    continue;
                }

                var restoreKey = (inst.WindowHandle, accountKey);
                AccountWindowBounds? savedToRestore = null;
                if (canRestore &&
                    !_restoredWindowKeys.Contains(restoreKey) &&
                    state.AccountWindowBounds.TryGetValue(accountKey, out var saved))
                {
                    savedToRestore = saved;
                }

                workItems.Add((inst.WindowHandle, accountKey, savedToRestore));
            }
        }

        // Perform all Win32 restoration and bounds capture outside _lock
        var restoredSuccessKeys = new List<(nint Hwnd, string AccountKey)>();
        var capturedResults = new List<(string AccountKey, AccountWindowBounds Bounds)>();

        foreach (var (hwnd, accountKey, savedToRestore) in workItems)
        {
            if (savedToRestore != null)
            {
                try
                {
                    if (TryRestoreWindow(hwnd, savedToRestore, settings))
                    {
                        restoredSuccessKeys.Add((hwnd, accountKey));
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug("AccountWindowLayoutService", $"Failed restoring bounds for {accountKey}: {ex.Message}");
                }
                continue;
            }

            var captured = CaptureBounds(hwnd);
            if (captured != null)
            {
                capturedResults.Add((accountKey, captured));
            }
        }

        bool shouldSave = false;
        lock (_lock)
        {
            foreach (var key in restoredSuccessKeys)
            {
                _restoredWindowKeys.Add(key);
            }

            foreach (var (accountKey, captured) in capturedResults)
            {
                if (!state.AccountWindowBounds.TryGetValue(accountKey, out var existing) ||
                    !BoundsEquivalent(existing, captured))
                {
                    state.AccountWindowBounds[accountKey] = captured;
                    _dirty = true;
                }
            }

            _restoredWindowKeys.RemoveWhere(k => !currentHwnds.Contains(k.Hwnd));
            bool anyTrackedClosed = _activeTrackedHwnds.Any(h => !currentHwnds.Contains(h));
            _activeTrackedHwnds = currentHwnds;

            var now = DateTimeOffset.UtcNow;
            if (_dirty && (anyTrackedClosed || (now - _lastSaveUtc) >= SaveDebounceInterval))
            {
                shouldSave = true;
                _dirty = false;
                _lastSaveUtc = now;
            }
        }

        if (shouldSave)
        {
            try
            {
                saveState();
            }
            catch (Exception ex)
            {
                _logger.Debug("AccountWindowLayoutService", $"Failed saving account window bounds: {ex.Message}");
            }
        }
    }

    public void FlushIfDirty(Action saveState)
    {
        ArgumentNullException.ThrowIfNull(saveState);

        bool shouldSave;
        lock (_lock)
        {
            shouldSave = _dirty;
            if (shouldSave)
            {
                _dirty = false;
                _lastSaveUtc = DateTimeOffset.UtcNow;
            }
        }

        if (shouldSave)
        {
            try
            {
                saveState();
            }
            catch (Exception ex)
            {
                _logger.Debug("AccountWindowLayoutService", $"Failed flushing account window bounds: {ex.Message}");
            }
        }
    }

    private static bool IsVisibleOnAnyMonitor(User32.RECT rect, IReadOnlyList<MonitorInfo> monitors)
    {
        long rectW = Math.Max(0L, (long)rect.Right - rect.Left);
        long rectH = Math.Max(0L, (long)rect.Bottom - rect.Top);

        foreach (var mon in monitors)
        {
            var work = mon.WorkArea;
            long overlapW = Math.Min((long)rect.Right, work.Right) - Math.Max((long)rect.Left, work.Left);
            long overlapH = Math.Min((long)rect.Bottom, work.Bottom) - Math.Max((long)rect.Top, work.Top);

            if (overlapW >= Math.Min(MinVisibleOverlapWidth, rectW) &&
                overlapH >= Math.Min(MinVisibleOverlapHeight, rectH))
            {
                return true;
            }
        }

        return false;
    }

    private static bool BoundsEquivalent(AccountWindowBounds a, AccountWindowBounds b) =>
        a.Left == b.Left &&
        a.Top == b.Top &&
        a.Width == b.Width &&
        a.Height == b.Height &&
        a.IsMaximized == b.IsMaximized &&
        string.Equals(a.MonitorDeviceName, b.MonitorDeviceName, StringComparison.OrdinalIgnoreCase);
}
