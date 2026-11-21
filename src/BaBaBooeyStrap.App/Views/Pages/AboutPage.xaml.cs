using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using BaBaBooeyStrap.App.ViewModels;

namespace BaBaBooeyStrap.App.Views.Pages;

public partial class AboutPage : Page
{
    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    public AboutPage()
    {
        InitializeComponent();
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        Directory.CreateDirectory(ViewModel.LaunchSettings.LogsPath);
        Process.Start(new ProcessStartInfo("explorer.exe", ViewModel.LaunchSettings.LogsPath) { UseShellExecute = true });
    }
}
