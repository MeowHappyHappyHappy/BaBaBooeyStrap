using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using BaBaBooeyStrap.App.Services;
using BaBaBooeyStrap.App.ViewModels;
using BaBaBooeyStrap.App.Views.Dialogs;
using BaBaBooeyStrap.Core.Models;
using Microsoft.Win32;

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

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null || ViewModel.LaunchSettings.IsCheckingAppUpdate) return;

        ViewModel.LaunchSettings.IsCheckingAppUpdate = true;
        ViewModel.LaunchSettings.SetAppUpdateStatus("Str_About_CheckingUpdates");

        try
        {
            var info = await ViewModel.AppUpdateService.CheckForUpdateAsync();
            if (ViewModel.StateManager != null)
            {
                ViewModel.StateManager.Prop.LastAppUpdateCheck = DateTimeOffset.UtcNow;
                ViewModel.StateManager.Save();
            }

            if (!info.IsUpdateAvailable)
            {
                ViewModel.LaunchSettings.SetAppUpdateStatus("Str_About_UpToDateFormat", info.CurrentVersion.ToString(3));
                return;
            }

            ViewModel.LaunchSettings.SetAppUpdateStatus("Str_About_UpdateAvailableFormat", info.LatestVersion.ToString(3));

            var dlg = new UpdateAvailableDialog(info, ViewModel.AppUpdateService)
            {
                Owner = Window.GetWindow(this)
            };
            dlg.ShowDialog();

            if (!string.IsNullOrWhiteSpace(dlg.SkippedVersion) && ViewModel.StateManager != null)
            {
                ViewModel.StateManager.Prop.SkippedAppVersion = dlg.SkippedVersion;
                ViewModel.StateManager.Save();
            }

            if (dlg.UpdateInstalledNeedsRestart)
            {
                if (Application.Current is App app)
                {
                    app.RequestShutdown(0);
                }
                else
                {
                    Application.Current?.Shutdown();
                }
            }
        }
        catch (Exception ex)
        {
            ViewModel.LaunchSettings.SetAppUpdateStatus("Str_About_UpdateCheckFailedFormat", ex.Message);
        }
        finally
        {
            ViewModel.LaunchSettings.IsCheckingAppUpdate = false;
        }
    }

    private void ResetFastFlags_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;

        bool confirmed = FluentDialogService.ShowConfirm(
            LocalizationService.Instance.GetString("Str_About_ResetFastFlags"),
            LocalizationService.Instance.GetString("Str_About_ResetFastFlagsConfirm"),
            isDestructive: true,
            owner: Window.GetWindow(this));
        if (!confirmed) return;

        try
        {
            DiagnosticsService.ResetFastFlags(ViewModel.FastFlags.FastFlagManager);
            ViewModel.FastFlags.RefreshFromSettings();
            FluentDialogService.ShowInfo(
                LocalizationService.Instance.GetString("Str_About_TroubleshootingHeader"),
                LocalizationService.Instance.GetString("Str_About_ResetFastFlagsDone"),
                Window.GetWindow(this));
        }
        catch (Exception ex)
        {
            FluentDialogService.ShowError(
                LocalizationService.Instance.GetString("Str_Dialog_ErrorTitle"),
                ex.Message,
                Window.GetWindow(this));
        }
    }

    private void ClearCacheImmediate_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;

        bool confirmed = FluentDialogService.ShowConfirm(
            LocalizationService.Instance.GetString("Str_About_ClearCacheNow"),
            LocalizationService.Instance.GetString("Str_About_ClearCacheConfirm"),
            isDestructive: true,
            owner: Window.GetWindow(this));
        if (!confirmed) return;

        try
        {
            var report = DiagnosticsService.ClearAllCachesImmediate(ViewModel.AppPaths);
            double freedMb = report.BytesFreed / (1024.0 * 1024.0);
            FluentDialogService.ShowInfo(
                LocalizationService.Instance.GetString("Str_About_TroubleshootingHeader"),
                LocalizationService.Instance.Format("Str_Behaviour_CleanerReportFormat", report.FilesDeleted, freedMb),
                Window.GetWindow(this));
        }
        catch (Exception ex)
        {
            FluentDialogService.ShowError(
                LocalizationService.Instance.GetString("Str_Dialog_ErrorTitle"),
                ex.Message,
                Window.GetWindow(this));
        }
    }

    private void RepairProtocols_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;

        try
        {
            var installer = new InstallerService(ViewModel.AppPaths, ViewModel.StateManager);
            DiagnosticsService.RepairProtocols(installer);
            ViewModel.AccountManagement.RefreshProtocolStatus();

            FluentDialogService.ShowInfo(
                LocalizationService.Instance.GetString("Str_About_TroubleshootingHeader"),
                LocalizationService.Instance.GetString("Str_About_RepairProtocolsDone"),
                Window.GetWindow(this));
        }
        catch (Exception ex)
        {
            FluentDialogService.ShowError(
                LocalizationService.Instance.GetString("Str_Dialog_ErrorTitle"),
                ex.Message,
                Window.GetWindow(this));
        }
    }

    private void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;

        var dlg = new SaveFileDialog
        {
            Title = LocalizationService.Instance.GetString("Str_About_ExportDiagnostics"),
            Filter = LocalizationService.Instance.GetString("Str_Filter_ZipArchive"),
            FileName = $"BaBaBooeyStrap_Diagnostics_{DateTime.Now:yyyyMMdd_HHmmss}.zip"
        };

        if (dlg.ShowDialog() != true) return;

        try
        {
            DiagnosticsService.ExportDiagnosticsZip(
                dlg.FileName,
                ViewModel.AppPaths,
                ViewModel.Settings,
                ViewModel.StateManager?.Prop ?? new State(),
                ViewModel.RobloxStateManager?.Prop ?? new RobloxState(),
                ViewModel.LaunchSettings.AppVersion);

            bool openFolder = FluentDialogService.ShowConfirm(
                LocalizationService.Instance.GetString("Str_About_TroubleshootingHeader"),
                LocalizationService.Instance.Format("Str_About_ExportDiagnosticsDoneFormat", dlg.FileName),
                confirmText: LocalizationService.Instance.GetString("Str_About_OpenFolder"),
                cancelText: LocalizationService.Instance.GetString("Str_Common_Close"),
                owner: Window.GetWindow(this));

            if (openFolder)
            {
                string? dir = Path.GetDirectoryName(dlg.FileName);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
                }
            }
        }
        catch (Exception ex)
        {
            FluentDialogService.ShowError(
                LocalizationService.Instance.GetString("Str_Dialog_ErrorTitle"),
                ex.Message,
                Window.GetWindow(this));
        }
    }
}
