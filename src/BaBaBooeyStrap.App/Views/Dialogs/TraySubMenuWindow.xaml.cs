using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BaBaBooeyStrap.Core.Interop;

namespace BaBaBooeyStrap.App.Views.Dialogs;

public partial class TraySubMenuWindow : Window
{
    private readonly Window? _parentWindow;
    private readonly Func<nint, bool> _isChainWindow;
    private readonly Action _dismissAll;
    private readonly TrayMenuBranchController _branch;
    private bool _allowClose;
    private bool _suppressDeactivateDismiss;

    internal TraySubMenuWindow(
        Window? parentWindow,
        Func<User32.RECT> getWorkArea,
        Func<nint, bool> isChainWindow,
        Action dismissAll)
    {
        _parentWindow = parentWindow;
        _isChainWindow = isChainWindow ?? throw new ArgumentNullException(nameof(isChainWindow));
        _dismissAll = dismissAll ?? throw new ArgumentNullException(nameof(dismissAll));

        InitializeComponent();
        _branch = new TrayMenuBranchController(this, ItemsHost, getWorkArea, isChainWindow, dismissAll);
    }

    internal void ShowAtParentRow(
        IReadOnlyList<TrayMenuItemModel> items,
        TrayMenuPalette palette,
        int parentCardLeft,
        int parentCardRight,
        int itemTopPhys,
        User32.RECT workArea)
    {
        _suppressDeactivateDismiss = true;
        try
        {
            TrayMenuBranchController.ApplyPaletteToCard(CardBorder, palette);
            _branch.PopulateItems(items, palette);

            var helper = new WindowInteropHelper(this);
            nint hwnd = helper.EnsureHandle();

            Opacity = 0;
            User32.SetWindowPos(
                hwnd,
                User32.HWND_TOPMOST,
                Math.Max(workArea.Left, Math.Min(workArea.Right - 100, parentCardRight)),
                Math.Clamp(itemTopPhys, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - 100)),
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
            int totalW = Math.Max(120, (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX));
            int totalH = Math.Max(60, (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY));
            int cardW = Math.Max(80, totalW - (2 * padX));
            int cardH = Math.Max(40, totalH - (2 * padY));

            var (cardLeft, cardTop) = TrayMenuPositioner.ComputeSubMenuPosition(
                parentCardLeft,
                parentCardRight,
                itemTopPhys,
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
                User32.SWP_NOSIZE | User32.SWP_NOACTIVATE | User32.SWP_SHOWWINDOW);

            Opacity = 1;
        }
        finally
        {
            _suppressDeactivateDismiss = false;
        }
    }

    internal bool ContainsHwnd(nint hwnd) => _branch.ContainsHwnd(hwnd);

    internal void HideBranch()
    {
        _branch.CloseChild();
        if (IsVisible)
        {
            Hide();
        }
    }

    internal void ForceClose()
    {
        _allowClose = true;
        _branch.Dispose();
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            HideBranch();
            return;
        }
        base.OnClosing(e);
    }

    internal void FocusFromKeyboard()
    {
        _branch.SelectFirst();
        Activate();
        Focus();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        _branch.HandleKeyDown(e, isSubMenu: true, () =>
        {
            HideBranch();
            if (_parentWindow is { IsVisible: true })
            {
                _parentWindow.Activate();
                _parentWindow.Focus();
            }
        });
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (_suppressDeactivateDismiss || !IsVisible) return;

        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (!IsVisible || _suppressDeactivateDismiss) return;
            nint fg = User32.GetForegroundWindow();
            if (!_isChainWindow(fg))
            {
                _dismissAll();
            }
        });
    }
}
