using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using BaBaBooeyStrap.App.Services;
using BaBaBooeyStrap.App.ViewModels;
using BaBaBooeyStrap.Core;
using Microsoft.Win32;

namespace BaBaBooeyStrap.App.Views.Pages;

public partial class AppearancePage : Page
{
    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    public AppearancePage()
    {
        InitializeComponent();
    }

    private void BrowseBootstrapperIcon_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var ofd = new OpenFileDialog { Filter = LocalizationService.Instance.GetString("Str_Appearance_IconFileFilter") };
        if (ofd.ShowDialog() == true)
        {
            ViewModel.BootstrapperIconCustomLocation = ofd.FileName;
        }
    }

    private void OpenThemesFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Paths.CustomThemes);
        Process.Start(new ProcessStartInfo("explorer.exe", Paths.CustomThemes) { UseShellExecute = true });
        ViewModel?.RefreshCustomThemes();
    }

    private void PreviewDialog_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var cts = new CancellationTokenSource();
        var dialog = BootstrapperDialogFactory.Create(ViewModel.Settings, cts);
        dialog.Message = LocalizationService.Instance.GetString("Str_Bootstrapper_PreviewMessage");
        dialog.SetProgress(65, 100, false);
        dialog.CancelRequested += (_, _) => dialog.CloseBootstrapper();
        dialog.ShowBootstrapper();
    }
}
