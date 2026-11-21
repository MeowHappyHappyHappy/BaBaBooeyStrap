using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace BaBaBooeyStrap.App.Views.Dialogs;

public partial class ServerJoinToastWindow : Window
{
    private static ServerJoinToastWindow? _currentToast;

    private readonly Action? _onClick;
    private readonly DispatcherTimer _closeTimer;
    private bool _isClosing;

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
        _closeTimer.Tick += (_, _) => BeginFadeOutAndClose();

        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            _closeTimer.Stop();
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

        _closeTimer.Start();
    }

    private void Toast_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _closeTimer.Stop();
        try
        {
            _onClick?.Invoke();
        }
        catch { }

        CloseImmediate();
    }

    private void BeginFadeOutAndClose()
    {
        if (_isClosing) return;
        _isClosing = true;
        _closeTimer.Stop();

        var fadeOut = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(240));
        fadeOut.Completed += (_, _) => CloseImmediate();
        BeginAnimation(OpacityProperty, fadeOut);
    }

    private void CloseImmediate()
    {
        _isClosing = true;
        _closeTimer.Stop();
        try
        {
            Close();
        }
        catch { }
    }
}
