using System.Net.Http;
using BaBaBooeyStrap.App.Services;
using BaBaBooeyStrap.App.Services.Pipeline;
using BaBaBooeyStrap.App.ViewModels;
using BaBaBooeyStrap.App.Views;
using BaBaBooeyStrap.Bootstrapper.Settings;
using BaBaBooeyStrap.Core;
using BaBaBooeyStrap.Core.Logging;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Storage;
using BaBaBooeyStrap.Integrations.Discord;
using BaBaBooeyStrap.Integrations.Watcher;
using BaBaBooeyStrap.Integrations.Window;

namespace BaBaBooeyStrap.App.DependencyInjection;

public static class AppServiceConfiguration
{
    public static AppServices CreateCoreServices(IPaths? paths = null, bool initializeGlobalEnvironment = false)
    {
        if (initializeGlobalEnvironment)
        {
            Paths.Initialize();
            Paths.EnsureDirectoriesExist();
            Logger.InitializeStatic();
        }

        IPaths resolvedPaths = paths ?? Paths.Current;

        var settingsManager = new JsonManager<Settings>(resolvedPaths.SettingsFile);
        settingsManager.Load(alertFailure: false);

        var stateManager = new JsonManager<State>(resolvedPaths.StateFile);
        stateManager.Load(alertFailure: false);

        var robloxStateManager = new JsonManager<RobloxState>(resolvedPaths.RobloxStateFile);
        robloxStateManager.Load(alertFailure: false);

        var accountStore = new DpapiAccountStore(resolvedPaths.AccountsFile);
        var joinTargetStore = new JoinTargetStore(resolvedPaths.TargetsFile);
        var installerService = new InstallerService(resolvedPaths, stateManager);
        var globalSettingsManager = new GlobalSettingsManager(resolvedPaths.RobloxGlobalSettings);
        var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var appUpdateService = new AppUpdateService(resolvedPaths, httpClient, Logger.Current);

        return new AppServices(
            resolvedPaths,
            settingsManager,
            stateManager,
            robloxStateManager,
            accountStore,
            joinTargetStore,
            installerService,
            globalSettingsManager,
            httpClient)
        {
            AppUpdateService = appUpdateService
        };
    }

    public static SingleInstanceIpc CreateSingleInstanceIpc() => new();

    public static IReadOnlyList<IBootstrapPipelineStep> CreateDefaultBootstrapPipeline(
        IPaths paths,
        IJsonManager<Settings> settingsManager,
        IJsonManager<State> stateManager,
        IJsonManager<RobloxState> robloxStateManager,
        HttpClient httpClient,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(settingsManager);
        ArgumentNullException.ThrowIfNull(stateManager);
        ArgumentNullException.ThrowIfNull(robloxStateManager);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);

        return
        [
            new VersionCheckStep(paths, settingsManager, stateManager, robloxStateManager, httpClient, logger),
            new PackageDeploymentStep(paths, stateManager, robloxStateManager, httpClient, logger),
            new ModsApplicationStep(paths, robloxStateManager, logger),
            new PreLaunchCleanupStep(paths, logger)
        ];
    }

    public static BootstrapperOrchestrator CreateBootstrapperOrchestrator(AppServices services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var logger = Logger.Current;
        var pipeline = CreateDefaultBootstrapPipeline(
            services.Paths,
            services.SettingsManager,
            services.StateManager,
            services.RobloxStateManager,
            services.HttpClient,
            logger);

        var orchestrator = new BootstrapperOrchestrator(
            services.Paths,
            services.SettingsManager,
            services.StateManager,
            services.RobloxStateManager,
            logger: logger,
            pipelineSteps: pipeline,
            httpClient: services.HttpClient);

        services.Orchestrator = orchestrator;
        return orchestrator;
    }

    public static NotifyIconWrapper CreateNotifyIconWrapper(
        AppServices services,
        Action onOpenSettings,
        Action onLaunchRoblox,
        Action onToggleMultiInstance,
        Action onExitRequested,
        bool headlessMode = false,
        Action<RobloxAccount, SavedJoinTarget?>? onQuickLaunchAccount = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var orchestrator = services.Orchestrator
            ?? throw new InvalidOperationException("Orchestrator must be initialized before creating NotifyIconWrapper.");

        var tray = new NotifyIconWrapper(
            headlessMode: headlessMode,
            onOpenSettings: onOpenSettings,
            onLaunchRoblox: onLaunchRoblox,
            getMultiInstanceStatus: () => orchestrator.MultiInstanceController.GetStatus(),
            onToggleMultiInstance: onToggleMultiInstance,
            onExitRequested: onExitRequested,
            getActiveServers: () => orchestrator.GetActiveServers(),
            getAccounts: () => services.AccountStore.LoadAll(),
            getSavedTargets: () => services.JoinTargetStore.LoadAll(),
            onQuickLaunchAccount: onQuickLaunchAccount);

        services.TrayWrapper = tray;
        return tray;
    }

    public static MainWindowViewModel CreateMainWindowViewModel(AppServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var orchestrator = services.Orchestrator
            ?? throw new InvalidOperationException("Orchestrator must be initialized before creating MainWindowViewModel.");

        return new MainWindowViewModel(
            services.SettingsManager.Prop,
            services.AccountStore,
            orchestrator.MultiInstanceController,
            orchestrator.ProcessSupervisor,
            orchestrator,
            services.Paths,
            globalSettingsManager: services.GlobalSettingsManager,
            stateManager: services.StateManager,
            robloxStateManager: services.RobloxStateManager,
            joinTargetStore: services.JoinTargetStore,
            settingsManager: services.SettingsManager,
            appUpdateService: services.AppUpdateService);
    }

    public static MainWindow CreateMainWindow(AppServices services, Action onLaunchRequested)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(onLaunchRequested);

        var vm = CreateMainWindowViewModel(services);
        return new MainWindow(
            vm,
            services.SettingsManager,
            services.StateManager,
            services.TrayWrapper,
            onLaunchRequested: onLaunchRequested);
    }

    public static DedicatedWatcherScope CreateDedicatedWatcherScope(WatcherData data, Settings settings)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(settings);

        var watcher = new ActivityWatcher();
        var discord = settings.UseDiscordRichPresence ? new DiscordRpcService() : null;
        var windowManip = settings.EnableWindowManipulation
            ? new WindowManipulationService(new Win32WindowInterop(), new WindowManipulationOptions
            {
                TargetProcessId = (uint)data.ProcessId,
                CustomTitleTemplate = settings.RobloxTitle,
                CustomIconPath = settings.RobloxIcon == Core.Models.Enums.RobloxIcon.IconCustom
                    ? settings.RobloxIconCustomLocation
                    : null,
                BorderlessFullscreen = settings.FakeBorderlessFullscreen
            })
            : null;

        var session = new WatcherSession(data, watcher, new ProcessLifecycleMonitor(), discord, windowManip);
        return new DedicatedWatcherScope(watcher, discord, windowManip, session);
    }
}
