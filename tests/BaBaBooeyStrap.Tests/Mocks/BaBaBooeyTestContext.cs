using BaBaBooeyStrap.Bootstrapper.FastFlags;
using BaBaBooeyStrap.Core;
using BaBaBooeyStrap.Core.Storage;
using BaBaBooeyStrap.MultiInstance;
using BaBaBooeyStrap.MultiInstance.Abstractions;
using BaBaBooeyStrap.Tests.Tier1_FeatureCoverage.Engines;

namespace BaBaBooeyStrap.Tests.Mocks;

public sealed class BaBaBooeyTestContext : IDisposable
{
    public MockRobloxEnvironment Environment { get; }
    public ParameterizedMutexTestHelper MutexHelper { get; }
    public MockHttpMessageHandler HttpHandler { get; }
    public HttpClient HttpClient { get; }
    public MockDiscordIpcServer DiscordServer { get; }
    public DiscordRpcClient RpcClient { get; }
    public PathProvider Paths { get; }
    public string DiscordPipeName { get; }

    private readonly List<IDisposable> _disposables = new();

    public BaBaBooeyTestContext(string prefix = "T34")
    {
        // 1. Filesystem Sandbox
        Environment = new MockRobloxEnvironment();
        Paths = new PathProvider(Environment.BaBaBooeyStrapPath);

        // 2. Kernel Mutex & Event Isolation
        MutexHelper = new ParameterizedMutexTestHelper(prefix);

        // 3. HTTP Mock Handler & Client
        HttpHandler = new MockHttpMessageHandler();
        HttpClient = HttpHandler.ToHttpClient();

        // 4. Isolated Named Pipe IPC Server & Client
        DiscordPipeName = $"test-ipc-{Guid.NewGuid():N}";
        DiscordServer = new MockDiscordIpcServer(DiscordPipeName);
        RpcClient = new DiscordRpcClient(DiscordPipeName);
    }

    public WindowsMultiInstanceController CreateIsolatedController(Func<IReadOnlyList<int>>? processEnumerator = null)
    {
        var controller = new WindowsMultiInstanceController(MutexHelper.MutexName, MutexHelper.EventName, processEnumerator);
        _disposables.Add(controller);
        return controller;
    }

    public ProcessSupervisor CreateMockSupervisor(Func<IReadOnlyList<ClientWindowInfo>>? windowProbe = null)
    {
        return new ProcessSupervisor(windowProbe);
    }

    public DpapiAccountStore CreateIsolatedAccountStore()
    {
        return new DpapiAccountStore(Path.Combine(Environment.BaBaBooeyStrapPath, "accounts.dat"));
    }

    public JsonManager<T> CreateIsolatedJsonManager<T>() where T : class, new()
    {
        string path = Path.Combine(Environment.BaBaBooeyStrapPath, $"{typeof(T).Name}.json");
        return new JsonManager<T>(path);
    }

    public FastFlagManager CreateIsolatedFastFlagManager(string? versionGuid = null)
    {
        string path = !string.IsNullOrEmpty(versionGuid)
            ? Path.Combine(Environment.RobloxVersionsPath, versionGuid, "ClientSettings", "ClientAppSettings.json")
            : Path.Combine(Environment.ModificationsPath, "ClientSettings", "ClientAppSettings.json");
        return new FastFlagManager(path);
    }

    public void RegisterForCleanup(IDisposable disposable)
    {
        if (disposable != null) _disposables.Add(disposable);
    }

    public void Dispose()
    {
        // Strict Teardown Order:
        // 1. Dispose registered active watchers/controllers/supervisors
        foreach (var item in _disposables)
        {
            try { item.Dispose(); } catch { }
        }
        _disposables.Clear();

        // 2. Discord RPC client and server
        try { RpcClient.Dispose(); } catch { }
        try { DiscordServer.Dispose(); } catch { }

        // 3. HTTP client and handler
        try { HttpClient.Dispose(); } catch { }

        // 4. Kernel mutexes and worker threads
        try { MutexHelper.Dispose(); } catch { }

        // 5. Filesystem sandbox (all handles are now released)
        try { Environment.Dispose(); } catch { }
    }
}
