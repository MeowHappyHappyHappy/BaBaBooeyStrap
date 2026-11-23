using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BaBaBooeyStrap.App.Services;
using BaBaBooeyStrap.Core.Interop;
using Microsoft.Win32;

namespace BaBaBooeyStrap.App.Views.Dialogs;

public partial class ServerJoinToastWindow : Window
{
    private const int MaxVisibleToasts = 4;
    private const double ToastGapDip = 8;
    private static readonly List<ServerJoinToastWindow> ActiveToasts = new();

    private readonly Action? _onClick;
    private readonly DispatcherTimer _closeTimer;
    private readonly DispatcherTimer _copiedCloseTimer;
    private bool _isClosing;
    private bool _isHovered;
    private bool _hasCopied;
    private bool _displaySubscribed;

    public ServerJoinToastWindow(
        string title,
        string locationText,
        string endpointText,
        string isoCode,
        BitmapImage? flagImage,
        int durationMs = 5000,
        Action? onClick = null)
    {
        InitializeComponent();

        _onClick = onClick;
        TitleText.Text = title;
        LocationText.Text = locationText;
        EndpointText.Text = endpointText;

        if (flagImage != null)
        {
            FlagImage.Source = flagImage;
            FallbackIsoText.Visibility = Visibility.Collapsed;
        }
        else
        {
            FlagImage.Visibility = Visibility.Collapsed;
            FallbackIsoText.Text = string.IsNullOrWhiteSpace(isoCode) ? "??" : isoCode;
        }

        _closeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(1000, durationMs))
        };
        _closeTimer.Tick += (_, _) =>
        {
            if (_isHovered || _hasCopied) return;
            BeginFadeOutAndClose();
        };

        _copiedCloseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(900)
        };
        _copiedCloseTimer.Tick += (_, _) =>
        {
            _copiedCloseTimer.Stop();
            CloseImmediate();
        };

        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            _closeTimer.Stop();
            _copiedCloseTimer.Stop();
            if (_displaySubscribed)
            {
                _displaySubscribed = false;
                SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            }

            if (ActiveToasts.Remove(this))
            {
                RepositionActiveToasts();
            }
        };
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

    public static void ShowToast(
        string title,
        string locationText,
        string endpointText,
        string isoCode,
        BitmapImage? flagImage,
        int durationMs = 5000,
        Action? onClick = null)
    {
        while (ActiveToasts.Count >= MaxVisibleToasts)
        {
            var oldest = ActiveToasts[0];
            ActiveToasts.RemoveAt(0);
            try
            {
                oldest.CloseImmediate();
            }
            catch
            {
            }
        }

        var toast = new ServerJoinToastWindow(title, locationText, endpointText, isoCode, flagImage, durationMs, onClick);
        ActiveToasts.Add(toast);
        toast.Show();
        RepositionActiveToasts();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_displaySubscribed)
        {
            _displaySubscribed = true;
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        }

        RepositionActiveToasts();

        Opacity = 0;
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180));
        BeginAnimation(OpacityProperty, fadeIn);

        if (!_isHovered)
        {
            _closeTimer.Start();
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        Dispatcher.BeginInvoke(RepositionActiveToasts);
    }

    private static void RepositionActiveToasts()
    {
        if (ActiveToasts.Count == 0)
        {
            return;
        }

        var workArea = SystemParameters.WorkArea;
        double bottom = workArea.Bottom - 8;

        for (int i = ActiveToasts.Count - 1; i >= 0; i--)
        {
            var toast = ActiveToasts[i];
            if (!toast.IsVisible)
            {
                continue;
            }

            double width = toast.ActualWidth > 0 ? toast.ActualWidth : (toast.Width > 0 ? toast.Width : 340);
            double height = toast.ActualHeight > 0 ? toast.ActualHeight : 84;

            toast.Left = Math.Max(workArea.Left + 8, workArea.Right - width - 8);
            toast.Top = Math.Max(workArea.Top + 8, bottom - height);
            bottom = toast.Top - ToastGapDip;
        }
    }

    private void Toast_MouseEnter(object sender, MouseEventArgs e)
    {
        _isHovered = true;
        _closeTimer.Stop();

        if (!_hasCopied && _isClosing)
        {
            _isClosing = false;
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
        }
    }

    private void Toast_MouseLeave(object sender, MouseEventArgs e)
    {
        _isHovered = false;
        if (!_hasCopied && !_isClosing)
        {
            _closeTimer.Start();
        }
    }

    private void Toast_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_hasCopied) return;
        _hasCopied = true;
        _closeTimer.Stop();
        _isClosing = false;
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;

        try
        {
            _onClick?.Invoke();
        }
        catch { }

        CopiedBadgeText.Text = LocalizationService.Instance.GetString("Str_Toast_Copied");
        CopiedBadge.Visibility = Visibility.Visible;
        _copiedCloseTimer.Start();
    }

    private void BeginFadeOutAndClose()
    {
        if (_isClosing) return;
        _isClosing = true;
        _closeTimer.Stop();

        var fadeOut = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(240));
        fadeOut.Completed += (_, _) =>
        {
            if (_isClosing)
            {
                CloseImmediate();
            }
        };
        BeginAnimation(OpacityProperty, fadeOut);
    }

    private void CloseImmediate()
    {
        _isClosing = true;
        _closeTimer.Stop();
        _copiedCloseTimer.Stop();
        try
        {
            Close();
        }
        catch { }
    }
}
