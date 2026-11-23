using System.Windows;
using System.Windows.Threading;
using BaBaBooeyStrap.App.Views.Dialogs;
using BaBaBooeyStrap.Core.Interop;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Models.Enums;
using BaBaBooeyStrap.Core.Storage;
using BaBaBooeyStrap.Integrations.Window;

namespace BaBaBooeyStrap.App.Services;

public sealed class WindowSwitcherCoordinator : IDisposable
{
    private readonly IRunningInstanceTracker _tracker;
    private readonly WindowFocusHelper _focusHelper;
    private readonly WindowTilingService _tilingService;
    private readonly IMonitorProvider _monitorProvider;
    private readonly IJsonManager<Settings> _settingsManager;
    private readonly IJsonManager<State> _stateManager;
    private readonly AccountWindowLayoutService _accountLayoutService;
    private readonly GlobalHotkeyService? _hotkeyService;
    private readonly NotifyIconWrapper? _trayWrapper;
    private readonly Dispatcher? _dispatcher;
    private readonly bool _enableFloatingWindows;
    private readonly object _stateSyncLock = new();

    private QuickSwitcherWindow? _quickSwitcher;
    private FloatingSwitcherBarWindow? _floatingBar;
    private WindowFocusHighlightOverlay? _highlightOverlay;
    private int _lastFocusedRobloxPid;
    private bool _floatingBarManuallyHidden;
    private bool _floatingBarManuallyShown;
    private volatile bool _disposed;

    public event EventHandler<IReadOnlyList<RunningRobloxInstance>>? InstancesChanged;

    public WindowSwitcherCoordinator(
        IRunningInstanceTracker tracker,
        WindowFocusHelper focusHelper,
        WindowTilingService tilingService,
        IMonitorProvider monitorProvider,
        IJsonManager<Settings> settingsManager,
        IJsonManager<State> stateManager,
        GlobalHotkeyService? hotkeyService = null,
        NotifyIconWrapper? trayWrapper = null,
        Dispatcher? dispatcher = null,
        bool enableFloatingWindows = true,
        AccountWindowLayoutService? accountLayoutService = null)
    {
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _focusHelper = focusHelper ?? throw new ArgumentNullException(nameof(focusHelper));
        _tilingService = tilingService ?? throw new ArgumentNullException(nameof(tilingService));
        _monitorProvider = monitorProvider ?? throw new ArgumentNullException(nameof(monitorProvider));
        _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
        _stateManager = stateManager ?? throw new ArgumentNullException(nameof(stateManager));
        _accountLayoutService = accountLayoutService ?? new AccountWindowLayoutService(monitorProvider: _monitorProvider);
        _hotkeyService = hotkeyService;
        _trayWrapper = trayWrapper;
        _dispatcher = dispatcher ?? Application.Current?.Dispatcher;
        _enableFloatingWindows = enableFloatingWindows;

        _tracker.InstancesChanged += OnTrackerInstancesChanged;
        _tracker.InstancesRefreshed += OnTrackerInstancesRefreshed;
        _tracker.FocusedInstanceChanged += OnTrackerFocusedChanged;

        if (_hotkeyService != null)
        {
            _hotkeyService.QuickSwitcherRequested += OnHotkeyQuickSwitcher;
            _hotkeyService.CycleNextWindowRequested += OnHotkeyCycleNext;
            _hotkeyService.CyclePreviousWindowRequested += OnHotkeyCyclePrevious;
            _hotkeyService.DirectSlotRequested += OnHotkeyDirectSlot;
        }
    }

    public IRunningInstanceTracker Tracker => _tracker;
    public IMonitorProvider MonitorProvider => _monitorProvider;
    public GlobalHotkeyService? HotkeyService => _hotkeyService;

    public bool IsFloatingBarVisible =>
        _floatingBar != null && _floatingBar.IsVisible;

    public void Initialize()
    {
        if (_disposed) return;

        if (_hotkeyService != null)
        {
            RunOnUi(() => _hotkeyService.ReloadFromSettings(_settingsManager.Prop));
        }

        _tracker.Start();
        EvaluateFloatingBarVisibility(_tracker.GetRunningInstances());
    }

