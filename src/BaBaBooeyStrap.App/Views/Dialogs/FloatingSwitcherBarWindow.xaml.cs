using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using BaBaBooeyStrap.App.Services;
using BaBaBooeyStrap.Core.Interop;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Models.Enums;
using BaBaBooeyStrap.Core.Storage;
using BaBaBooeyStrap.Integrations.Window;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace BaBaBooeyStrap.App.Views.Dialogs;

public partial class FloatingSwitcherBarWindow : Window
{
    private sealed class PillItem
    {
        private static readonly Brush FocusedBg = new SolidColorBrush(Color.FromArgb(0xCC, 0x25, 0x63, 0xEB));
        private static readonly Brush FocusedBorder = new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA));
        private static readonly Brush MinimizedBg = new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF));
        private static readonly Brush NormalBg = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
        private static readonly Brush NormalBorder = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        private static readonly Brush DimForeground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));

        public required RunningRobloxInstance Instance { get; init; }
        public int ProcessId => Instance.ProcessId;
        public string IndexPrefix => $"#{Instance.SlotIndex}";
        public string DisplayLabel => Instance.EffectiveLabel;
        public string ToolTipText
        {
            get
            {
                string state = Instance.IsFocused
                    ? LocalizationService.Instance.GetString("Str_QuickSwitcher_ActiveBadge")
                    : (Instance.IsMinimized
                        ? LocalizationService.Instance.GetString("Str_QuickSwitcher_MinimizedState")
                        : LocalizationService.Instance.GetString("Str_Common_Ready"));
                return $"{Instance.DetailLabel}\n#{Instance.SlotIndex} · {Instance.MonitorBadge} · {state}";
            }
        }
        public Brush BackgroundBrush => Instance.IsFocused ? FocusedBg : (Instance.IsMinimized ? MinimizedBg : NormalBg);
        public Brush BorderBrush => Instance.IsFocused ? FocusedBorder : NormalBorder;
        public Brush ForegroundBrush => Instance.IsMinimized && !Instance.IsFocused ? DimForeground : Brushes.White;
    }

    private readonly IMonitorProvider _monitorProvider;
    private readonly IJsonManager<State> _stateManager;
    private readonly Action<int> _onFocusInstance;
    private readonly Action<int, string> _onMoveToMonitor;
    private readonly Action<int> _onToggleMinimize;
    private readonly Action<int> _onCloseInstance;
    private readonly Action _onOpenQuickSwitcher;
    private readonly Action<WindowTilingMode, string?> _onTile;
    private readonly Action _onMinimizeAll;
    private readonly Action _onHideBar;

    private IReadOnlyList<RunningRobloxInstance> _instances = Array.Empty<RunningRobloxInstance>();
    private bool _isCollapsed;
    private bool _hasRestoredPosition;
    private bool _allowClose;

    private bool _isDragging;
    private User32.POINT _dragStartCursor;
    private User32.RECT _dragStartWindowRect;

    public FloatingSwitcherBarWindow(
        IMonitorProvider monitorProvider,
        IJsonManager<State> stateManager,
        Action<int> onFocusInstance,
        Action<int, string> onMoveToMonitor,
        Action<int> onToggleMinimize,
        Action<int> onCloseInstance,
        Action onOpenQuickSwitcher,
        Action<WindowTilingMode, string?> onTile,
        Action onMinimizeAll,
        Action onHideBar)
    {
        _monitorProvider = monitorProvider ?? throw new ArgumentNullException(nameof(monitorProvider));
        _stateManager = stateManager ?? throw new ArgumentNullException(nameof(stateManager));
        _onFocusInstance = onFocusInstance ?? throw new ArgumentNullException(nameof(onFocusInstance));
        _onMoveToMonitor = onMoveToMonitor ?? throw new ArgumentNullException(nameof(onMoveToMonitor));
        _onToggleMinimize = onToggleMinimize ?? throw new ArgumentNullException(nameof(onToggleMinimize));
        _onCloseInstance = onCloseInstance ?? throw new ArgumentNullException(nameof(onCloseInstance));
        _onOpenQuickSwitcher = onOpenQuickSwitcher ?? throw new ArgumentNullException(nameof(onOpenQuickSwitcher));
        _onTile = onTile ?? throw new ArgumentNullException(nameof(onTile));
        _onMinimizeAll = onMinimizeAll ?? throw new ArgumentNullException(nameof(onMinimizeAll));
        _onHideBar = onHideBar ?? throw new ArgumentNullException(nameof(onHideBar));

        InitializeComponent();
        _isCollapsed = _stateManager.Prop.FloatingBarCollapsed;
        ApplyCollapsedState();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        nint hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != nint.Zero)
        {
            long exStyle = User32.GetWindowLongPtr(hwnd, User32.GWL_EXSTYLE).ToInt64();
            exStyle |= User32.WS_EX_NOACTIVATE | User32.WS_EX_TOOLWINDOW;
            User32.SetWindowLongPtr(hwnd, User32.GWL_EXSTYLE, unchecked((nint)exStyle));
        }
    }

    public void UpdateInstances(IReadOnlyList<RunningRobloxInstance> instances)
    {
        _instances = instances ?? Array.Empty<RunningRobloxInstance>();
        AccountsList.ItemsSource = _instances.Select(i => new PillItem { Instance = i }).ToList();
        NoInstancesLabel.Visibility = _instances.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateCollapseLabel();
    }

    public void EnsureVisible()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (!_hasRestoredPosition)
        {
            _hasRestoredPosition = true;
            Dispatcher.BeginInvoke(RestoreOrCenterPosition);
        }
    }

    public void ResetPosition()
    {
        _stateManager.Prop.FloatingBarMonitorDevice = null;
        _stateManager.Prop.FloatingBarLeft = null;
        _stateManager.Prop.FloatingBarTop = null;
        _stateManager.Prop.FloatingBarCollapsed = false;
        _stateManager.Save();

        _isCollapsed = false;
        ApplyCollapsedState();

        if (IsVisible)
        {
            RestoreOrCenterPosition();
        }
        else
        {
            _hasRestoredPosition = false;
        }
    }

    public void ForceClose()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    private void RestoreOrCenterPosition()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == nint.Zero) return;

        UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(this);
        int barWidthPx = Math.Max(80, (int)Math.Round(ActualWidth * dpi.DpiScaleX));
        int barHeightPx = Math.Max(36, (int)Math.Round(ActualHeight * dpi.DpiScaleY));

        var state = _stateManager.Prop;
        MonitorInfo? savedMonitor = _monitorProvider.FindMonitorByDeviceName(state.FloatingBarMonitorDevice);

        int targetX;
        int targetY;

        if (savedMonitor != null && state.FloatingBarLeft.HasValue && state.FloatingBarTop.HasValue)
        {
            var work = savedMonitor.WorkArea;
            int maxX = Math.Max(work.Left, work.Right - barWidthPx);
            int maxY = Math.Max(work.Top, work.Bottom - barHeightPx);
            targetX = Math.Clamp(work.Left + (int)Math.Round(state.FloatingBarLeft.Value), work.Left, maxX);
            targetY = Math.Clamp(work.Top + (int)Math.Round(state.FloatingBarTop.Value), work.Top, maxY);
        }
        else
        {
            var primary = _monitorProvider.GetPrimaryMonitor();
            var work = primary.WorkArea;
            targetX = work.Left + Math.Max(0, (work.Width - barWidthPx) / 2);
            targetY = work.Top + 10;
        }

        User32.SetWindowPos(
            hwnd,
            User32.HWND_TOPMOST,
            targetX,
            targetY,
            0,
            0,
            User32.SWP_NOSIZE | User32.SWP_NOACTIVATE | User32.SWP_SHOWWINDOW);
    }

    private void ApplyCollapsedState()
    {
        ExpandedPanel.Visibility = _isCollapsed ? Visibility.Collapsed : Visibility.Visible;
        UpdateCollapseLabel();
    }

    private void UpdateCollapseLabel()
    {
        CollapseButtonLabel.Text = _isCollapsed ? $"▶ [{_instances.Count}]" : "◀";
    }

    private void DragHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == nint.Zero) return;

        if (User32.GetCursorPos(out _dragStartCursor) && User32.GetWindowRect(hwnd, out _dragStartWindowRect))
        {
            _isDragging = true;
            DragHandle.CaptureMouse();
            e.Handled = true;
        }
    }

    private void DragHandle_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging) return;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == nint.Zero) return;

        if (User32.GetCursorPos(out var current))
        {
            int dx = current.X - _dragStartCursor.X;
            int dy = current.Y - _dragStartCursor.Y;
            User32.SetWindowPos(
                hwnd,
                User32.HWND_TOPMOST,
                _dragStartWindowRect.Left + dx,
                _dragStartWindowRect.Top + dy,
                0,
                0,
                User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
        }
    }

    private void DragHandle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;
        DragHandle.ReleaseMouseCapture();
        e.Handled = true;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != nint.Zero && User32.GetWindowRect(hwnd, out var rect))
        {
            var monitor = _monitorProvider.GetMonitorFromWindow(hwnd);
            var work = monitor.WorkArea;
            int barW = Math.Max(40, rect.Width);
            int barH = Math.Max(32, rect.Height);
            int clampedX = Math.Clamp(rect.Left, work.Left, Math.Max(work.Left, work.Right - barW));
            int clampedY = Math.Clamp(rect.Top, work.Top, Math.Max(work.Top, work.Bottom - barH));

            if (clampedX != rect.Left || clampedY != rect.Top)
            {
                User32.SetWindowPos(hwnd, User32.HWND_TOPMOST, clampedX, clampedY, 0, 0, User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
            }

            _stateManager.Prop.FloatingBarMonitorDevice = monitor.DeviceName;
            _stateManager.Prop.FloatingBarLeft = clampedX - work.Left;
            _stateManager.Prop.FloatingBarTop = clampedY - work.Top;
            _stateManager.Save();
        }
    }

    private void CollapseButton_Click(object sender, RoutedEventArgs e)
    {
        _isCollapsed = !_isCollapsed;
        _stateManager.Prop.FloatingBarCollapsed = _isCollapsed;
        _stateManager.Save();
        ApplyCollapsedState();
    }

    private void AccountPill_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: int pid })
        {
            _onFocusInstance(pid);
        }
    }

    private void AccountPill_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int pid }) return;
        var instance = _instances.FirstOrDefault(i => i.ProcessId == pid);
        if (instance == null) return;

        var menu = new ContextMenu();

        var focusItem = new MenuItem { Header = LocalizationService.Instance.GetString("Str_Switcher_FocusWindow") };
        focusItem.Click += (_, _) => _onFocusInstance(pid);
        menu.Items.Add(focusItem);

        var minItem = new MenuItem
        {
            Header = LocalizationService.Instance.GetString(
                instance.IsMinimized ? "Str_Switcher_RestoreWindow" : "Str_Switcher_MinimizeWindow")
        };
        minItem.Click += (_, _) => _onToggleMinimize(pid);
        menu.Items.Add(minItem);

        var monitors = _monitorProvider.GetMonitors();
        if (monitors.Count > 1)
        {
            var moveMenu = new MenuItem { Header = LocalizationService.Instance.GetString("Str_Switcher_MoveToMonitor") };
            foreach (var mon in monitors)
            {
                var capturedMon = mon;
                var monItem = new MenuItem
                {
                    Header = mon.DisplayLabel,
                    IsChecked = string.Equals(mon.DeviceName, instance.MonitorDeviceName, StringComparison.OrdinalIgnoreCase)
                };
                monItem.Click += (_, _) => _onMoveToMonitor(pid, capturedMon.DeviceName);
                moveMenu.Items.Add(monItem);
            }
            menu.Items.Add(moveMenu);
        }

        menu.Items.Add(new Separator());

        var closeItem = new MenuItem { Header = LocalizationService.Instance.GetString("Str_Switcher_CloseWindow") };
        closeItem.Click += (_, _) => _onCloseInstance(pid);
        menu.Items.Add(closeItem);

        menu.PlacementTarget = sender as UIElement;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void QuickSwitcherBtn_Click(object sender, RoutedEventArgs e) => _onOpenQuickSwitcher();

    private void TileMenuBtn_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();

        var monitors = _monitorProvider.GetMonitors();
        if (monitors.Count <= 1)
        {
            var grid = new MenuItem { Header = LocalizationService.Instance.GetString("Str_Tiling_SingleMonitorGrid") };
            grid.Click += (_, _) => _onTile(WindowTilingMode.SingleMonitorGrid, null);
            menu.Items.Add(grid);
        }
        else
        {
            var gridSub = new MenuItem { Header = LocalizationService.Instance.GetString("Str_Tiling_SingleMonitorGrid") };
            var cursorGrid = new MenuItem { Header = LocalizationService.Instance.GetString("Str_Tiling_MonitorCursorAuto") };
            cursorGrid.Click += (_, _) => _onTile(WindowTilingMode.SingleMonitorGrid, null);
            gridSub.Items.Add(cursorGrid);
            gridSub.Items.Add(new Separator());
            foreach (var mon in monitors)
            {
                var captured = mon;
                var mItem = new MenuItem { Header = mon.DisplayLabel };
                mItem.Click += (_, _) => _onTile(WindowTilingMode.SingleMonitorGrid, captured.DeviceName);
                gridSub.Items.Add(mItem);
            }
            menu.Items.Add(gridSub);

            var spread = new MenuItem { Header = LocalizationService.Instance.GetString("Str_Tiling_SpreadAcrossMonitors") };
            spread.Click += (_, _) => _onTile(WindowTilingMode.SpreadAcrossMonitors, null);
            menu.Items.Add(spread);

            var mainSide = new MenuItem { Header = LocalizationService.Instance.GetString("Str_Tiling_MainAndSide") };
            mainSide.Click += (_, _) => _onTile(WindowTilingMode.MainAndSide, null);
            menu.Items.Add(mainSide);
        }

        menu.PlacementTarget = TileMenuBtn;
        menu.IsOpen = true;
    }

    private void MinimizeAllBtn_Click(object sender, RoutedEventArgs e) => _onMinimizeAll();

    private void HideBarBtn_Click(object sender, RoutedEventArgs e) => _onHideBar();
}
