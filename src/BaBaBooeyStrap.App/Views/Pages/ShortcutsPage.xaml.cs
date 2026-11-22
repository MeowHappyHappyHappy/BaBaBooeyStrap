using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using BaBaBooeyStrap.App.ViewModels;

namespace BaBaBooeyStrap.App.Views.Pages;

public partial class ShortcutsPage : Page
{
    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    public ShortcutsPage()
    {
        InitializeComponent();
    }

    private void CreateDesktop_Click(object sender, RoutedEventArgs e) => ViewModel?.LaunchSettings.CreateDesktopShortcut();
    private void CreateStartMenu_Click(object sender, RoutedEventArgs e) => ViewModel?.LaunchSettings.CreateStartMenuShortcuts();
    private void RegisterPlayer_Click(object sender, RoutedEventArgs e)
    {
        ViewModel?.LaunchSettings.RegisterPlayerProtocols();
        ViewModel?.AccountManagement.RefreshProtocolStatus();
    }

    private void RegisterStudio_Click(object sender, RoutedEventArgs e) => ViewModel?.LaunchSettings.RegisterStudioProtocols();
    private void RemoveStartup_Click(object sender, RoutedEventArgs e) => ViewModel?.LaunchSettings.RemoveRobloxStartup();
    private void UnregisterAll_Click(object sender, RoutedEventArgs e)
    {
        ViewModel?.LaunchSettings.UnregisterAllProtocols();
        ViewModel?.AccountManagement.RefreshProtocolStatus();
    }

    private void OpenBase_Click(object sender, RoutedEventArgs e) => OpenFolder(ViewModel?.LaunchSettings.BasePath);
    private void OpenVersions_Click(object sender, RoutedEventArgs e) => OpenFolder(ViewModel?.LaunchSettings.VersionsPath);
    private void OpenLogs_Click(object sender, RoutedEventArgs e) => OpenFolder(ViewModel?.LaunchSettings.LogsPath);

    private static void OpenFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
    }
}