    public IReadOnlyList<RunningRobloxInstance> GetRunningInstances() =>
        _tracker.GetRunningInstances();

    public IReadOnlyList<MonitorInfo> GetMonitors() =>
        _monitorProvider.GetMonitors();

    public void ShowQuickSwitcher()
    {
        if (_disposed || !_enableFloatingWindows) return;

        RunOnUi(() =>
        {
            if (_disposed) return;

            if (_quickSwitcher is { IsVisible: true })
            {
                _quickSwitcher.Hide();
                return;
            }

            _tracker.RefreshNow();
            var instances = _tracker.GetRunningInstances();
            var cursorMonitor = _monitorProvider.GetMonitorFromCursor();
            var monitorCount = _monitorProvider.GetMonitors().Count;

            _quickSwitcher ??= new QuickSwitcherWindow(
                onFocusInstance: FocusProcess,
                onMoveNextMonitor: MoveProcessToNextMonitor,
                onToggleMinimize: ToggleMinimizeProcess,
                onCloseInstance: CloseProcess,
                onTileAll: () => TileInstances());

            _quickSwitcher.UpdateAndShow(instances, cursorMonitor, monitorCount);
        });
    }

    public void CycleNextInstance() => CycleByOffset(1);

    public void CyclePreviousInstance() => CycleByOffset(-1);

    private void CycleByOffset(int delta)
    {
        if (_disposed) return;

        var instances = _tracker.GetRunningInstances();
        if (instances.Count == 0) return;

        if (instances.Count == 1)
        {
            FocusProcess(instances[0].ProcessId);
            return;
        }

        int currentIdx = -1;
        for (int i = 0; i < instances.Count; i++)
        {
            if (instances[i].IsFocused)
            {
                currentIdx = i;
                break;
            }
        }

        int lastFocusedPid = Volatile.Read(ref _lastFocusedRobloxPid);
        if (currentIdx < 0 && lastFocusedPid > 0)
        {
            for (int i = 0; i < instances.Count; i++)
            {
                if (instances[i].ProcessId == lastFocusedPid)
                {
                    currentIdx = i;
                    break;
                }
            }
        }

        int count = instances.Count;
        int nextIdx = currentIdx < 0
            ? (delta >= 0 ? 0 : count - 1)
            : (currentIdx + delta % count + count) % count;
        FocusProcess(instances[nextIdx].ProcessId);
    }

    public void FocusSlot(int slotIndex)
    {
        if (_disposed || slotIndex <= 0) return;

        var match = _tracker.GetRunningInstances().FirstOrDefault(i => i.SlotIndex == slotIndex);
        if (match != null)
        {
            FocusProcess(match.ProcessId);
        }
    }

    public void FocusProcess(int pid)
    {
        if (_disposed) return;

        var inst = _tracker.GetRunningInstances().FirstOrDefault(i => i.ProcessId == pid);
        if (inst == null) return;

        bool activated = _focusHelper.ActivateWindow(inst.WindowHandle, (uint)pid);
        if (activated)
        {
            Volatile.Write(ref _lastFocusedRobloxPid, pid);
        }
        _tracker.RefreshNow();

        if (activated)
        {
            var latest = _tracker.GetRunningInstances().FirstOrDefault(i => i.ProcessId == pid) ?? inst;
            ShowSwitchHighlightIfEnabled(latest);
        }
    }

    public void MoveProcessToMonitor(int pid, string deviceName)
    {
        if (_disposed) return;

        var inst = _tracker.GetRunningInstances().FirstOrDefault(i => i.ProcessId == pid);
        var monitor = _monitorProvider.FindMonitorByDeviceName(deviceName);
        if (inst == null || monitor == null) return;

        bool moved = _focusHelper.MoveWindowToMonitor(inst.WindowHandle, monitor, activateAfterMove: true, (uint)pid);
        if (moved)
        {
            Volatile.Write(ref _lastFocusedRobloxPid, pid);
        }
        _tracker.RefreshNow();

        if (moved)
        {
            var latest = _tracker.GetRunningInstances().FirstOrDefault(i => i.ProcessId == pid) ?? inst;
            ShowSwitchHighlightIfEnabled(latest);
        }
    }

