using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BaBaBooeyStrap.Core.Interop;

namespace BaBaBooeyStrap.App.Views.Dialogs;

public partial class TrayContextMenuWindow : Window
{
    private readonly TrayMenuBranchController _branch;
    private User32.RECT _currentWorkArea;
    private bool _allowClose;
    private bool _suppressDeactivateDismiss;

    public TrayContextMenuWindow()
    {
        InitializeComponent();
        _branch = new TrayMenuBranchController(
            this,
            ItemsHost,
            () => _currentWorkArea,
            IsWindowInChain,
            DismissAll);
    }

    internal void ShowAtCursor(TrayMenuSnapshot snapshot, int cursorX, int cursorY, User32.RECT workArea)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        _currentWorkArea = workArea;
        _suppressDeactivateDismiss = true;
        try
        {
            var palette = TrayMenuPalette.CreateCurrent();
            TrayMenuBranchController.ApplyPaletteToCard(CardBorder, palette);

            HeaderBorder.Background = palette.HeaderBackground;
            HeaderBorder.BorderBrush = palette.SeparatorBrush;
            AppTitleText.Text = snapshot.AppTitle;
            AppTitleText.Foreground = palette.TextPrimary;
            VersionPill.Background = palette.VersionPillBackground;
            VersionTextBlock.Text = snapshot.VersionText;
            VersionTextBlock.Foreground = palette.AccentBrush;
            StatusPill.Background = palette.BadgeBackground;
            StatusDot.Fill = TrayMenuPalette.GetStatusColor(snapshot.MultiInstanceBadgeState);
            StatusBadgeText.Text = snapshot.MultiInstanceBadgeText;
            StatusBadgeText.Foreground = palette.TextSecondary;

            _branch.PopulateItems(snapshot.Items, palette);

            var helper = new WindowInteropHelper(this);
            nint hwnd = helper.EnsureHandle();

            Opacity = 0;
            User32.SetWindowPos(
                hwnd,
                User32.HWND_TOPMOST,
                workArea.Left + 20,
                workArea.Top + 20,
                0,
                0,
                User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);

            if (!IsVisible)
            {
                Show();
            }

            UpdateLayout();

            var dpi = VisualTreeHelper.GetDpi(this);
            int padX = (int)Math.Round(TrayMenuBranchController.ShadowMarginDip * dpi.DpiScaleX);
            int padY = (int)Math.Round(TrayMenuBranchController.ShadowMarginDip * dpi.DpiScaleY);
            int totalW = Math.Max(160, (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX));
            int totalH = Math.Max(80, (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY));
            int cardW = Math.Max(100, totalW - (2 * padX));
            int cardH = Math.Max(60, totalH - (2 * padY));

            var (cardLeft, cardTop) = TrayMenuPositioner.ComputePopupPosition(
                cursorX,
                cursorY,
                cardW,
                cardH,
                workArea);

            User32.SetWindowPos(
                hwnd,
                User32.HWND_TOPMOST,
                cardLeft - padX,
                cardTop - padY,
                0,
                0,
                User32.SWP_NOSIZE | User32.SWP_SHOWWINDOW);

            Opacity = 1;
            User32.SetForegroundWindow(hwnd);
            Activate();
            Focus();
        }
        finally
        {
            _suppressDeactivateDismiss = false;
        }
    }

    public void DismissAll()
    {
        _branch.CloseChild();
        if (IsVisible)
        {
            Hide();
        }
    }

    public void ForceClose()
    {
        _allowClose = true;
        _branch.Dispose();
        Close();
    }

    private bool IsWindowInChain(nint hwnd) => _branch.ContainsHwnd(hwnd);

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            DismissAll();
            return;
        }
        base.OnClosing(e);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            DismissAll();
        }
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (_suppressDeactivateDismiss || !IsVisible) return;

        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (!IsVisible || _suppressDeactivateDismiss) return;
            nint fg = User32.GetForegroundWindow();
            if (!IsWindowInChain(fg))
            {
                DismissAll();
            }
        });
    }
}
