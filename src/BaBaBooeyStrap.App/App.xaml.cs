using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BaBaBooeyStrap.App.Services;
using BaBaBooeyStrap.App.ViewModels;
using BaBaBooeyStrap.App.Views;
using BaBaBooeyStrap.App.Views.Dialogs;
using BaBaBooeyStrap.Bootstrapper.Settings;
using BaBaBooeyStrap.Core;
using BaBaBooeyStrap.Core.Launch;
using BaBaBooeyStrap.Core.Logging;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Models.Enums;
using BaBaBooeyStrap.Core.Storage;
using BaBaBooeyStrap.Integrations.Discord;
using BaBaBooeyStrap.Integrations.Watcher;
using BaBaBooeyStrap.Integrations.Window;
using Wpf.Ui.Appearance;

namespace BaBaBooeyStrap.App;

public partial class App : Application
{
    private SingleInstanceIpc? _ipc;
    private JsonManager<Settings>? _settingsManager;
    private JsonManager<State>? _stateManager;
    private JsonManager<RobloxState>? _robloxStateManager;
    private DpapiAccountStore? _accountStore;
    private JoinTargetStore? _joinTargetStore;
    private BootstrapperOrchestrator? _orchestrator;
    private NotifyIconWrapper? _trayWrapper;
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
            Paths.Initialize();
            Paths.EnsureDirectoriesExist();
            Logger.InitializeStatic();

            _settingsManager = new JsonManager<Settings>(Paths.SettingsFile);
            _settingsManager.Load(alertFailure: false);

            _stateManager = new JsonManager<State>(Paths.StateFile);
            _stateManager.Load(alertFailure: false);

            _robloxStateManager = new JsonManager<RobloxState>(Paths.RobloxStateFile);
            _robloxStateManager.Load(alertFailure: false);

            _accountStore = new DpapiAccountStore(Paths.AccountsFile);
            _joinTargetStore = new JoinTargetStore(Paths.TargetsFile);

            if (_settingsManager.Prop.WPFSoftwareRender)
            {
                RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            }