    public void MoveProcessToNextMonitor(int pid)
    {
        if (_disposed) return;

        var monitors = _monitorProvider.GetMonitors();
        if (monitors.Count <= 1) return;

        var inst = _tracker.GetRunningInstances().FirstOrDefault(i => i.ProcessId == pid);
        if (inst == null) return;

        int currentIdx = -1;
        for (int i = 0; i < monitors.Count; i++)
        {
            if (string.Equals(monitors[i].DeviceName, inst.MonitorDeviceName, StringComparison.OrdinalIgnoreCase))
            {
                currentIdx = i;
                break;
            }
        }

        var nextMonitor = monitors[(currentIdx + 1) % monitors.Count];
        MoveProcessToMonitor(pid, nextMonitor.DeviceName);
    }

    public void ToggleMinimizeProcess(int pid)
    {
        if (_disposed) return;

        var inst = _tracker.GetRunningInstances().FirstOrDefault(i => i.ProcessId == pid);
        if (inst == null) return;

        _focusHelper.ToggleMinimizeRestore(inst.WindowHandle, (uint)pid);
        _tracker.RefreshNow();
    }

    public void MinimizeAll()
    {
        if (_disposed) return;

        foreach (var inst in _tracker.GetRunningInstances())
        {
            _focusHelper.MinimizeWindow(inst.WindowHandle, (uint)inst.ProcessId);
        }
        _tracker.RefreshNow();
    }

    public void CloseProcess(int pid)
    {
        if (_disposed) return;

        var inst = _tracker.GetRunningInstances().FirstOrDefault(i => i.ProcessId == pid);
        if (inst == null) return;

        _focusHelper.CloseWindow(inst.WindowHandle, (uint)pid);
    }

    public int TileInstances(
        WindowTilingMode? modeOverride = null,
        string? monitorDeviceOverride = null,
        int? mainPidOverride = null)
    {
        if (_disposed) return 0;

        var instances = _tracker.GetRunningInstances();
        if (instances.Count == 0) return 0;

        var settings = _settingsManager.Prop;
        var mode = modeOverride ?? settings.DefaultWindowTilingMode;
        var hwnds = instances.Select(i => i.WindowHandle).ToList();

        int count;
        switch (mode)
        {
            case WindowTilingMode.SpreadAcrossMonitors:
                count = _tilingService.TileSpreadAcrossMonitors(hwnds);
                break;

            case WindowTilingMode.MainAndSide:
                int mainLastFocusedPid = Volatile.Read(ref _lastFocusedRobloxPid);
                var mainInst = (mainPidOverride.HasValue
                    ? instances.FirstOrDefault(i => i.ProcessId == mainPidOverride.Value)
                    : null)
                    ?? instances.FirstOrDefault(i => i.IsFocused)
                    ?? (mainLastFocusedPid > 0
                        ? instances.FirstOrDefault(i => i.ProcessId == mainLastFocusedPid)
                        : null)
                    ?? instances[0];

                var sideHwnds = instances
                    .Where(i => i.ProcessId != mainInst.ProcessId)
                    .Select(i => i.WindowHandle)
                    .ToList();

                var mainMonitor = _monitorProvider.FindMonitorByDeviceName(settings.MainSidePrimaryMonitorDevice);
                var sideMonitor = _monitorProvider.FindMonitorByDeviceName(settings.MainSideSecondaryMonitorDevice);
                count = _tilingService.TileMainAndSide(mainInst.WindowHandle, sideHwnds, mainMonitor, sideMonitor);
                break;

            case WindowTilingMode.SingleMonitorGrid:
            default:
                string? targetDevice = !string.IsNullOrWhiteSpace(monitorDeviceOverride)
                    ? monitorDeviceOverride
                    : settings.PreferredTilingMonitorDevice;
                var targetMonitor = _monitorProvider.FindMonitorByDeviceName(targetDevice)
                    ?? _monitorProvider.GetMonitorFromCursor();
                count = _tilingService.TileSingleMonitor(hwnds, targetMonitor);
                break;
        }

        _tracker.RefreshNow();
        return count;
    }

