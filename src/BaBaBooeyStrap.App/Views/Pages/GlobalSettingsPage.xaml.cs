using System.Windows;
using System.Windows.Controls;
using BaBaBooeyStrap.App.Services;
using BaBaBooeyStrap.App.ViewModels;
using BaBaBooeyStrap.Core.Logging;

namespace BaBaBooeyStrap.App.Views.Pages;

public partial class GlobalSettingsPage : Page
{
    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    public GlobalSettingsPage()
    {
        InitializeComponent();
    }

    private void Reload_Click(object sender, RoutedEventArgs e) =>
        RunSafe("GlobalSettingsPage.Reload_Click", () => ViewModel?.LaunchSettings.LoadGlobalSettingsSafe());

    private void Apply_Click(object sender, RoutedEventArgs e) =>
        RunSafe("GlobalSettingsPage.Apply_Click", () => ViewModel?.LaunchSettings.SaveGlobalSettings());

    private void FpsPreset_Click(object sender, RoutedEventArgs e)
    {
        RunSafe("GlobalSettingsPage.FpsPreset_Click", () =>
        {
            if (ViewModel != null && sender is FrameworkElement fe && int.TryParse(fe.Tag?.ToString(), out int fps))
            {
                ViewModel.LaunchSettings.FramerateCap = fps;
            }
        });
    }

    private void RunSafe(string context, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Logger.ExceptionStatic(context, ex);
            FluentDialogService.ShowError(
                LocalizationService.Instance.GetString("Str_Dialog_ErrorTitle"),
                ex.Message,
                Window.GetWindow(this));
        }
    }
}
