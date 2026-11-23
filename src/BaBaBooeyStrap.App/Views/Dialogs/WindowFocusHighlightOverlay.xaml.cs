using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BaBaBooeyStrap.Core.Interop;
using BaBaBooeyStrap.Integrations.Window;

namespace BaBaBooeyStrap.App.Views.Dialogs;

public partial class WindowFocusHighlightOverlay : Window
{
    private readonly DispatcherTimer _holdTimer;
    private bool _allowClose;
    private User32.RECT _lastBounds;

    public WindowFocusHighlightOverlay()
    {
        InitializeComponent();

        _holdTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(700)
        };
        _holdTimer.Tick += (_, _) =>
        {
            _holdTimer.Stop();
            FadeOutAndHide();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        nint hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != nint.Zero)
        {
            long exStyle = User32.GetWindowLongPtr(hwnd, User32.GWL_EXSTYLE).ToInt64();
            exStyle |= User32.WS_EX_TRANSPARENT | User32.WS_EX_NOACTIVATE | User32.WS_EX_TOOLWINDOW | User32.WS_EX_LAYERED;
            User32.SetWindowLongPtr(hwnd, User32.GWL_EXSTYLE, unchecked((nint)exStyle));
        }
    }

    public void ShowForWindow(nint targetHwnd, RunningRobloxInstance instance)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (targetHwnd == nint.Zero || !User32.IsWindow(targetHwnd))
        {
            return;
        }

        if (!TryResolveVisibleBounds(targetHwnd, out var rect) || rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        SlotText.Text = $"#{instance.SlotIndex}";
        AccountNameText.Text = instance.EffectiveLabel;
        var extras = new List<string>();
        if (!string.IsNullOrWhiteSpace(instance.Notes))
        {
            extras.Add(instance.Notes!);
        }
        extras.Add(instance.MonitorBadge);
        ExtraInfoText.Text = string.Join(" · ", extras);

        _holdTimer.Stop();
        BeginAnimation(OpacityProperty, null);

        var helper = new WindowInteropHelper(this);
        nint overlayHwnd = helper.EnsureHandle();

        bool wasVisible = IsVisible;
        bool boundsChanged = !wasVisible ||
                             rect.Left != _lastBounds.Left ||
                             rect.Top != _lastBounds.Top ||
                             rect.Right != _lastBounds.Right ||
                             rect.Bottom != _lastBounds.Bottom;

        Opacity = 0;
        if (!wasVisible)
        {
            Show();
        }

        if (boundsChanged)
        {
            _lastBounds = rect;
            User32.SetWindowPos(
                overlayHwnd,
                User32.HWND_TOPMOST,
                rect.Left,
                rect.Top,
                rect.Width,
                rect.Height,
                User32.SWP_NOACTIVATE | User32.SWP_SHOWWINDOW);
        }

        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(90));
        BeginAnimation(OpacityProperty, fadeIn);
        _holdTimer.Start();
    }

    public void ForceClose()
    {
        _allowClose = true;
        _holdTimer.Stop();
        BeginAnimation(OpacityProperty, null);
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            _holdTimer.Stop();
            BeginAnimation(OpacityProperty, null);
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    private void FadeOutAndHide()
    {
        var fadeOut = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(260));
        fadeOut.Completed += (_, _) =>
        {
            if (!_allowClose && !_holdTimer.IsEnabled)
            {
                Hide();
            }
        };
        BeginAnimation(OpacityProperty, fadeOut);
    }

    private static bool TryResolveVisibleBounds(nint hwnd, out User32.RECT rect)
    {
        if (Dwmapi.DwmGetWindowAttribute(
                hwnd,
                Dwmapi.DWMWA_EXTENDED_FRAME_BOUNDS,
                out rect,
                Marshal.SizeOf<User32.RECT>()) == 0 &&
            rect.Width > 0 &&
            rect.Height > 0 &&
            rect.Left > -32000 &&
            rect.Top > -32000)
        {
            return true;
        }

        return User32.GetWindowRect(hwnd, out rect) && rect.Width > 0 && rect.Height > 0;
    }
}