    public void ToggleFloatingBar()
    {
        if (_disposed || !_enableFloatingWindows) return;

        RunOnUi(() =>
        {
            if (_disposed) return;

            if (IsFloatingBarVisible)
            {
                _floatingBarManuallyHidden = true;
                _floatingBarManuallyShown = false;
                _floatingBar?.Hide();
            }
            else
            {
                _floatingBarManuallyHidden = false;
                _floatingBarManuallyShown = true;
                ShowFloatingBar(_tracker.GetRunningInstances());
            }
        });
    }

    public void HideFloatingBarForSession()
    {
        if (_disposed || !_enableFloatingWindows) return;

        RunOnUi(() =>
        {
            _floatingBarManuallyHidden = true;
            _floatingBarManuallyShown = false;
            _floatingBar?.Hide();
        });
    }

    public void ResetFloatingBarPosition()
    {
        if (_disposed) return;

        if (!_enableFloatingWindows)
        {
            ResetFloatingBarStateSynchronized();
            return;
        }

        RunOnUi(() =>
        {
            if (_floatingBar != null)
            {
                _floatingBar.ResetPosition();
            }
            else
            {
                ResetFloatingBarStateSynchronized();
            }
        });
    }

    private void ResetFloatingBarStateSynchronized()
    {
        lock (_stateSyncLock)
        {
            _stateManager.Prop.FloatingBarMonitorDevice = null;
            _stateManager.Prop.FloatingBarLeft = null;
            _stateManager.Prop.FloatingBarTop = null;
            _stateManager.Prop.FloatingBarCollapsed = false;
            _stateManager.Save();
        }
    }

    private void SaveStateSynchronized()
    {
        lock (_stateSyncLock)
        {
            _stateManager.Save();
        }
    }

    public void ReloadSettings()
    {
        if (_disposed) return;

        RunOnUi(() =>
        {
            if (_disposed) return;
            _hotkeyService?.ReloadFromSettings(_settingsManager.Prop);
            _tracker.RefreshNow();
            EvaluateFloatingBarVisibility(_tracker.GetRunningInstances());
        });
    }

    private void OnTrackerInstancesChanged(object? sender, IReadOnlyList<RunningRobloxInstance> instances)
    {
        var focused = instances.FirstOrDefault(i => i.IsFocused);
        if (focused != null)
        {
            Volatile.Write(ref _lastFocusedRobloxPid, focused.ProcessId);
        }

        try { _trayWrapper?.RefreshRunningInstancesMenu(); } catch { }
        RaiseInstancesChangedSafe(instances);

        if (_enableFloatingWindows)
        {
            RunOnUi(() =>
            {
                if (_disposed) return;
                _quickSwitcher?.RefreshInstances(instances, _monitorProvider.GetMonitors().Count);
                _floatingBar?.UpdateInstances(instances);
                EvaluateFloatingBarVisibility(instances);
            });
        }
    }

    private void RaiseInstancesChangedSafe(IReadOnlyList<RunningRobloxInstance> instances)
    {
        var handler = InstancesChanged;
        if (handler == null) return;

        foreach (EventHandler<IReadOnlyList<RunningRobloxInstance>> subscriber in handler.GetInvocationList())
        {
            try
            {
                subscriber(this, instances);
            }
            catch (Exception ex)
            {
                BaBaBooeyStrap.Core.Logging.Logger.Current.Debug("WindowSwitcherCoordinator", $"InstancesChanged subscriber error: {ex.Message}");
            }
        }
    }

    private void OnTrackerInstancesRefreshed(object? sender, IReadOnlyList<RunningRobloxInstance> instances)
    {
        if (_disposed) return;
        lock (_stateSyncLock)
        {
            _accountLayoutService.ProcessInstances(instances, _settingsManager.Prop, _stateManager.Prop, () => _stateManager.Save());
        }
    }

    private void OnTrackerFocusedChanged(object? sender, RunningRobloxInstance? focused)
    {
        if (focused != null)
        {
            Volatile.Write(ref _lastFocusedRobloxPid, focused.ProcessId);
        }
    }

    private void OnHotkeyQuickSwitcher(object? sender, EventArgs e) => ShowQuickSwitcher();
    private void OnHotkeyCycleNext(object? sender, EventArgs e) => CycleNextInstance();
    private void OnHotkeyCyclePrevious(object? sender, EventArgs e) => CyclePreviousInstance();

