using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BaBaBooeyStrap.App.Services;
using BaBaBooeyStrap.Core.Interop;
using BaBaBooeyStrap.Integrations.Window;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace BaBaBooeyStrap.App.Views.Dialogs;

public partial class QuickSwitcherWindow : Window
{
    private sealed class SwitcherRowItem
    {
        private static readonly Brush FocusedSlotBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
        private static readonly Brush NormalSlotBrush = new SolidColorBrush(Color.FromArgb(0x45, 0xFF, 0xFF, 0xFF));

        public required RunningRobloxInstance Instance { get; init; }
        public int ProcessId => Instance.ProcessId;
        public int SlotIndex => Instance.SlotIndex;
        public string SlotText => $"#{Instance.SlotIndex}";
        public string PrimaryTitle => Instance.EffectiveLabel;
        public string MonitorBadge => Instance.MonitorBadge;
        public Brush SlotBackground => Instance.IsFocused ? FocusedSlotBrush : NormalSlotBrush;
        public Visibility FocusedVisibility => Instance.IsFocused ? Visibility.Visible : Visibility.Collapsed;
        public Visibility MoveMonitorVisibility { get; init; }
        public string Subtitle
        {
            get
            {
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(Instance.Notes))
                {
                    parts.Add(Instance.Notes!);
                }
                parts.Add($"PID {Instance.ProcessId}");
                if (Instance.IsMinimized)
                {
                    parts.Add(LocalizationService.Instance.GetString("Str_QuickSwitcher_MinimizedState"));
                }
                if (Instance.IsExternal)
                {
                    parts.Add(LocalizationService.Instance.GetString("Str_QuickSwitcher_ExternalState"));
                }
                return string.Join(" · ", parts);
            }
        }
    }

    private readonly Action<int> _onFocusInstance;
    private readonly Action<int> _onMoveNextMonitor;
    private readonly Action<int> _onToggleMinimize;
    private readonly Action<int> _onCloseInstance;
    private readonly Action _onTileAll;

    private List<RunningRobloxInstance> _allInstances = new();
    private int _monitorCount = 1;
    private bool _allowClose;
    private bool _suppressDeactivateHide;
    private int? _pendingDigitPid;
    private Key? _pendingDigitKey;
    private readonly DispatcherTimer _digitReleaseFallbackTimer;

    public QuickSwitcherWindow(
        Action<int> onFocusInstance,
        Action<int> onMoveNextMonitor,
        Action<int> onToggleMinimize,
        Action<int> onCloseInstance,
        Action onTileAll)
    {
        _onFocusInstance = onFocusInstance ?? throw new ArgumentNullException(nameof(onFocusInstance));
        _onMoveNextMonitor = onMoveNextMonitor ?? throw new ArgumentNullException(nameof(onMoveNextMonitor));
        _onToggleMinimize = onToggleMinimize ?? throw new ArgumentNullException(nameof(onToggleMinimize));
        _onCloseInstance = onCloseInstance ?? throw new ArgumentNullException(nameof(onCloseInstance));
        _onTileAll = onTileAll ?? throw new ArgumentNullException(nameof(onTileAll));

        InitializeComponent();

        _digitReleaseFallbackTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(220)
        };
        _digitReleaseFallbackTimer.Tick += (_, _) =>
        {
            _digitReleaseFallbackTimer.Stop();
            CompletePendingDigitActivation();
        };
    }

    public void UpdateAndShow(IReadOnlyList<RunningRobloxInstance> instances, MonitorInfo targetMonitor, int monitorCount)
    {
        ClearPendingDigitActivation();
        _monitorCount = Math.Max(1, monitorCount);
        _allInstances = instances.ToList();

        _suppressDeactivateHide = true;
        try
        {
            SearchBox.Text = string.Empty;
            ApplyFilter();

            // Place initially near target monitor so WPF picks that monitor's DPI before final physical centering.
            var helper = new WindowInteropHelper(this);
            if (helper.Handle == nint.Zero)
            {
                Left = targetMonitor.WorkArea.Left;
                Top = targetMonitor.WorkArea.Top;
                Show();
            }
            else
            {
                User32.SetWindowPos(
                    helper.Handle,
                    nint.Zero,
                    targetMonitor.WorkArea.Left + 40,
                    targetMonitor.WorkArea.Top + 40,
                    0,
                    0,
                    User32.SWP_NOSIZE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);
                Show();
            }

            UpdateLayout();
            CenterOnMonitorPhysical(targetMonitor);
            Activate();
            SearchBox.Focus();
        }
        finally
        {
            _suppressDeactivateHide = false;
        }
    }

    public void RefreshInstances(IReadOnlyList<RunningRobloxInstance> instances, int monitorCount)
    {
        _monitorCount = Math.Max(1, monitorCount);
        _allInstances = instances.ToList();
        if (IsVisible)
        {
            ApplyFilter();
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

    private void CenterOnMonitorPhysical(MonitorInfo monitor)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == nint.Zero) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        int physW = Math.Max(200, (int)Math.Round(ActualWidth * dpi.DpiScaleX));
        int physH = Math.Max(140, (int)Math.Round(ActualHeight * dpi.DpiScaleY));

        int x = monitor.WorkArea.Left + Math.Max(0, (monitor.WorkArea.Width - physW) / 2);
        int y = monitor.WorkArea.Top + Math.Max(0, (monitor.WorkArea.Height - physH) / 2);

        User32.SetWindowPos(
            hwnd,
            User32.HWND_TOPMOST,
            x,
            y,
            0,
            0,
            User32.SWP_NOSIZE | User32.SWP_SHOWWINDOW);
    }

    private void ApplyFilter()
    {
        string query = SearchBox.Text?.Trim() ?? string.Empty;
        var filtered = string.IsNullOrWhiteSpace(query)
            ? _allInstances
            : _allInstances.Where(i =>
                    i.EffectiveLabel.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrWhiteSpace(i.Notes) && i.Notes.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                    i.ProcessId.ToString().Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    i.SlotIndex.ToString().Equals(query, StringComparison.OrdinalIgnoreCase))
                .ToList();

        var rows = filtered.Select(i => new SwitcherRowItem
        {
            Instance = i,
            MoveMonitorVisibility = _monitorCount > 1 ? Visibility.Visible : Visibility.Collapsed
        }).ToList();

        InstanceList.ItemsSource = rows;
        EmptyStateText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        InstanceList.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        if (rows.Count > 0)
        {
            int focusedIdx = rows.FindIndex(r => r.Instance.IsFocused);
            int defaultIdx = focusedIdx >= 0 && rows.Count > 1
                ? (focusedIdx + 1) % rows.Count
                : 0;
            InstanceList.SelectedIndex = defaultIdx;
            InstanceList.ScrollIntoView(rows[defaultIdx]);
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter();
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        ClearPendingDigitActivation();
        if (!_suppressDeactivateHide && IsVisible)
        {
            Hide();
        }
    }

    private void Window_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_pendingDigitPid.HasValue)
        {
            e.Handled = true;
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_pendingDigitPid.HasValue && e.Key == _pendingDigitKey)
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            ClearPendingDigitActivation();
            e.Handled = true;
            Hide();
            return;
        }

        if (e.Key == Key.Down)
        {
            MoveSelection(1);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Up)
        {
            MoveSelection(-1);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            ActivateSelected();
            e.Handled = true;
            return;
        }

        // Number keys 1-9 jump directly when search box is empty, deferring focus until KeyUp
        // so the target Roblox window does not receive the pressed digit key.
        if (string.IsNullOrEmpty(SearchBox.Text))
        {
            int? slot = e.Key switch
            {
                >= Key.D1 and <= Key.D9 => (e.Key - Key.D1) + 1,
                >= Key.NumPad1 and <= Key.NumPad9 => (e.Key - Key.NumPad1) + 1,
                _ => null
            };

            if (slot.HasValue && InstanceList.ItemsSource is IEnumerable<SwitcherRowItem> rows)
            {
                var match = rows.FirstOrDefault(r => r.SlotIndex == slot.Value);
                if (match != null)
                {
                    e.Handled = true;
                    _pendingDigitPid = match.ProcessId;
                    _pendingDigitKey = e.Key;
                    _digitReleaseFallbackTimer.Stop();
                    _digitReleaseFallbackTimer.Start();
                }
            }
        }
    }

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (_pendingDigitPid.HasValue && e.Key == _pendingDigitKey)
        {
            e.Handled = true;
            CompletePendingDigitActivation();
        }
    }

    private void CompletePendingDigitActivation()
    {
        int? pid = _pendingDigitPid;
        ClearPendingDigitActivation();
        if (pid.HasValue)
        {
            ActivateInstance(pid.Value);
        }
    }

    private void ClearPendingDigitActivation()
    {
        _digitReleaseFallbackTimer.Stop();
        _pendingDigitPid = null;
        _pendingDigitKey = null;
    }

    private void MoveSelection(int delta)
    {
        if (InstanceList.Items.Count == 0) return;
        int current = InstanceList.SelectedIndex;
        int count = InstanceList.Items.Count;
        int next = current < 0 ? 0 : (current + delta + count) % count;
        InstanceList.SelectedIndex = next;
        InstanceList.ScrollIntoView(InstanceList.SelectedItem);
    }

    private void ActivateSelected()
    {
        if (InstanceList.SelectedItem is SwitcherRowItem selected)
        {
            ActivateInstance(selected.ProcessId);
        }
        else if (InstanceList.Items.Count > 0 && InstanceList.Items[0] is SwitcherRowItem first)
        {
            ActivateInstance(first.ProcessId);
        }
    }

    private void ActivateInstance(int pid)
    {
        Hide();
        _onFocusInstance(pid);
    }

    private void InstanceList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (IsClickInsideButton(e.OriginalSource as DependencyObject)) return;
        ActivateSelected();
    }

    private void InstanceList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (IsClickInsideButton(e.OriginalSource as DependencyObject)) return;
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is SwitcherRowItem row)
        {
            ActivateInstance(row.ProcessId);
        }
    }

    private void MoveMonitor_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { Tag: int pid })
        {
            _suppressDeactivateHide = true;
            try { _onMoveNextMonitor(pid); }
            finally { _suppressDeactivateHide = false; }
        }
    }

    private void MinimizeItem_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { Tag: int pid })
        {
            _suppressDeactivateHide = true;
            try { _onToggleMinimize(pid); }
            finally { _suppressDeactivateHide = false; }
        }
    }

    private void CloseItem_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { Tag: int pid })
        {
            _suppressDeactivateHide = true;
            try { _onCloseInstance(pid); }
            finally { _suppressDeactivateHide = false; }
        }
    }

    private void TileAll_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Hide();
        _onTileAll();
    }

    private static bool IsClickInsideButton(DependencyObject? source) =>
        FindAncestor<Button>(source) != null;

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}
