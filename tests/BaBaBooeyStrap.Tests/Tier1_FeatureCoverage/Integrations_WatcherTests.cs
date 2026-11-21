using BaBaBooeyStrap.Tests.Mocks;
using BaBaBooeyStrap.Tests.Tier1_FeatureCoverage.Engines;

namespace BaBaBooeyStrap.Tests.Tier1_FeatureCoverage;

public sealed class Integrations_WatcherTests : IClassFixture<MockRobloxEnvironment>
{
    private readonly MockRobloxEnvironment _env;

    public Integrations_WatcherTests(MockRobloxEnvironment env)
    {
        _env = env;
    }

    [Fact]
    public async Task F16_Watcher_ParseGameJoin_ExtractsPlaceIdJobIdAndServerIp()
    {
        // Arrange
        using var streamWriter = _env.CreateActiveLogStream("test_join.log");
        using var watcher = new ActivityWatcher();

        GameJoinEventArgs? receivedArgs = null;
        var tcs = new TaskCompletionSource<GameJoinEventArgs>();
        watcher.GameJoined += (s, e) =>
        {
            receivedArgs = e;
            tcs.TrySetResult(e);
        };

        watcher.StartWatching(streamWriter.FilePath);

        // Act
        streamWriter.WriteLine("Log header line");
        streamWriter.WriteLine("! Joining game 'job-9876-abcd' place 1818 at 128.0.0.1");

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(2000));

        // Assert
        completed.Should().Be(tcs.Task);
        receivedArgs.Should().NotBeNull();
        receivedArgs!.PlaceId.Should().Be(1818);
        receivedArgs.JobId.Should().Be("job-9876-abcd");
        receivedArgs.ServerIp.Should().Be("128.0.0.1");
    }

    [Fact]
    public async Task F16_Watcher_ParseUdmuxAddress_ExtractsUdmuxIpAndPort()
    {
        // Arrange
        using var streamWriter = _env.CreateActiveLogStream("test_udmux.log");
        using var watcher = new ActivityWatcher();

        var tcs = new TaskCompletionSource<GameJoinEventArgs>();
        watcher.GameJoined += (s, e) => tcs.TrySetResult(e);

        watcher.StartWatching(streamWriter.FilePath);

        // Act: Write UDMUX line before join line
        streamWriter.WriteLine("UDMUX Address = 10.0.0.5, Port = 53640 | RCC Server Address = 10.0.0.5, Port = 53640");
        streamWriter.WriteLine("! Joining game 'job-udmux-test' place 2020 at 10.0.0.5");

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(2000));

        // Assert
        completed.Should().Be(tcs.Task);
        var res = await tcs.Task;
        res.UdmuxIp.Should().Be("10.0.0.5");
        res.UdmuxPort.Should().Be(53640);
    }

    [Fact]
    public async Task F16_Watcher_BloxstrapRpcCommand_ParsesRpcPayload()
    {
        // Arrange
        using var streamWriter = _env.CreateActiveLogStream("test_rpc.log");
        using var watcher = new ActivityWatcher();

        var tcs = new TaskCompletionSource<string>();
        watcher.RpcCommandReceived += (s, json) => tcs.TrySetResult(json);

        watcher.StartWatching(streamWriter.FilePath);

        // Act
        streamWriter.WriteLine("[BloxstrapRPC] {\"command\":\"SetDetails\",\"data\":{\"details\":\"In Lobby\"}}");

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(2000));

        // Assert
        completed.Should().Be(tcs.Task);
        string payload = await tcs.Task;
        payload.Should().Contain("\"command\":\"SetDetails\"");
        payload.Should().Contain("\"details\":\"In Lobby\"");
    }

    [Fact]
    public async Task F16_Watcher_NonBlockingStreaming_ReadsLinesAppendedInRealTime()
    {
        // Arrange
        using var streamWriter = _env.CreateActiveLogStream("test_realtime.log");
        using var watcher = new ActivityWatcher();

        int joinEventsCount = 0;
        watcher.GameJoined += (s, e) => Interlocked.Increment(ref joinEventsCount);

        watcher.StartWatching(streamWriter.FilePath);

        // Act: Stream 3 distinct join events sequentially
        for (int i = 0; i < 3; i++)
        {
            streamWriter.WriteLine($"! Joining game 'job-{i}' place {1000 + i} at 192.168.1.{i}");
            await Task.Delay(50);
        }

        await Task.Delay(300);

        // Assert
        joinEventsCount.Should().Be(3);
    }

    [Fact]
    public void F16_Watcher_StopWatching_CleansUpFileWatcherAndBackgroundTasks()
    {
        // Arrange
        using var streamWriter = _env.CreateActiveLogStream("test_stop.log");
        var watcher = new ActivityWatcher();
        watcher.StartWatching(streamWriter.FilePath);

        // Act
        watcher.StopWatching();

        // Write line after stopping
        int joinEvents = 0;
        watcher.GameJoined += (s, e) => joinEvents++;
        streamWriter.WriteLine("! Joining game 'job-after-stop' place 9999 at 1.1.1.1");
        Thread.Sleep(100);

        // Assert
        joinEvents.Should().Be(0);
        watcher.Dispose();
    }
}
