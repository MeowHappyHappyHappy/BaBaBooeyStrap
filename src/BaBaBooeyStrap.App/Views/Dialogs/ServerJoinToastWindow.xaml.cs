using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BaBaBooeyStrap.App.Services;

namespace BaBaBooeyStrap.App.Views.Dialogs;

public partial class ServerJoinToastWindow : Window
{
    private static ServerJoinToastWindow? _currentToast;

    private readonly Action? _onClick;
    private readonly DispatcherTimer _closeTimer;
    private readonly DispatcherTimer _copiedCloseTimer;
    private bool _isClosing;
    private bool _isHovered;
    private bool _hasCopied;

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
            if (ReferenceEquals(_currentToast, this))
            {
                _currentToast = null;
            }
        };
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
        try
        {
            _currentToast?.CloseImmediate();
        }
        catch { }

        var toast = new ServerJoinToastWindow(title, locationText, endpointText, isoCode, flagImage, durationMs, onClick);
        _currentToast = toast;
        toast.Show();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - ActualWidth - 4;
        Top = workArea.Bottom - ActualHeight - 4;

        Opacity = 0;
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180));
        BeginAnimation(OpacityProperty, fadeIn);

        if (!_isHovered)
        {
            _closeTimer.Start();
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