            LocalizationService.Instance.ApplyLanguage(_settingsManager.Prop.Language);
            ThemeService.ApplyTheme(
                _settingsManager.Prop.Theme,
                _settingsManager.Prop.UseAcrylicBackground
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
            _ipc = new SingleInstanceIpc();
            if (!_ipc.TryAcquirePrimary())
            {
                await _ipc.SendArgumentsToPrimaryAsync(e.Args);
                ShutdownApp(0);
                return;
            }

            _ipc.ArgumentsReceived += (_, forwardedArgs) =>
            {
                Dispatcher.InvokeAsync(() => HandleForwardedCliArgs(forwardedArgs));
            };
            _ipc.StartPipeServer();

            // 3. Initialize core orchestrator & Multi-Instance engine
            _orchestrator = new BootstrapperOrchestrator(
                Paths.Current,
                _settingsManager,
                _stateManager,
                _robloxStateManager);

            if (_settingsManager.Prop.MultiInstanceEnabled && !launchSettings.NoHoldFlag.Active)
            {
                _orchestrator.MultiInstanceController.Enable();
            }

            // 4. Initialize System Tray Keep-Alive
            _trayWrapper = new NotifyIconWrapper(
                headlessMode: false,
                onOpenSettings: ShowMainWindow,
                onLaunchRoblox: () => _ = LaunchPlayerFromTrayOrSettingsAsync(new LaunchSettings()),
                getMultiInstanceStatus: () => _orchestrator.MultiInstanceController.GetStatus(),
                onToggleMultiInstance: ToggleMultiInstanceFromTray,
                onExitRequested: HandleTrayExitRequested,
                getActiveServers: () => _orchestrator.GetActiveServers());

            _orchestrator.ActiveServersChanged += (_, _) => _trayWrapper?.RefreshServerLocationStatus();
            _orchestrator.ServerLocationResolved += (_, info) =>
            {
                var (title, message) = NotifyIconWrapper.FormatServerJoinNotification(info);
                _trayWrapper?.ShowBalloonNotification(title, message, 5000);
            };

            _trayWrapper.Initialize(
                _settingsManager.Prop.BootstrapperIcon,
                _settingsManager.Prop.BootstrapperIconCustomLocation);

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
        var installer = new InstallerService(Paths.Current, _stateManager);

        if (launchSettings.InstallFlag.Active)
        {
            installer.Install();
            _trayWrapper?.ShowBalloonNotification(
                LocalizationService.Instance.GetString("Str_Tray_InstalledTitle"),
                LocalizationService.Instance.GetString("Str_Tray_InstalledMessage"));
            if (!launchSettings.QuietFlag.Active && launchSettings.RobloxLaunchMode == LaunchMode.None)
            {
                ShowMainWindow();
            }
            else if (launchSettings.QuietFlag.Active && !_settingsManager!.Prop.MultiInstanceEnabled)
            {
                ShutdownApp(0);
            }
            return;
        }

        if (!launchSettings.QuietFlag.Active &&
            (string.IsNullOrWhiteSpace(_stateManager!.Prop.InstallLocation) || !installer.IsInstalledHere()))
        {
            installer.Install(createDesktopShortcut: false, createStartMenuShortcuts: true, registerProtocols: true);
        }

        if (launchSettings.HoldFlag.Active)
        {
            _orchestrator?.MultiInstanceController.Enable();
            _mainWindow?.ViewModel.RefreshMultiInstanceStatus();
            _trayWrapper?.RefreshMultiInstanceMenuStatus();
            if (!launchSettings.QuietFlag.Active)
            {
                _trayWrapper?.ShowBalloonNotification(
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
                _orchestrator?.MultiInstanceController.Enable();
                _mainWindow?.ViewModel.RefreshMultiInstanceStatus();
                _trayWrapper?.RefreshMultiInstanceMenuStatus();
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
        if (_orchestrator == null || _settingsManager == null || _accountStore == null) return;

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

        var accounts = _accountStore.LoadAll();
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

        bool previousMultiInstance = _settingsManager.Prop.MultiInstanceEnabled;
        if (launchSettings.NoHoldFlag.Active)
        {
            _settingsManager.Prop.MultiInstanceEnabled = false;
        }
        else if (previousMultiInstance)
        {
            _orchestrator.MultiInstanceController.Enable();
        }

        string? settingsBackup = null;
        var globalSettings = new GlobalSettingsManager(Paths.RobloxGlobalSettings);
        try
        {
            string effectiveTarget = RobloxLauncher.BuildEffectiveTargetInput(rawTarget, launchSettings.JobIdFlag.Data);
            var parsedTarget = RobloxLauncher.ParseTargetInput(effectiveTarget);

            if (account.RobloxSettings?.HasOverrides == true)
            {
                settingsBackup = globalSettings.CaptureBackup();
                globalSettings.ApplyProfile(account.RobloxSettings);
            }

            var previousPids = _orchestrator.ProcessSupervisor.CaptureRunningClientPids();
            var result = await _orchestrator.LaunchAccountAsync(account, parsedTarget);

            if (result.Success)
            {
                _accountStore.Upsert(account);
                _mainWindow?.ViewModel.ReloadAccounts();
                _trayWrapper?.ShowBalloonNotification(
                    LocalizationService.Instance.GetString("Str_Tray_LaunchedTitle"),
                    result.ProcessId.HasValue
                        ? LocalizationService.Instance.Format("Str_Tray_LaunchedPidFormat", result.ProcessId.Value)
                        : LocalizationService.Instance.GetString("Str_Tray_LaunchedDone"));

                if (settingsBackup != null)
                {
                    await _orchestrator.ProcessSupervisor.WaitForNewClientMainWindowAsync(previousPids);
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
            _settingsManager.Prop.MultiInstanceEnabled = previousMultiInstance;
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
        if (_mainWindow == null)
        {
            var vm = new MainWindowViewModel(
                _settingsManager!.Prop,
                _accountStore,
                _orchestrator!.MultiInstanceController,
                _orchestrator.ProcessSupervisor,
                _orchestrator,
                Paths.Current,
                stateManager: _stateManager,
                robloxStateManager: _robloxStateManager,
                joinTargetStore: _joinTargetStore);

            _mainWindow = new MainWindow(
                vm,
                _settingsManager,
                _stateManager!,
                _trayWrapper,
                onLaunchRequested: () => _ = LaunchPlayerFromTrayOrSettingsAsync(new LaunchSettings()));

            _mainWindow.Closed += (_, _) =>
            {
                _mainWindow = null;
                if (!_isShuttingDown && _settingsManager?.Prop.MinimizeToTray == false)
                {
                    ShutdownApp(0);
                }
            };
        }

        _mainWindow.Show();
        if (_mainWindow.WindowState == System.Windows.WindowState.Minimized)
            _mainWindow.WindowState = System.Windows.WindowState.Normal;
        _mainWindow.Activate();
        _mainWindow.ViewModel.RefreshMultiInstanceStatus();
        _mainWindow.ViewModel.RefreshProtocolStatus();
    }

    private async Task LaunchPlayerFromTrayOrSettingsAsync(LaunchSettings launchSettings)
    {
        if (_orchestrator == null || _settingsManager == null) return;

        using var cts = new CancellationTokenSource();
        IBootstrapperDialog? dialog = null;

        if (!launchSettings.QuietFlag.Active)
        {
            dialog = BootstrapperDialogFactory.Create(_settingsManager.Prop, cts, Paths.Current);
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

            var result = await _orchestrator.LaunchWithProgressAsync(launchSettings, progress, UpdateStatus, cts.Token);
            dialog?.CloseBootstrapper();

            if (result.Success)
            {
                _trayWrapper?.ShowBalloonNotification(
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

        if (!_settingsManager.Prop.MultiInstanceEnabled &&
            (_mainWindow == null || !_mainWindow.IsVisible) &&
            _orchestrator.ProcessSupervisor.GetRunningRobloxPids().Count == 0)
        {
            ShutdownApp(0);
        }
    }

    private async Task RunDedicatedWatcherModeAsync(LaunchSettings launchSettings)
    {
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
                    data = new WatcherData { ProcessId = pid, LogFile = Paths.RobloxLogs };
                    break;
                }
            }

            if (data == null)
            {
                var running = Process.GetProcessesByName("RobloxPlayerBeta").FirstOrDefault();
                if (running != null)
                {
                    data = new WatcherData { ProcessId = running.Id, LogFile = Paths.RobloxLogs };
                    running.Dispose();
                }
            }

            if (data == null || data.ProcessId <= 0) return;

            var settings = _settingsManager!.Prop;
            using var watcher = new ActivityWatcher();
            using var discord = settings.UseDiscordRichPresence ? new DiscordRpcService() : null;
            using var windowManip = settings.EnableWindowManipulation
                ? new WindowManipulationService(new Win32WindowInterop(), new WindowManipulationOptions
                {
                    TargetProcessId = (uint)data.ProcessId,
                    CustomTitleTemplate = settings.RobloxTitle,
                    BorderlessFullscreen = settings.FakeBorderlessFullscreen
                })
                : null;

            windowManip?.StartHook();
            using var session = new WatcherSession(data, watcher, new ProcessLifecycleMonitor(), discord, windowManip);
            await session.RunAsync();
        }
        catch (Exception ex)
        {
            Logger.ExceptionStatic("App.Watcher", ex);
        }
    }

    private void RunUninstallMode(LaunchSettings launchSettings)
    {
        if (!launchSettings.QuietFlag.Active)
        {
            var confirm = MessageBox.Show(
                LocalizationService.Instance.GetString("Str_Dialog_UninstallConfirm"),
                LocalizationService.Instance.GetString("Str_Dialog_UninstallTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes) return;
        }

        var installer = new InstallerService(Paths.Current, _stateManager);
        installer.Uninstall(removeDataDirectory: true, scheduleSelfDelete: true);
    }

    private void ToggleMultiInstanceFromTray()
    {
        if (_orchestrator == null || _settingsManager == null) return;
        bool next = !_settingsManager.Prop.MultiInstanceEnabled;
        _settingsManager.Prop.MultiInstanceEnabled = next;
        _settingsManager.Save();

        if (next)
            _orchestrator.MultiInstanceController.Enable();
        else
            _orchestrator.MultiInstanceController.Disable();

        _mainWindow?.ViewModel.RefreshMultiInstanceStatus();
    }

    private void HandleTrayExitRequested()
    {
        if (_orchestrator != null)
        {
            var runningPids = _orchestrator.MultiInstanceController.GetStatus().RunningRobloxPids;
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
        _trayWrapper?.Dispose();
        _orchestrator?.MultiInstanceController.Dispose();
        _orchestrator?.Dispose();
        _ipc?.Dispose();
        Shutdown(exitCode);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayWrapper?.Dispose();
        _orchestrator?.Dispose();
        _ipc?.Dispose();
        base.OnExit(e);
    }
}
