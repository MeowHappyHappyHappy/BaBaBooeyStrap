using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BaBaBooeyStrap.App.DependencyInjection;
using BaBaBooeyStrap.App.Services;
using BaBaBooeyStrap.App.Views;
using BaBaBooeyStrap.App.Views.Dialogs;
using BaBaBooeyStrap.Core;
using BaBaBooeyStrap.Core.Launch;
using BaBaBooeyStrap.Core.Logging;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Models.Enums;

namespace BaBaBooeyStrap.App;

public partial class App : Application
{
    private AppServices? _services;
    private MainWindow? _mainWindow;
    private bool _isQuietMode;
    private bool _isShuttingDown;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            _services = AppServiceConfiguration.CreateCoreServices(initializeGlobalEnvironment: true);
            var appSettings = _services.SettingsManager.Prop;

            if (appSettings.WPFSoftwareRender)
            {
                RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            }

            LocalizationService.Instance.ApplyLanguage(appSettings.Language);
            ThemeService.ApplyTheme(
                appSettings.Theme,
                appSettings.UseAcrylicBackground
                    ? Wpf.Ui.Controls.WindowBackdropType.Acrylic
                    : Wpf.Ui.Controls.WindowBackdropType.Mica);

            var launchSettings = new LaunchSettings(e.Args);
            _isQuietMode = launchSettings.QuietFlag.Active;

            // 1. Immediate dedicated modes (Watcher / Uninstall) bypass single-instance UI guard
            if (launchSettings.WatcherFlag.Active)
            {
                await RunDedicatedWatcherModeAsync(launchSettings);
                ShutdownApp(0);
                return;
            }

            if (launchSettings.UninstallFlag.Active)
            {
                RunUninstallMode(launchSettings);
                ShutdownApp(0);
                return;
            }

            // 2. Single-instance App Mutex + Named Pipe forwarding
            var ipc = AppServiceConfiguration.CreateSingleInstanceIpc();
            _services.Ipc = ipc;
            if (!ipc.TryAcquirePrimary())
            {
                await ipc.SendArgumentsToPrimaryAsync(e.Args);
                ShutdownApp(0);
                return;
            }

            ipc.ArgumentsReceived += (_, forwardedArgs) =>
            {
                Dispatcher.InvokeAsync(() => HandleForwardedCliArgs(forwardedArgs));
            };
            ipc.StartPipeServer();

            // 3. Initialize core orchestrator & Multi-Instance engine
            var orchestrator = AppServiceConfiguration.CreateBootstrapperOrchestrator(_services);
            if (appSettings.MultiInstanceEnabled && !launchSettings.NoHoldFlag.Active)
            {
                orchestrator.MultiInstanceController.Enable();
            }

            // 4. Initialize System Tray Keep-Alive
            var trayWrapper = AppServiceConfiguration.CreateNotifyIconWrapper(
                _services,
                onOpenSettings: ShowMainWindow,
                onLaunchRoblox: () => _ = LaunchPlayerFromTrayOrSettingsAsync(new LaunchSettings()),
                onToggleMultiInstance: ToggleMultiInstanceFromTray,
                onExitRequested: HandleTrayExitRequested);

            orchestrator.ActiveServersChanged += (_, _) => _services?.TrayWrapper?.RefreshServerLocationStatus();
            orchestrator.ServerLocationResolved += (_, info) =>
            {
                var (title, message) = NotifyIconWrapper.FormatServerJoinNotification(info);
                _services?.TrayWrapper?.ShowBalloonNotification(title, message, 5000);
            };

            trayWrapper.Initialize(
                appSettings.BootstrapperIcon,
                appSettings.BootstrapperIconCustomLocation);

