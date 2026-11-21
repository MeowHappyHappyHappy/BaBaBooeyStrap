using System.Net;
using System.Text;
using System.Text.Json;
using BaBaBooeyStrap.Core.Roblox;
using BaBaBooeyStrap.Tests.Mocks;
using BaBaBooeyStrap.Tests.Tier1_FeatureCoverage.Engines;
using FluentAssertions;
using Xunit;

namespace BaBaBooeyStrap.Tests.Tier4_RealWorldScenarios;

public sealed class Scenario4_ActivityTrackingDiscordShareLinkTests
{
    [Fact]
    public async Task Scenario4_VipShareLinkToDiscordRpc_ResolvesLinkAndStreamsJoinPresence()
    {
        // 1. Arrange Web API & Share Link
        var handler = new MockHttpMessageHandler()
            .SetupShareLinkWorkflow("share_code_vip", 1818, "priv-server-777");

        // IPInfo mock route
        handler.SetupRoute("ipinfo.io/128.116.119.5/json", () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"country\":\"US\",\"city\":\"San Jose\",\"region\":\"California\"}", Encoding.UTF8, "application/json")
        });

        using var httpClient = handler.ToHttpClient();
        using var authService = new RobloxAuthService(httpClient);

        // Resolve Share Link (F4)
        var shareResult = await authService.ResolveShareLinkAsync("cookie_unused", "share_code_vip");
        shareResult.PlaceId.Should().Be(1818);
        shareResult.LinkCode.Should().Be("priv-server-777");

        var builder = new PlaceLauncherUriBuilder
        {
            PlaceId = shareResult.PlaceId,
            LinkCode = shareResult.LinkCode
        };
        string launchUrl = builder.BuildPlaceLauncherUrl();
        launchUrl.Should().Contain("linkCode=priv-server-777");
        launchUrl.Should().Contain("placeId=1818");

        // 2. Start Discord IPC Mock Server (F17)
        string pipeName = $"test-ipc-{Guid.NewGuid():N}";
        using var discordServer = new MockDiscordIpcServer(pipeName);
        discordServer.Start();

        using var rpcClient = new DiscordRpcClient(pipeName);
        bool connected = await rpcClient.ConnectAsync(TimeSpan.FromSeconds(3));
        connected.Should().BeTrue();

        // 3. Start Live Log Streaming & Activity Watcher (F16)
        using var env = new MockRobloxEnvironment();
        using var logStream = env.CreateActiveLogStream("Player_live_session.log");
        using var watcher = new ActivityWatcher();

        GameJoinEventArgs? joinEvent = null;
        var joinTcs = new TaskCompletionSource<GameJoinEventArgs>();
        watcher.GameJoined += (s, e) =>
        {
            joinEvent = e;
            joinTcs.TrySetResult(e);
        };

        var leftTcs = new TaskCompletionSource<bool>();
        watcher.GameLeft += (s, e) => leftTcs.TrySetResult(true);

        watcher.StartWatching(logStream.FilePath);

        // 4. Stream Log Lines into Active Stream
        logStream.WriteLine("2026-11-14T14:30:00.000Z,0.000,0,Info [FLog::Output] Log initialized");
        logStream.WriteLine("2026-11-14T14:30:01.000Z,1.000,0,Info [FLog::Output] UDMUX Address = 128.116.119.5, Port = 53640");
        logStream.WriteLine("2026-11-14T14:30:02.000Z,2.000,0,Info [FLog::Output] ! Joining game 'job-session-789' place 1818 at 128.116.119.5");

        var completed = await Task.WhenAny(joinTcs.Task, Task.Delay(3000));
        completed.Should().Be(joinTcs.Task);
        joinEvent.Should().NotBeNull();
        joinEvent!.PlaceId.Should().Be(1818);
        joinEvent.ServerIp.Should().Be("128.116.119.5");

        // 5. Matchmaking Geo-Lookup (F20)
        var geo = await RoValraService.GetGeoLocationAsync(joinEvent.ServerIp, httpClient);
        string locationBadge = RoValraService.FormatLocationBadge(geo);
        locationBadge.Should().Contain("San Jose");

        // 6. Update Discord Rich Presence
        await rpcClient.SetActivityAsync("Playing Experience", $"{locationBadge} | Server job-session-789", placeId: 1818);

        bool receivedPresence = await discordServer.WaitForMessageAsync(
            msg => msg.Contains("SET_ACTIVITY") && msg.Contains("San Jose") && msg.Contains("1818"),
            TimeSpan.FromSeconds(3));
        receivedPresence.Should().BeTrue();

        // 7. Disconnect and Clear Presence
        logStream.WriteLine("2026-11-14T14:35:00.000Z,300.000,0,Info [FLog::Output] Leaving game");
        var leftCompleted = await Task.WhenAny(leftTcs.Task, Task.Delay(3000));
        leftCompleted.Should().Be(leftTcs.Task);

        await rpcClient.ClearPresenceAsync();

        bool receivedClear = await discordServer.WaitForMessageAsync(
            msg => msg.Contains("SET_ACTIVITY") && msg.Contains("\"activity\":null"),
            TimeSpan.FromSeconds(3));
        receivedClear.Should().BeTrue();
    }

    [Fact]
    public async Task Scenario4_BloxstrapRpcLogStreaming_ParsesCustomRpcCommandsAndUpdatesRichPresence()
    {
        string pipeName = $"test-ipc-{Guid.NewGuid():N}";
        using var discordServer = new MockDiscordIpcServer(pipeName);
        discordServer.Start();

        using var rpcClient = new DiscordRpcClient(pipeName);
        bool connected = await rpcClient.ConnectAsync(TimeSpan.FromSeconds(3));
        connected.Should().BeTrue();

        using var env = new MockRobloxEnvironment();
        using var logStream = env.CreateActiveLogStream("Player_bloxstrap_rpc.log");
        using var watcher = new ActivityWatcher();

        var rpcTcs = new TaskCompletionSource<string>();
        watcher.RpcCommandReceived += (s, cmdJson) => rpcTcs.TrySetResult(cmdJson);

        watcher.StartWatching(logStream.FilePath);

        // Stream custom BloxstrapRPC command from game
        logStream.WriteLine("[BloxstrapRPC] {\"command\":\"SetRichPresence\",\"details\":\"Speedrunning World 1\",\"state\":\"Level 5 Complete\"}");

        var completed = await Task.WhenAny(rpcTcs.Task, Task.Delay(3000));
        completed.Should().Be(rpcTcs.Task);

        string receivedCommand = await rpcTcs.Task;
        using var doc = JsonDocument.Parse(receivedCommand);
        string details = doc.RootElement.GetProperty("details").GetString()!;
        string state = doc.RootElement.GetProperty("state").GetString()!;

        await rpcClient.SetActivityAsync(details, state);

        bool receivedOnDiscord = await discordServer.WaitForMessageAsync(
            msg => msg.Contains("Speedrunning World 1") && msg.Contains("Level 5 Complete"),
            TimeSpan.FromSeconds(3));

        receivedOnDiscord.Should().BeTrue();
    }

    [Fact]
    public async Task Scenario4_RoValraGeoLocation_EnrichesPresenceWithCountryFlagAndCityBadge()
    {
        var handler = new MockHttpMessageHandler();

        // 4 global locations
        handler.SetupRoute("ipinfo.io/128.116.119.5/json", () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"country\":\"US\",\"city\":\"San Jose\",\"region\":\"California\"}", Encoding.UTF8, "application/json")
        });
        handler.SetupRoute("ipinfo.io/185.10.10.1/json", () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"country\":\"GB\",\"city\":\"London\",\"region\":\"England\"}", Encoding.UTF8, "application/json")
        });
        handler.SetupRoute("ipinfo.io/194.12.12.1/json", () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"country\":\"DE\",\"city\":\"Frankfurt\",\"region\":\"Hesse\"}", Encoding.UTF8, "application/json")
        });
        handler.SetupRoute("ipinfo.io/133.1.1.1/json", () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"country\":\"JP\",\"city\":\"Tokyo\",\"region\":\"Kanto\"}", Encoding.UTF8, "application/json")
        });

        using var httpClient = handler.ToHttpClient();

        var geoUS = await RoValraService.GetGeoLocationAsync("128.116.119.5", httpClient);
        var geoGB = await RoValraService.GetGeoLocationAsync("185.10.10.1", httpClient);
        var geoDE = await RoValraService.GetGeoLocationAsync("194.12.12.1", httpClient);
        var geoJP = await RoValraService.GetGeoLocationAsync("133.1.1.1", httpClient);

        string badgeUS = RoValraService.FormatLocationBadge(geoUS);
        string badgeGB = RoValraService.FormatLocationBadge(geoGB);
        string badgeDE = RoValraService.FormatLocationBadge(geoDE);
        string badgeJP = RoValraService.FormatLocationBadge(geoJP);

        badgeUS.Should().Contain("San Jose");
        badgeGB.Should().Contain("London");
        badgeDE.Should().Contain("Frankfurt");
        badgeJP.Should().Contain("Tokyo");
    }

    [Fact]
    public async Task Scenario4_GameDisconnectAndLeave_FiresGameLeftAndClearsDiscordPresenceSafely()
    {
        string pipeName = $"test-ipc-{Guid.NewGuid():N}";
        using var discordServer = new MockDiscordIpcServer(pipeName);
        discordServer.Start();

        using var rpcClient = new DiscordRpcClient(pipeName);
        bool connected = await rpcClient.ConnectAsync(TimeSpan.FromSeconds(3));
        connected.Should().BeTrue();

        using var env = new MockRobloxEnvironment();
        using var logStream = env.CreateActiveLogStream("Player_leave_test.log");
        using var watcher = new ActivityWatcher();

        var leftTcs = new TaskCompletionSource<bool>();
        watcher.GameLeft += (s, e) => leftTcs.TrySetResult(true);

        watcher.StartWatching(logStream.FilePath);

        await rpcClient.SetActivityAsync("Playing Game", "In Session");
        await discordServer.WaitForMessageAsync(msg => msg.Contains("Playing Game"), TimeSpan.FromSeconds(2));

        logStream.WriteLine("2026-11-14T14:30:00.000Z,0.000,0,Info [FLog::Output] Log initialized");
        logStream.WriteLine("2026-11-14T15:00:00.000Z,100.000,0,Info [FLog::Output] Leaving game");

        var completed = await Task.WhenAny(leftTcs.Task, Task.Delay(3000));
        completed.Should().Be(leftTcs.Task);

        await rpcClient.ClearPresenceAsync();

        bool receivedClear = await discordServer.WaitForMessageAsync(
            msg => msg.Contains("SET_ACTIVITY") && msg.Contains("\"activity\":null"),
            TimeSpan.FromSeconds(3));

        receivedClear.Should().BeTrue();
    }
}
