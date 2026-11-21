using System.Net;
using System.Text;
using System.Text.Json;
using BaBaBooeyStrap.Core.Interop;
using BaBaBooeyStrap.Tests.Mocks;
using BaBaBooeyStrap.Tests.Tier1_FeatureCoverage.Engines;
using FluentAssertions;
using Xunit;

namespace BaBaBooeyStrap.Tests.Tier3_CrossFeatureCombinations;

public sealed class ActivityWatcher_With_DiscordRpcTests : IClassFixture<MockRobloxEnvironment>
{
    private readonly MockRobloxEnvironment _env;

    public ActivityWatcher_With_DiscordRpcTests(MockRobloxEnvironment env)
    {
        _env = env;
    }

    [Fact]
    public async Task T3_ActivityWatcher_DiscordRpc_GameJoinLog_DispatchesPresenceWithPlaceDetails()
    {
        string pipeName = $"test-ipc-{Guid.NewGuid():N}";
        using var server = new MockDiscordIpcServer(pipeName);
        server.Start();

        using var client = new DiscordRpcClient(pipeName);
        bool connected = await client.ConnectAsync(TimeSpan.FromSeconds(3));
        connected.Should().BeTrue();

        using var logStream = _env.CreateActiveLogStream($"t3_join_{Guid.NewGuid():N}.log");
        using var watcher = new ActivityWatcher();

        var joinTcs = new TaskCompletionSource<GameJoinEventArgs>();
        watcher.GameJoined += async (s, e) =>
        {
            joinTcs.TrySetResult(e);
            await client.SetActivityAsync($"Place {e.PlaceId}", "In Game", e.PlaceId);
        };

        watcher.StartWatching(logStream.FilePath);

        logStream.WriteLine("2026-11-14T14:30:00.000Z,0.000,0,Info [FLog::Output] Log started");
        logStream.WriteLine("2026-11-14T14:30:01.000Z,1.000,0,Info [FLog::Output] ! Joining game 'job-join-999' place 1818 at 128.116.1.1");

        var completed = await Task.WhenAny(joinTcs.Task, Task.Delay(3000));
        completed.Should().Be(joinTcs.Task);

        bool received = await server.WaitForMessageAsync(
            msg => msg.Contains("SET_ACTIVITY") && msg.Contains("1818"),
            TimeSpan.FromSeconds(3));

        received.Should().BeTrue();
    }

    [Fact]
    public async Task T3_ActivityWatcher_DiscordRpc_GameLeaveLog_ClearsPresencePromptly()
    {
        string pipeName = $"test-ipc-{Guid.NewGuid():N}";
        using var server = new MockDiscordIpcServer(pipeName);
        server.Start();

        using var client = new DiscordRpcClient(pipeName);
        bool connected = await client.ConnectAsync(TimeSpan.FromSeconds(3));
        connected.Should().BeTrue();

        using var logStream = _env.CreateActiveLogStream($"t3_leave_{Guid.NewGuid():N}.log");
        using var watcher = new ActivityWatcher();

        var leaveTcs = new TaskCompletionSource<bool>();
        watcher.GameLeft += async (s, e) =>
        {
            leaveTcs.TrySetResult(true);
            await client.ClearPresenceAsync();
        };

        watcher.StartWatching(logStream.FilePath);

        logStream.WriteLine("2026-11-14T14:30:00.000Z,0.000,0,Info [FLog::Output] In game session");
        logStream.WriteLine("2026-11-14T14:30:02.000Z,2.000,0,Info [FLog::Output] Game disconnected");

        var completed = await Task.WhenAny(leaveTcs.Task, Task.Delay(3000));
        completed.Should().Be(leaveTcs.Task);

        bool receivedClear = await server.WaitForMessageAsync(
            msg => msg.Contains("SET_ACTIVITY") && msg.Contains("\"activity\":null"),
            TimeSpan.FromSeconds(3));

        receivedClear.Should().BeTrue();
    }

