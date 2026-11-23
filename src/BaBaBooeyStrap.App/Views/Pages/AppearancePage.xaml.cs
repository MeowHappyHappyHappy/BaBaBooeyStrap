using System.Windows;
using System.Windows.Controls;
using BaBaBooeyStrap.App.Services;
using BaBaBooeyStrap.App.ViewModels;
using BaBaBooeyStrap.Core;
using BaBaBooeyStrap.Core.Logging;
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
        var owner = Window.GetWindow(this);
        var ofd = new OpenFileDialog { Filter = LocalizationService.Instance.GetString("Str_Appearance_IconFileFilter") };
        if ((owner != null ? ofd.ShowDialog(owner) : ofd.ShowDialog()) == true)
        {
            ViewModel.WindowIntegrations.BootstrapperIconCustomLocation = ofd.FileName;
        }
    }

    private void BrowseRobloxIcon_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var owner = Window.GetWindow(this);
        var ofd = new OpenFileDialog { Filter = LocalizationService.Instance.GetString("Str_Appearance_RobloxIconFileFilter") };
        if ((owner != null ? ofd.ShowDialog(owner) : ofd.ShowDialog()) == true)
        {
            ViewModel.WindowIntegrations.RobloxIconCustomLocation = ofd.FileName;
        }
    }

    private void OpenThemesFolder_Click(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        string dir = ViewModel?.AppPaths.CustomThemes ?? Paths.Current.CustomThemes;
        FluentDialogService.TryOpenFolderInExplorer(dir, owner);
        ViewModel?.WindowIntegrations.RefreshCustomThemes();
    }

    private void PreviewDialog_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        CancellationTokenSource? cts = null;
        try
        {
            cts = new CancellationTokenSource();
            var dialog = BootstrapperDialogFactory.Create(ViewModel.Settings, cts);
            dialog.Message = LocalizationService.Instance.GetString("Str_Bootstrapper_PreviewMessage");
            dialog.SetProgress(65, 100, false);
            dialog.CancelRequested += (_, _) => dialog.CloseBootstrapper();

            if (dialog is Window previewWindow)
            {
                var capturedCts = cts;
                previewWindow.Closed += (_, _) =>
                {
                    try { capturedCts.Cancel(); } catch { }
                    try { capturedCts.Dispose(); } catch { }
                };
                cts = null;
            }

            dialog.ShowBootstrapper();
        }
        catch (Exception ex)
        {
            Logger.ExceptionStatic("AppearancePage.PreviewDialog_Click", ex);
            FluentDialogService.ShowError(
                LocalizationService.Instance.GetString("Str_Dialog_ErrorTitle"),
                ex.Message,
                Window.GetWindow(this));
        }
        finally
        {
            if (cts != null)
            {
                try { cts.Dispose(); } catch { }
            }
        }
    }
}