    private void OnHotkeyDirectSlot(object? sender, int slot)
    {
        if (_disposed || slot is < 1 or > 9) return;
        RunOnUi(() => FocusSlot(slot));
    }

    private void ShowSwitchHighlightIfEnabled(RunningRobloxInstance instance)
    {
        if (!_enableFloatingWindows || _disposed || !_settingsManager.Prop.EnableWindowSwitchHighlight)
        {
            return;
        }

        RunOnUi(() =>
        {
            if (_disposed) return;
            _highlightOverlay ??= new WindowFocusHighlightOverlay();
            _highlightOverlay.ShowForWindow(instance.WindowHandle, instance);
        });
    }

    private void EvaluateFloatingBarVisibility(IReadOnlyList<RunningRobloxInstance> instances)
    {
        if (!_enableFloatingWindows || _disposed) return;

        var settings = _settingsManager.Prop;
        if (!settings.FloatingSwitcherBarEnabled)
        {
            _floatingBar?.Hide();
            return;
        }

        if (instances.Count == 0)
        {
            _floatingBarManuallyHidden = false;
            _floatingBarManuallyShown = false;
            _floatingBar?.Hide();
            return;
        }

        if (_floatingBarManuallyShown)
        {
            ShowFloatingBar(instances);
            return;
        }

        if (_floatingBarManuallyHidden)
        {
            _floatingBar?.Hide();
            return;
        }

        if (settings.FloatingSwitcherBarAutoShow)
        {
            if (instances.Count >= 2)
            {
                ShowFloatingBar(instances);
            }
            else
            {
                _floatingBar?.Hide();
            }
        }
    }

    private void ShowFloatingBar(IReadOnlyList<RunningRobloxInstance> instances)
    {
        _floatingBar ??= new FloatingSwitcherBarWindow(
            _monitorProvider,
            _stateManager,
            onFocusInstance: FocusProcess,
            onMoveToMonitor: MoveProcessToMonitor,
            onToggleMinimize: ToggleMinimizeProcess,
            onCloseInstance: CloseProcess,
            onOpenQuickSwitcher: ShowQuickSwitcher,
            onTile: (mode, device) => TileInstances(mode, device),
            onMinimizeAll: MinimizeAll,
            onHideBar: HideFloatingBarForSession);

        _floatingBar.UpdateInstances(instances);
        _floatingBar.EnsureVisible();
    }

    private void RunOnUi(Action action)
    {
        try
        {
            if (_dispatcher == null)
            {
                action();
                return;
            }

            if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
            {
                return;
            }

            if (_dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                _ = _dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        BaBaBooeyStrap.Core.Logging.Logger.Current.Debug("WindowSwitcherCoordinator", $"UI dispatch action error: {ex.Message}");
                    }
                });
            }
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException or InvalidOperationException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _tracker.InstancesChanged -= OnTrackerInstancesChanged;
        _tracker.InstancesRefreshed -= OnTrackerInstancesRefreshed;
        _tracker.FocusedInstanceChanged -= OnTrackerFocusedChanged;

        SaveSynchronizedFlush();

        if (_hotkeyService != null)
        {
            _hotkeyService.QuickSwitcherRequested -= OnHotkeyQuickSwitcher;
            _hotkeyService.CycleNextWindowRequested -= OnHotkeyCycleNext;
            _hotkeyService.CyclePreviousWindowRequested -= OnHotkeyCyclePrevious;
            _hotkeyService.DirectSlotRequested -= OnHotkeyDirectSlot;
        }

        RunOnUi(() =>
        {
            try { _quickSwitcher?.ForceClose(); } catch { }
            try { _floatingBar?.ForceClose(); } catch { }
            try { _highlightOverlay?.ForceClose(); } catch { }
            _quickSwitcher = null;
            _floatingBar = null;
            _highlightOverlay = null;
        });

        GC.SuppressFinalize(this);
    }

    private void SaveSynchronizedFlush()
    {
        lock (_stateSyncLock)
        {
            _accountLayoutService.FlushIfDirty(() => _stateManager.Save());
        }
    }
}
