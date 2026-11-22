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
using BaBaBooeyStrap.Bootstrapper.Settings;
using BaBaBooeyStrap.Core;
using BaBaBooeyStrap.Core.Launch;
using BaBaBooeyStrap.Core.Logging;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Models.Enums;
using BaBaBooeyStrap.Core.Storage;

namespace BaBaBooeyStrap.App;

public partial class App : Application
{
    private AppServices? _services;
    private MainWindow? _mainWindow;
    private bool _isQuietMode;
    private bool _isShuttingDown;
    private bool _hasCheckedAppUpdateThisSession;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            _services = AppServiceConfiguration.CreateCoreServices(initializeGlobalEnvironment: true);
            _services.GlobalSettingsGuard?.RecoverOnStartup();
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
                onExitRequested: HandleTrayExitRequested,
                onQuickLaunchAccount: (acc, target) => _ = LaunchAccountFromTrayAsync(acc, target));

            orchestrator.ActiveServersChanged += (_, _) => _services?.TrayWrapper?.RefreshServerLocationStatus();
            orchestrator.ServerLocationResolved += (_, info) =>
            {
                _services?.TrayWrapper?.ShowServerJoinToast(info, 5000);
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
                try
                {
                    FluentDialogService.ShowError(
                        LocalizationService.Instance.GetString("Str_Dialog_ErrorTitle"),
                        ex.Message);
                }
                catch { }
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
                FluentDialogService.ShowWarning(
                    LocalizationService.Instance.GetString("Str_Dialog_ErrorTitle"),
                    e.Exception.Message);
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

        if (!string.IsNullOrWhiteSpace(stateManager.Prop.InstallLocation) && !stateManager.Prop.OnboardingCompleted)
        {
            stateManager.Prop.OnboardingCompleted = true;
            stateManager.Save();
        }

        if (!launchSettings.QuietFlag.Active &&
            !stateManager.Prop.OnboardingCompleted &&
            string.IsNullOrWhiteSpace(stateManager.Prop.InstallLocation) &&
            launchSettings.RobloxLaunchMode == LaunchMode.None &&
            !launchSettings.AccountFlag.Active &&
            !launchSettings.HoldFlag.Active &&
            !launchSettings.NoLaunchFlag.Active)
        {
            var firstRun = new FirstRunDialog(settingsManager.Prop);
            if (firstRun.ShowDialog() != true)
            {
                ShutdownApp(0);
                return;
            }

            installer.Install(
                createDesktopShortcut: firstRun.CreateDesktopShortcut,
                createStartMenuShortcuts: true,
                registerProtocols: firstRun.RegisterProtocols);

            if (settingsManager.Prop.MultiInstanceEnabled)
                orchestrator?.MultiInstanceController.Enable();
            else
                orchestrator?.MultiInstanceController.Disable();

            if (settingsManager.Prop.RunOnStartup)
            {
                string startupExe = File.Exists(_services.Paths.Application) ? _services.Paths.Application : _services.Paths.Process;
                WindowsRegistry.SetAppStartup(startupExe, true);
            }

            stateManager.Prop.OnboardingCompleted = true;
            settingsManager.Save();
            stateManager.Save();
        }
        else if (!launchSettings.QuietFlag.Active &&
            (string.IsNullOrWhiteSpace(stateManager.Prop.InstallLocation) ||
             !installer.IsInstalledHere() ||
             installer.NeedsInstallSync() ||
             !RobloxLauncher.IsRobloxProtocolRegistered(_services.Paths.Application)))
        {
            installer.Install(createDesktopShortcut: false, createStartMenuShortcuts: true, registerProtocols: true);
        }

        if (launchSettings.NoLaunchFlag.Active &&
            !launchSettings.MenuFlag.Active &&
            launchSettings.RobloxLaunchMode == LaunchMode.None &&
            !launchSettings.AccountFlag.Active &&
            !launchSettings.HoldFlag.Active)
        {
            return;
        }

        if (launchSettings.HoldFlag.Active)
        {
            orchestrator?.MultiInstanceController.Enable();
            _mainWindow?.ViewModel.AccountManagement.RefreshMultiInstanceStatus();
            trayWrapper?.RefreshMultiInstanceMenuStatus();

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

        string? identifier = launchSettings.AccountFlag.Data;
        string? rawTarget = launchSettings.TargetFlag.Data;
        if (string.IsNullOrWhiteSpace(identifier) || string.IsNullOrWhiteSpace(rawTarget))
        {
            if (!launchSettings.QuietFlag.Active)
            {
                FluentDialogService.ShowWarning(
                    LocalizationService.Instance.GetString("Str_Dialog_LaunchErrorTitle"),
                    LocalizationService.Instance.GetString("Str_Cli_MissingAccountOrTarget"));
            }
            return;
        }

        var accounts = _services.AccountStore.LoadAll();
        RobloxAccount? account = Guid.TryParse(identifier, out var id)
            ? accounts.FirstOrDefault(a => a.Id == id)
            : accounts.FirstOrDefault(a => string.Equals(a.Username, identifier, StringComparison.OrdinalIgnoreCase));

        if (account == null)
        {
            Logger.WarnStatic("App.Cli", $"Account not found: '{identifier}'.");
            if (!launchSettings.QuietFlag.Active)
            {
                FluentDialogService.ShowWarning(
                    LocalizationService.Instance.GetString("Str_Dialog_LaunchErrorTitle"),
                    LocalizationService.Instance.Format("Str_Cli_AccountNotFoundFormat", identifier));
            }
            return;
        }

        await ExecuteAccountLaunchAsync(
            account,
            rawTarget,
            launchSettings.JobIdFlag.Data,
            launchSettings.QuietFlag.Active,
            launchSettings.NoHoldFlag.Active);

        if (launchSettings.DetachFlag.Active && (_mainWindow == null || !_mainWindow.IsVisible))
        {
            ShutdownApp(0);
        }
    }

    private async Task LaunchAccountFromTrayAsync(RobloxAccount account, SavedJoinTarget? savedTarget)
    {
        if (_services == null) return;

        var state = _services.StateManager.Prop;
        string? rawTarget = savedTarget != null ? savedTarget.TargetInput : state.LastTargetInput;
        string? jobId = savedTarget != null ? savedTarget.JobId : state.LastJobId;

        if (string.IsNullOrWhiteSpace(rawTarget))
        {
            FluentDialogService.ShowWarning(
                LocalizationService.Instance.GetString("Str_Tray_QuickLaunch"),
                LocalizationService.Instance.GetString("Str_Tray_QuickLaunchNoTarget"));
            ShowMainWindow();
            return;
        }

        await ExecuteAccountLaunchAsync(
            account,
            rawTarget,
            jobId,
            quietMode: false,
            forceDisableMultiInstance: false);
    }

    private async Task ExecuteAccountLaunchAsync(
        RobloxAccount account,
        string rawTarget,
        string? jobId,
        bool quietMode,
        bool forceDisableMultiInstance)
    {
        if (_services?.Orchestrator == null) return;

        var orchestrator = _services.Orchestrator;
        var settingsManager = _services.SettingsManager;
        var accountStore = _services.AccountStore;
        var globalSettingsGuard = _services.GlobalSettingsGuard;

        bool previousMultiInstance = settingsManager.Prop.MultiInstanceEnabled;
        if (forceDisableMultiInstance)
        {
            settingsManager.Prop.MultiInstanceEnabled = false;
        }
        else if (previousMultiInstance)
        {
            orchestrator.MultiInstanceController.Enable();
        }

        using var cts = new CancellationTokenSource();
        IBootstrapperDialog? dialog = null;

        if (!quietMode)
        {
            dialog = BootstrapperDialogFactory.Create(settingsManager.Prop, cts, _services.Paths);
            dialog.ShowBootstrapper();
        }

        IAsyncDisposable? guardSession = null;
        var pidsToProtect = new HashSet<int>();
        bool shouldProtect = false;
        bool launchSucceeded = false;

        try
        {
            if (globalSettingsGuard != null)
            {
                guardSession = await globalSettingsGuard.BeginLaunchSessionAsync(cts.Token);
            }

            string effectiveTarget = RobloxLauncher.BuildEffectiveTargetInput(rawTarget, jobId);
            var parsedTarget = RobloxLauncher.ParseTargetInput(effectiveTarget);

            var previousPids = orchestrator.ProcessSupervisor.CaptureRunningClientPids();
            bool hasOverride = account.RobloxSettings?.HasOverrides == true;
            shouldProtect = hasOverride || (settingsManager.Prop.MultiInstanceEnabled && previousPids.Count > 0);

            if (shouldProtect && globalSettingsGuard != null)
            {
                globalSettingsGuard.EnsureBackupCaptured();
                if (hasOverride)
                {
                    globalSettingsGuard.ApplyProfileForLaunch(account.RobloxSettings!);
                }
            }

            var progress = dialog?.ViewModel;
            void UpdateStatus(string msg)
            {
                if (dialog == null) return;
                Dispatcher.InvokeAsync(() => dialog.Message = msg);
            }

            var result = await orchestrator.LaunchAccountAsync(
                account,
                parsedTarget,
                progress,
                UpdateStatus,
                skipDeploymentCheck: false,
                cts.Token);

            dialog?.CloseBootstrapper();

            if (result.Success)
            {
                launchSucceeded = true;
                accountStore.Upsert(account);
                _mainWindow?.ViewModel.AccountManagement.ReloadAccounts();

                if (result.ProcessId.HasValue)
                {
                    pidsToProtect.Add(result.ProcessId.Value);
                }

                if (shouldProtect)
                {
                    var waitResult = await orchestrator.ProcessSupervisor.WaitForNewClientMainWindowAsync(
                        previousPids,
                        ct: cts.Token);
                    if (waitResult.ProcessId.HasValue)
                    {
                        pidsToProtect.Add(waitResult.ProcessId.Value);
                    }

                    if (hasOverride)
                    {
                        await Task.Delay(1200, cts.Token);
                    }
                }

                if (settingsManager.Prop.MultiInstanceEnabled)
                {
                    foreach (int existingPid in previousPids)
                    {
                        pidsToProtect.Add(existingPid);
                    }
                }
            }
            else if (!quietMode)
            {
                FluentDialogService.ShowWarning(
                    LocalizationService.Instance.GetString("Str_Dialog_LaunchErrorTitle"),
                    result.ErrorMessage ?? LocalizationService.Instance.GetString("Str_Dialog_LaunchFailedFallback"));
            }
        }
        catch (OperationCanceledException)
        {
            dialog?.CloseBootstrapper();
            Logger.InfoStatic("App.ExecuteAccountLaunchAsync", "Quick launch cancelled by user.");
        }
        catch (Exception ex)
        {
            dialog?.CloseBootstrapper();
            Logger.ExceptionStatic("App.ExecuteAccountLaunchAsync", ex);
            if (!quietMode)
            {
                FluentDialogService.ShowError(
                    LocalizationService.Instance.GetString("Str_Dialog_LaunchErrorTitle"),
                    ex.Message);
            }
        }
        finally
        {
            dialog?.CloseBootstrapper();
            settingsManager.Prop.MultiInstanceEnabled = previousMultiInstance;
            if (globalSettingsGuard != null)
            {
                try
                {
                    globalSettingsGuard.RestoreOriginalAndProtect(
                        pidsToProtect,
                        enableReadOnlyProtection: shouldProtect && (launchSucceeded || pidsToProtect.Count > 0));
                }
                catch { }
            }

            if (guardSession != null)
            {
                try { await guardSession.DisposeAsync(); } catch { }
            }
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

        TriggerBackgroundAppUpdateCheckIfDue();
    }

    private void TriggerBackgroundAppUpdateCheckIfDue()
    {
        if (_hasCheckedAppUpdateThisSession || _isShuttingDown || _services?.AppUpdateService == null)
        {
            return;
        }

        var settings = _services.SettingsManager.Prop;
        var stateManager = _services.StateManager;
        if (!settings.CheckAppUpdates)
        {
            return;
        }

        if (stateManager.Prop.LastAppUpdateCheck is { } lastCheck &&
            DateTimeOffset.UtcNow - lastCheck < TimeSpan.FromHours(6))
        {
            return;
        }

        _hasCheckedAppUpdateThisSession = true;
        var updateService = _services.AppUpdateService;

        _ = Task.Run(async () =>
        {
            try
            {
                var info = await updateService.CheckForUpdateAsync().ConfigureAwait(false);
                stateManager.Prop.LastAppUpdateCheck = DateTimeOffset.UtcNow;
                stateManager.Save();

                if (!info.IsUpdateAvailable)
                {
                    return;
                }

                string latestStr = info.LatestVersion.ToString();
                if (!string.IsNullOrWhiteSpace(stateManager.Prop.SkippedAppVersion) &&
                    string.Equals(stateManager.Prop.SkippedAppVersion, latestStr, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    if (_isShuttingDown) return;

                    var dlg = new UpdateAvailableDialog(info, updateService);
                    if (_mainWindow != null && _mainWindow.IsLoaded && _mainWindow.IsVisible)
                    {
                        dlg.Owner = _mainWindow;
                    }
                    dlg.ShowDialog();

                    if (!string.IsNullOrWhiteSpace(dlg.SkippedVersion))
                    {
                        stateManager.Prop.SkippedAppVersion = dlg.SkippedVersion;
                        stateManager.Save();
                    }

                    if (dlg.UpdateInstalledNeedsRestart)
                    {
                        ShutdownApp(0);
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.WarnStatic("App.UpdateCheck", $"Background update check failed: {ex.Message}");
            }
        });
    }

    private async Task LaunchPlayerFromTrayOrSettingsAsync(LaunchSettings launchSettings)
    {
        if (_services?.Orchestrator == null) return;

        var orchestrator = _services.Orchestrator;
        var settingsManager = _services.SettingsManager;
        var trayWrapper = _services.TrayWrapper;

        if (!launchSettings.QuietFlag.Active && settingsManager.Prop.ConfirmLaunches)
        {
            bool confirmed = FluentDialogService.ShowConfirm(
                LocalizationService.Instance.GetString("Str_Dialog_ConfirmLaunchTitle"),
                LocalizationService.Instance.GetString("Str_Dialog_ConfirmLaunchGenericMessage"),
                confirmText: LocalizationService.Instance.GetString("Str_Common_Launch"));
            if (!confirmed)
            {
                return;
            }
        }

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

            try
            {
                _services.InstallerService.EnsureProtocolsPreserved();
                _mainWindow?.ViewModel.AccountManagement.RefreshProtocolStatus();
            }
            catch { }

            if (!result.Success && !launchSettings.QuietFlag.Active)
            {
                FluentDialogService.ShowWarning(
                    LocalizationService.Instance.GetString("Str_Dialog_LaunchErrorTitle"),
                    result.ErrorMessage ?? LocalizationService.Instance.GetString("Str_Dialog_LaunchFailedFallback"));
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
                FluentDialogService.ShowError(
                    LocalizationService.Instance.GetString("Str_Dialog_ErrorTitle"),
                    ex.Message);
            }
        }

        if (!settingsManager.Prop.MultiInstanceEnabled &&
            !settingsManager.Prop.MinimizeToTray &&
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
            bool confirmed = FluentDialogService.ShowConfirm(
                LocalizationService.Instance.GetString("Str_Dialog_UninstallTitle"),
                LocalizationService.Instance.GetString("Str_Dialog_UninstallConfirm"),
                isDestructive: true);

            if (!confirmed) return;
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
                bool confirmed = FluentDialogService.ShowConfirm(
                    LocalizationService.Instance.GetString("Str_Dialog_ConfirmExitTitle"),
                    LocalizationService.Instance.Format("Str_Dialog_ConfirmExitMessageFormat", runningPids.Count),
                    isDestructive: true);

                if (!confirmed)
                    return;
            }
        }

        ShutdownApp(0);
    }

    internal void RequestShutdown(int exitCode = 0) => ShutdownApp(exitCode);

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
