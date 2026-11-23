using System.Windows;
using System.Windows.Controls;
using BaBaBooeyStrap.App.Services;
using BaBaBooeyStrap.App.ViewModels;
using BaBaBooeyStrap.Core.Logging;

namespace BaBaBooeyStrap.App.Views.Pages;

public partial class ShortcutsPage : Page
{
    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    public ShortcutsPage()
    {
        InitializeComponent();
    }

    private void CreateDesktop_Click(object sender, RoutedEventArgs e) =>
        RunSafe("ShortcutsPage.CreateDesktop_Click", () => ViewModel?.LaunchSettings.CreateDesktopShortcut());

    private void CreateStartMenu_Click(object sender, RoutedEventArgs e) =>
        RunSafe("ShortcutsPage.CreateStartMenu_Click", () => ViewModel?.LaunchSettings.CreateStartMenuShortcuts());

    private void RegisterPlayer_Click(object sender, RoutedEventArgs e) =>
        RunSafe("ShortcutsPage.RegisterPlayer_Click", () =>
        {
            ViewModel?.LaunchSettings.RegisterPlayerProtocols();
            ViewModel?.AccountManagement.RefreshProtocolStatus();
        });

    private void RegisterStudio_Click(object sender, RoutedEventArgs e) =>
        RunSafe("ShortcutsPage.RegisterStudio_Click", () => ViewModel?.LaunchSettings.RegisterStudioProtocols());

    private void RemoveStartup_Click(object sender, RoutedEventArgs e) =>
        RunSafe("ShortcutsPage.RemoveStartup_Click", () => ViewModel?.LaunchSettings.RemoveRobloxStartup());

    private void UnregisterAll_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var owner = Window.GetWindow(this);
        bool confirmed = FluentDialogService.ShowConfirm(
            LocalizationService.Instance.GetString("Str_Shortcuts_UnregisterAll"),
            LocalizationService.Instance.GetString("Str_Shortcuts_UnregisterAllConfirm"),
            isDestructive: true,
            owner: owner);
        if (!confirmed) return;

        RunSafe("ShortcutsPage.UnregisterAll_Click", () =>
        {
            ViewModel.LaunchSettings.UnregisterAllProtocols();
            ViewModel.AccountManagement.RefreshProtocolStatus();
        });
    }

    private void OpenBase_Click(object sender, RoutedEventArgs e) => OpenFolder(ViewModel?.LaunchSettings.BasePath);
    private void OpenVersions_Click(object sender, RoutedEventArgs e) => OpenFolder(ViewModel?.LaunchSettings.VersionsPath);
    private void OpenLogs_Click(object sender, RoutedEventArgs e) => OpenFolder(ViewModel?.LaunchSettings.LogsPath);

    private void OpenFolder(string? path)
    {
        FluentDialogService.TryOpenFolderInExplorer(path, Window.GetWindow(this));
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
