using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace BaBaBooeyStrap.App.Views.Dialogs;

public enum FluentDialogResult
{
    Primary,
    Secondary,
    Cancel
}

public enum FluentDialogSeverity
{
    Info,
    Warning,
    Error,
    Question
}

public partial class FluentMessageDialog : FluentWindow
{
    private static readonly Brush WarningDarkBrush = FreezeBrush(0xF5, 0x9E, 0x0B);
    private static readonly Brush WarningLightBrush = FreezeBrush(0xB4, 0x53, 0x09);
    private static readonly Brush ErrorBrush = FreezeBrush(0xEF, 0x44, 0x44);
    private static readonly Brush InfoBrush = FreezeBrush(0x3B, 0x82, 0xF6);

    public FluentDialogResult Result { get; private set; } = FluentDialogResult.Cancel;

    public FluentMessageDialog(
        string title,
        string message,
        FluentDialogSeverity severity,
        string primaryText,
        string? secondaryText = null,
        string? cancelText = null,
        bool isDestructive = false,
        Window? owner = null)
    {
        InitializeComponent();

        Title = title;
        DialogTitleBar.Title = title;
        MessageTextBlock.Text = message;

        ConfigureSeverityIcon(severity);

        PrimaryButton.Content = primaryText;
        PrimaryButton.Appearance = isDestructive ? ControlAppearance.Danger : ControlAppearance.Primary;
        if (isDestructive)
        {
            PrimaryButton.IsDefault = false;
        }

        if (!string.IsNullOrEmpty(secondaryText))
        {
            SecondaryButton.Content = secondaryText;
            SecondaryButton.Visibility = Visibility.Visible;
        }

        if (!string.IsNullOrEmpty(cancelText))
        {
            CancelButton.Content = cancelText;
            CancelButton.Visibility = Visibility.Visible;
            PrimaryButton.Margin = new Thickness(0, 0, 8, 0);
        }
        else if (SecondaryButton.Visibility == Visibility.Visible)
        {
            SecondaryButton.Margin = new Thickness(0);
        }
        else
        {
            PrimaryButton.Margin = new Thickness(0);
            PrimaryButton.IsCancel = true;
        }

        if (owner != null && owner.IsLoaded && owner.IsVisible)
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    private void ConfigureSeverityIcon(FluentDialogSeverity severity)
    {
        switch (severity)
        {
            case FluentDialogSeverity.Warning:
                SeverityIcon.Symbol = SymbolRegular.Warning24;
                SeverityIcon.Foreground = IsLightTheme() ? WarningLightBrush : WarningDarkBrush;
                break;
            case FluentDialogSeverity.Error:
                SeverityIcon.Symbol = SymbolRegular.ErrorCircle24;
                SeverityIcon.Foreground = ErrorBrush;
                break;
            case FluentDialogSeverity.Question:
                SeverityIcon.Symbol = SymbolRegular.QuestionCircle24;
                SeverityIcon.Foreground = InfoBrush;
                break;
            case FluentDialogSeverity.Info:
            default:
                SeverityIcon.Symbol = SymbolRegular.Info24;
                SeverityIcon.Foreground = InfoBrush;
                break;
        }
    }

    private static bool IsLightTheme()
    {
        try
        {
            return ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Light;
        }
        catch
        {
            return false;
        }
    }

    private static Brush FreezeBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private void PrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        Result = FluentDialogResult.Primary;
        DialogResult = true;
        Close();
    }

    private void SecondaryButton_Click(object sender, RoutedEventArgs e)
    {
        Result = FluentDialogResult.Secondary;
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Result = FluentDialogResult.Cancel;
        DialogResult = false;
        Close();
    }
}
