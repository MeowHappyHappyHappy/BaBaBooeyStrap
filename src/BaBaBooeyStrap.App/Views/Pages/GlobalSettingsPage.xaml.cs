using System.Windows;
using System.Windows.Controls;
using BaBaBooeyStrap.App.ViewModels;

namespace BaBaBooeyStrap.App.Views.Pages;

public partial class GlobalSettingsPage : Page
{
    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    public GlobalSettingsPage()
    {
        InitializeComponent();
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => ViewModel?.LoadGlobalSettingsSafe();

    private void Apply_Click(object sender, RoutedEventArgs e) => ViewModel?.SaveGlobalSettings();

    private void FpsPreset_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null && sender is FrameworkElement fe && int.TryParse(fe.Tag?.ToString(), out int fps))
        {
            ViewModel.FramerateCap = fps;
        }
    }
}
