using System.Windows;
using System.Windows.Controls;
using BaBaBooeyStrap.App.Services;
using BaBaBooeyStrap.App.ViewModels;
using BaBaBooeyStrap.Core.Logging;

namespace BaBaBooeyStrap.App.Views.Pages;

public partial class ChannelPage : Page
{
    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    public ChannelPage()
    {
        InitializeComponent();
    }

    private async void TestCdn_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        try
        {
            await ViewModel.LaunchSettings.TestFastestCdnMirrorAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Logger.ExceptionStatic("ChannelPage.TestCdn_Click", ex);
            FluentDialogService.ShowError(
                LocalizationService.Instance.GetString("Str_Dialog_ErrorTitle"),
                ex.Message,
                Window.GetWindow(this));
        }
    }

    private void ResetChannelDefaults_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ViewModel?.LaunchSettings.ResetChannelDefaults();
        }
        catch (Exception ex)
        {
            Logger.ExceptionStatic("ChannelPage.ResetChannelDefaults_Click", ex);
            FluentDialogService.ShowError(
                LocalizationService.Instance.GetString("Str_Dialog_ErrorTitle"),
                ex.Message,
                Window.GetWindow(this));
        }
    }
}