            // 5. Route initial CLI command
            await RouteCliCommandAsync(launchSettings);
        }
        catch (Exception ex)
        {
            Logger.ExceptionStatic("App.OnStartup", ex);
            if (!_isQuietMode)
            {
                MessageBox.Show(
                    ex.Message,
                    LocalizationService.Instance.GetString("Str_Dialog_ErrorTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            ShutdownApp(1);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.ExceptionStatic("App.DispatcherUnhandledException", e.Exception);
        e.Handled = true;

        if (!_isQuietMode)
        {
            try
            {
                MessageBox.Show(
                    e.Exception.Message,
                    LocalizationService.Instance.GetString("Str_Dialog_ErrorTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            catch { }
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Logger.ExceptionStatic("App.UnobservedTaskException", e.Exception);
        e.SetObserved();
    }

    private async Task RouteCliCommandAsync(LaunchSettings launchSettings)
    {
        if (_services == null) return;

        var installer = _services.InstallerService;
        var settingsManager = _services.SettingsManager;
        var stateManager = _services.StateManager;
        var orchestrator = _services.Orchestrator;
        var trayWrapper = _services.TrayWrapper;

        if (launchSettings.InstallFlag.Active)
        {
            installer.Install();
            trayWrapper?.ShowBalloonNotification(
                LocalizationService.Instance.GetString("Str_Tray_InstalledTitle"),
                LocalizationService.Instance.GetString("Str_Tray_InstalledMessage"));
            if (!launchSettings.QuietFlag.Active && launchSettings.RobloxLaunchMode == LaunchMode.None)
            {
                ShowMainWindow();
            }
            else if (launchSettings.QuietFlag.Active && !settingsManager.Prop.MultiInstanceEnabled)
            {
                ShutdownApp(0);
            }
            return;
        }

        if (!launchSettings.QuietFlag.Active &&
            (string.IsNullOrWhiteSpace(stateManager.Prop.InstallLocation) || !installer.IsInstalledHere()))
        {
            installer.Install(createDesktopShortcut: false, createStartMenuShortcuts: true, registerProtocols: true);
        }

        if (launchSettings.HoldFlag.Active)
        {
            orchestrator?.MultiInstanceController.Enable();
            _mainWindow?.ViewModel.AccountManagement.RefreshMultiInstanceStatus();
            trayWrapper?.RefreshMultiInstanceMenuStatus();
            if (!launchSettings.QuietFlag.Active)
            {
                trayWrapper?.ShowBalloonNotification(
                    LocalizationService.Instance.GetString("Str_Tray_HoldTitle"),
                    LocalizationService.Instance.GetString("Str_Tray_HoldMessage"));
            }

            if (launchSettings.MenuFlag.Active)
            {
                ShowMainWindow();
            }
            return;
        }

        if (launchSettings.AccountFlag.Active)
        {
            await LaunchAccountFromCliAsync(launchSettings);
            return;
        }

        if (launchSettings.MenuFlag.Active ||
            (launchSettings.RobloxLaunchMode == LaunchMode.None && !launchSettings.NoLaunchFlag.Active))
        {
            ShowMainWindow();
            return;
        }

        await LaunchPlayerFromTrayOrSettingsAsync(launchSettings);
    }

    private async void HandleForwardedCliArgs(string[] args)
    {
        try
        {
            var forwarded = new LaunchSettings(args);
            if (forwarded.HoldFlag.Active)
            {
                _services?.Orchestrator?.MultiInstanceController.Enable();
                _mainWindow?.ViewModel.AccountManagement.RefreshMultiInstanceStatus();
                _services?.TrayWrapper?.RefreshMultiInstanceMenuStatus();
                if (forwarded.MenuFlag.Active)
                {
                    ShowMainWindow();
                }
                return;
            }

            if (forwarded.AccountFlag.Active)
            {
                await LaunchAccountFromCliAsync(forwarded);
                return;
            }

            if (forwarded.MenuFlag.Active || forwarded.RobloxLaunchMode == LaunchMode.None)
            {
                ShowMainWindow();
            }
            else
            {
                await LaunchPlayerFromTrayOrSettingsAsync(forwarded);
            }
        }
        catch (Exception ex)
        {
            Logger.ExceptionStatic("App.HandleForwardedCliArgs", ex);
        }
    }

    private async Task LaunchAccountFromCliAsync(LaunchSettings launchSettings)
    {
        if (_services?.Orchestrator == null) return;

        var orchestrator = _services.Orchestrator;
        var settingsManager = _services.SettingsManager;
        var accountStore = _services.AccountStore;
        var trayWrapper = _services.TrayWrapper;
        var globalSettings = _services.GlobalSettingsManager;

        string? identifier = launchSettings.AccountFlag.Data;
        string? rawTarget = launchSettings.TargetFlag.Data;
        if (string.IsNullOrWhiteSpace(identifier) || string.IsNullOrWhiteSpace(rawTarget))
        {
            if (!launchSettings.QuietFlag.Active)
            {
                MessageBox.Show(
                    LocalizationService.Instance.GetString("Str_Cli_MissingAccountOrTarget"),
                    LocalizationService.Instance.GetString("Str_Dialog_LaunchErrorTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            return;
        }

        var accounts = accountStore.LoadAll();
        RobloxAccount? account = Guid.TryParse(identifier, out var id)
            ? accounts.FirstOrDefault(a => a.Id == id)
            : accounts.FirstOrDefault(a => string.Equals(a.Username, identifier, StringComparison.OrdinalIgnoreCase));

        if (account == null)
        {
            Logger.WarnStatic("App.Cli", $"Account not found: '{identifier}'.");
            if (!launchSettings.QuietFlag.Active)
            {
                MessageBox.Show(
                    LocalizationService.Instance.Format("Str_Cli_AccountNotFoundFormat", identifier),
                    LocalizationService.Instance.GetString("Str_Dialog_LaunchErrorTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            return;
        }

        bool previousMultiInstance = settingsManager.Prop.MultiInstanceEnabled;
        if (launchSettings.NoHoldFlag.Active)
        {
            settingsManager.Prop.MultiInstanceEnabled = false;
        }
        else if (previousMultiInstance)
        {
            orchestrator.MultiInstanceController.Enable();
        }

        string? settingsBackup = null;
        try
        {
            string effectiveTarget = RobloxLauncher.BuildEffectiveTargetInput(rawTarget, launchSettings.JobIdFlag.Data);
            var parsedTarget = RobloxLauncher.ParseTargetInput(effectiveTarget);

            if (account.RobloxSettings?.HasOverrides == true)
            {
                settingsBackup = globalSettings.CaptureBackup();
                globalSettings.ApplyProfile(account.RobloxSettings);
            }

            var previousPids = orchestrator.ProcessSupervisor.CaptureRunningClientPids();
            var result = await orchestrator.LaunchAccountAsync(account, parsedTarget);

            if (result.Success)
            {
                accountStore.Upsert(account);
                _mainWindow?.ViewModel.AccountManagement.ReloadAccounts();
                trayWrapper?.ShowBalloonNotification(
                    LocalizationService.Instance.GetString("Str_Tray_LaunchedTitle"),
                    result.ProcessId.HasValue
                        ? LocalizationService.Instance.Format("Str_Tray_LaunchedPidFormat", result.ProcessId.Value)
                        : LocalizationService.Instance.GetString("Str_Tray_LaunchedDone"));

                if (settingsBackup != null)
                {
                    await orchestrator.ProcessSupervisor.WaitForNewClientMainWindowAsync(previousPids);
                }
            }
            else if (!launchSettings.QuietFlag.Active)
            {
                MessageBox.Show(
                    result.ErrorMessage ?? LocalizationService.Instance.GetString("Str_Dialog_LaunchFailedFallback"),
                    LocalizationService.Instance.GetString("Str_Dialog_LaunchErrorTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            Logger.ExceptionStatic("App.LaunchAccountFromCliAsync", ex);
            if (!launchSettings.QuietFlag.Active)
            {
                MessageBox.Show(
                    ex.Message,
                    LocalizationService.Instance.GetString("Str_Dialog_LaunchErrorTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        finally
        {
            settingsManager.Prop.MultiInstanceEnabled = previousMultiInstance;
            if (settingsBackup != null)
            {
                try { globalSettings.RestoreBackup(settingsBackup); } catch { }
            }
        }

        if (launchSettings.DetachFlag.Active && (_mainWindow == null || !_mainWindow.IsVisible))
        {
            ShutdownApp(0);
        }
    }

    private void ShowMainWindow()
    {
        if (_services == null) return;

        if (_mainWindow == null)
        {
            _mainWindow = AppServiceConfiguration.CreateMainWindow(
                _services,
                onLaunchRequested: () => _ = LaunchPlayerFromTrayOrSettingsAsync(new LaunchSettings()));

            _mainWindow.Closed += (_, _) =>
            {
                _mainWindow = null;
                if (!_isShuttingDown && _services?.SettingsManager.Prop.MinimizeToTray == false)
                {
                    ShutdownApp(0);
                }
            };
        }

        _mainWindow.Show();
        if (_mainWindow.WindowState == System.Windows.WindowState.Minimized)
            _mainWindow.WindowState = System.Windows.WindowState.Normal;
        _mainWindow.Activate();
        _mainWindow.ViewModel.AccountManagement.RefreshMultiInstanceStatus();
        _mainWindow.ViewModel.AccountManagement.RefreshProtocolStatus();
    }

    private async Task LaunchPlayerFromTrayOrSettingsAsync(LaunchSettings launchSettings)
    {
        if (_services?.Orchestrator == null) return;

        var orchestrator = _services.Orchestrator;
        var settingsManager = _services.SettingsManager;
        var trayWrapper = _services.TrayWrapper;

        using var cts = new CancellationTokenSource();
        IBootstrapperDialog? dialog = null;

        if (!launchSettings.QuietFlag.Active)
        {
            dialog = BootstrapperDialogFactory.Create(settingsManager.Prop, cts, _services.Paths);
            dialog.ShowBootstrapper();
        }

        try
        {
            var progress = dialog?.ViewModel;
            void UpdateStatus(string msg)
            {
                if (dialog == null) return;
                Dispatcher.InvokeAsync(() => dialog.Message = msg);
            }

            var result = await orchestrator.LaunchWithProgressAsync(launchSettings, progress, UpdateStatus, cts.Token);
            dialog?.CloseBootstrapper();

            if (result.Success)
            {
                trayWrapper?.ShowBalloonNotification(
                    LocalizationService.Instance.GetString("Str_Tray_LaunchedTitle"),
                    result.ProcessId.HasValue
                        ? LocalizationService.Instance.Format("Str_Tray_LaunchedPidFormat", result.ProcessId.Value)
                        : LocalizationService.Instance.GetString("Str_Tray_LaunchedDone"));
            }
            else if (!launchSettings.QuietFlag.Active)
            {
                MessageBox.Show(
                    result.ErrorMessage ?? LocalizationService.Instance.GetString("Str_Dialog_LaunchFailedFallback"),
                    LocalizationService.Instance.GetString("Str_Dialog_LaunchErrorTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (OperationCanceledException)
        {
            dialog?.CloseBootstrapper();
            Logger.InfoStatic("App", "Launch cancelled by user.");
        }
        catch (Exception ex)
        {
            dialog?.CloseBootstrapper();
            Logger.ExceptionStatic("App", ex);
            if (!launchSettings.QuietFlag.Active)
            {
                MessageBox.Show(
                    ex.Message,
                    LocalizationService.Instance.GetString("Str_Dialog_ErrorTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        if (!settingsManager.Prop.MultiInstanceEnabled &&
            (_mainWindow == null || !_mainWindow.IsVisible) &&
            orchestrator.ProcessSupervisor.GetRunningRobloxPids().Count == 0)
        {
            ShutdownApp(0);
        }
    }

    private async Task RunDedicatedWatcherModeAsync(LaunchSettings launchSettings)
    {
        if (_services == null) return;

        try
        {
            WatcherData? data = null;
            foreach (var arg in launchSettings.Args)
            {
                if (string.IsNullOrWhiteSpace(arg) || arg.StartsWith('-')) continue;
                if (File.Exists(arg))
                {
                    string json = await File.ReadAllTextAsync(arg);
                    data = JsonSerializer.Deserialize<WatcherData>(json);
                    break;
                }
                if (int.TryParse(arg, out int pid) && pid > 0)
                {
                    data = new WatcherData { ProcessId = pid, LogFile = _services.Paths.RobloxLogs };
                    break;
                }
            }

            if (data == null)
            {
                var running = Process.GetProcessesByName("RobloxPlayerBeta").FirstOrDefault();
                if (running != null)
                {
                    data = new WatcherData { ProcessId = running.Id, LogFile = _services.Paths.RobloxLogs };
                    running.Dispose();
                }
            }

            if (data == null || data.ProcessId <= 0) return;

            using var scope = AppServiceConfiguration.CreateDedicatedWatcherScope(data, _services.SettingsManager.Prop);
            scope.WindowManipulation?.StartHook();
            await scope.Session.RunAsync();
        }
        catch (Exception ex)
        {
            Logger.ExceptionStatic("App.Watcher", ex);
        }
    }

    private void RunUninstallMode(LaunchSettings launchSettings)
    {
        if (_services == null) return;

        if (!launchSettings.QuietFlag.Active)
        {
            var confirm = MessageBox.Show(
                LocalizationService.Instance.GetString("Str_Dialog_UninstallConfirm"),
                LocalizationService.Instance.GetString("Str_Dialog_UninstallTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes) return;
        }

        _services.InstallerService.Uninstall(removeDataDirectory: true, scheduleSelfDelete: true);
    }

    private void ToggleMultiInstanceFromTray()
    {
        if (_services?.Orchestrator == null) return;

        var settingsManager = _services.SettingsManager;
        var orchestrator = _services.Orchestrator;

        bool next = !settingsManager.Prop.MultiInstanceEnabled;
        settingsManager.Prop.MultiInstanceEnabled = next;
        settingsManager.Save();

        if (next)
            orchestrator.MultiInstanceController.Enable();
        else
            orchestrator.MultiInstanceController.Disable();

        _mainWindow?.ViewModel.AccountManagement.RefreshMultiInstanceStatus();
    }

    private void HandleTrayExitRequested()
    {
        if (_services?.Orchestrator != null)
        {
            var runningPids = _services.Orchestrator.MultiInstanceController.GetStatus().RunningRobloxPids;
            if (runningPids.Count > 0)
            {
                var choice = MessageBox.Show(
                    LocalizationService.Instance.Format("Str_Dialog_ConfirmExitMessageFormat", runningPids.Count),
                    LocalizationService.Instance.GetString("Str_Dialog_ConfirmExitTitle"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (choice != MessageBoxResult.Yes)
                    return;
            }
        }

        ShutdownApp(0);
    }

    private void ShutdownApp(int exitCode)
    {
        if (_isShuttingDown) return;
        _isShuttingDown = true;

        _mainWindow?.ForceClose();
        _services?.Dispose();
        Shutdown(exitCode);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }
}
