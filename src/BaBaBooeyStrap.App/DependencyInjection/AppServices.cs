using System.Net.Http;
using BaBaBooeyStrap.App.Services;
using BaBaBooeyStrap.Bootstrapper.Settings;
using BaBaBooeyStrap.Core;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Storage;
using BaBaBooeyStrap.Integrations.Discord;
using BaBaBooeyStrap.Integrations.Watcher;
using BaBaBooeyStrap.Integrations.Window;

namespace BaBaBooeyStrap.App.DependencyInjection;

public sealed class AppServices : IDisposable
{
    private bool _disposed;

    public AppServices(
        IPaths paths,
        JsonManager<Settings> settingsManager,
        JsonManager<State> stateManager,
        JsonManager<RobloxState> robloxStateManager,
        DpapiAccountStore accountStore,
        JoinTargetStore joinTargetStore,
        InstallerService installerService,
        GlobalSettingsManager globalSettingsManager,
        HttpClient httpClient)
    {
        Paths = paths ?? throw new ArgumentNullException(nameof(paths));
        SettingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
        StateManager = stateManager ?? throw new ArgumentNullException(nameof(stateManager));
        RobloxStateManager = robloxStateManager ?? throw new ArgumentNullException(nameof(robloxStateManager));
        AccountStore = accountStore ?? throw new ArgumentNullException(nameof(accountStore));
        JoinTargetStore = joinTargetStore ?? throw new ArgumentNullException(nameof(joinTargetStore));
        InstallerService = installerService ?? throw new ArgumentNullException(nameof(installerService));
        GlobalSettingsManager = globalSettingsManager ?? throw new ArgumentNullException(nameof(globalSettingsManager));
        HttpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public IPaths Paths { get; }
    public JsonManager<Settings> SettingsManager { get; }
    public JsonManager<State> StateManager { get; }
    public JsonManager<RobloxState> RobloxStateManager { get; }
    public DpapiAccountStore AccountStore { get; }
    public JoinTargetStore JoinTargetStore { get; }
    public InstallerService InstallerService { get; }
    public GlobalSettingsManager GlobalSettingsManager { get; }
    public HttpClient HttpClient { get; }

    public SingleInstanceIpc? Ipc { get; set; }
    public BootstrapperOrchestrator? Orchestrator { get; set; }
    public NotifyIconWrapper? TrayWrapper { get; set; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { TrayWrapper?.Dispose(); } catch { }
        try { Orchestrator?.MultiInstanceController.Dispose(); } catch { }
        try { Orchestrator?.Dispose(); } catch { }
        try { Ipc?.Dispose(); } catch { }
        try { HttpClient.Dispose(); } catch { }
    }
}

public sealed class DedicatedWatcherScope : IDisposable
{
    private bool _disposed;

    public DedicatedWatcherScope(
        ActivityWatcher activityWatcher,
        DiscordRpcService? discordRpc,
        WindowManipulationService? windowManipulation,
        WatcherSession session)
    {
        ActivityWatcher = activityWatcher ?? throw new ArgumentNullException(nameof(activityWatcher));
        DiscordRpc = discordRpc;
        WindowManipulation = windowManipulation;
        Session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public ActivityWatcher ActivityWatcher { get; }
    public DiscordRpcService? DiscordRpc { get; }
    public WindowManipulationService? WindowManipulation { get; }
    public WatcherSession Session { get; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { Session.Dispose(); } catch { }
        try { WindowManipulation?.Dispose(); } catch { }
        try { DiscordRpc?.Dispose(); } catch { }
        try { ActivityWatcher.Dispose(); } catch { }
    }
}