    [Fact]
    public async Task T3_ActivityWatcher_DiscordRpc_BloxstrapRpcCommand_UpdatesPresenceCustomData()
    {
        string pipeName = $"test-ipc-{Guid.NewGuid():N}";
        using var server = new MockDiscordIpcServer(pipeName);
        server.Start();

        using var client = new DiscordRpcClient(pipeName);
        bool connected = await client.ConnectAsync(TimeSpan.FromSeconds(3));
        connected.Should().BeTrue();

        using var logStream = _env.CreateActiveLogStream($"t3_rpc_{Guid.NewGuid():N}.log");
        using var watcher = new ActivityWatcher();

        var rpcTcs = new TaskCompletionSource<bool>();
        watcher.RpcCommandReceived += async (s, json) =>
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                string details = doc.RootElement.GetProperty("details").GetString()!;
                string state = doc.RootElement.GetProperty("state").GetString()!;
                await client.SetActivityAsync(details, state);
                rpcTcs.TrySetResult(true);
            }
            catch { }
        };

        watcher.StartWatching(logStream.FilePath);

        logStream.WriteLine("[BloxstrapRPC] {\"command\":\"SetRichPresence\",\"details\":\"Speedrunning World 1\",\"state\":\"Level 5\"}");

        var completed = await Task.WhenAny(rpcTcs.Task, Task.Delay(3000));
        completed.Should().Be(rpcTcs.Task);

        bool received = await server.WaitForMessageAsync(
            msg => msg.Contains("Speedrunning World 1") && msg.Contains("Level 5"),
            TimeSpan.FromSeconds(3));

        received.Should().BeTrue();
    }

    [Fact]
    public async Task T3_ActivityWatcher_RoValra_ServerIpLocation_EnrichesPresenceWithCountryFlag()
    {
        var httpHandler = new MockHttpMessageHandler();
        httpHandler.SetupRoute("ipinfo.io/128.116.1.1/json", () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"country\":\"US\",\"city\":\"Ashburn\",\"region\":\"Virginia\"}", Encoding.UTF8, "application/json")
        });

        using var httpClient = httpHandler.ToHttpClient();

        string pipeName = $"test-ipc-{Guid.NewGuid():N}";
        using var server = new MockDiscordIpcServer(pipeName);
        server.Start();

        using var client = new DiscordRpcClient(pipeName);
        bool connected = await client.ConnectAsync(TimeSpan.FromSeconds(3));
        connected.Should().BeTrue();

        using var logStream = _env.CreateActiveLogStream($"t3_rovalra_{Guid.NewGuid():N}.log");
        using var watcher = new ActivityWatcher();

        var joinTcs = new TaskCompletionSource<bool>();
        watcher.GameJoined += async (s, e) =>
        {
            var geo = await RoValraService.GetGeoLocationAsync(e.ServerIp, httpClient);
            string badge = RoValraService.FormatLocationBadge(geo);
            await client.SetActivityAsync("Playing Experience", badge);
            joinTcs.TrySetResult(true);
        };

        watcher.StartWatching(logStream.FilePath);

        logStream.WriteLine("2026-11-14T14:30:00.000Z,0.000,0,Info [FLog::Output] ! Joining game 'job-geo-77' place 4433 at 128.116.1.1");

        var completed = await Task.WhenAny(joinTcs.Task, Task.Delay(3000));
        completed.Should().Be(joinTcs.Task);

        bool receivedBadge = await server.WaitForMessageAsync(
            msg => msg.Contains("SET_ACTIVITY") && msg.Contains("Ashburn"),
            TimeSpan.FromSeconds(3));

        if (!receivedBadge)
        {
            string allMsgs = string.Join(" | ", server.ReceivedMessages);
            receivedBadge.Should().BeTrue($"Received messages: [{allMsgs}]");
        }
    }

    [Fact]
    public void T3_ActivityWatcher_WindowHooks_GameJoin_AppliesCustomTitleAndIcon()
    {
        using var testWindow = new NativeTestWindow("InitialTitle", visible: true);
        using var logStream = _env.CreateActiveLogStream($"t3_window_{Guid.NewGuid():N}.log");
        using var watcher = new ActivityWatcher();
        using var joinSignal = new ManualResetEventSlim(false);

        GameJoinEventArgs? joinedArgs = null;
        watcher.GameJoined += (s, e) =>
        {
            joinedArgs = e;
            joinSignal.Set();
        };

        watcher.StartWatching(logStream.FilePath);
        logStream.WriteLine("2026-11-14T14:30:01.000Z,1.000,0,Info [FLog::Output] ! Joining game 'job-win-hook' place 998877 at 128.116.1.1");

        joinSignal.Wait(TimeSpan.FromSeconds(3)).Should().BeTrue();
        joinedArgs.Should().NotBeNull();

        string newTitle = $"BaBaBooeyStrap - Game {joinedArgs!.PlaceId}";
        bool titleApplied = WindowManipulator.ApplyTitle(testWindow.Handle, newTitle);
        bool iconApplied = WindowManipulator.ApplyIcon(testWindow.Handle, nint.Zero);
        bool fullscreenApplied = WindowManipulator.SetBorderlessFullscreen(testWindow.Handle, true, 0, 0, 1920, 1080);

        titleApplied.Should().BeTrue();
        iconApplied.Should().BeTrue();
        fullscreenApplied.Should().BeTrue();

        var sb = new StringBuilder(256);
        User32.GetWindowTextW(testWindow.Handle, sb, 256);
        sb.ToString().Should().Be("BaBaBooeyStrap - Game 998877");
    }
}
