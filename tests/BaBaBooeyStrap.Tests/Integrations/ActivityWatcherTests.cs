using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Integrations.Watcher;
using BaBaBooeyStrap.Tests.Mocks;

namespace BaBaBooeyStrap.Tests.Integrations;

public sealed class ActivityWatcherTests : IDisposable
{
    private readonly MockRobloxEnvironment _env = new();

    private static ActivityWatcherOptions FastTestOptions() => new()
    {
        PollInterval = TimeSpan.FromMilliseconds(15),
        RpcRateLimit = TimeSpan.Zero,
        DirectoryScanWindow = TimeSpan.FromSeconds(15)
    };

    [Fact]
    public async Task JoinSequence_FullRobloxLog_ParsesPlaceUniverseJobIpPortAndUdmux()
    {
        using var logWriter = _env.CreateActiveLogStream("full_join.log");
        using var watcher = new ActivityWatcher(options: FastTestOptions());

        var joinTcs = new TaskCompletionSource<GameJoinEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.GameJoined += (_, e) => joinTcs.TrySetResult(e);

        watcher.StartWatching(logWriter.FilePath);

        string jobId = "11111111-2222-3333-4444-555555555555";
        logWriter.WriteLine($"2026-11-18T08:00:01.000Z,1.000,0,Info [FLog::Output] ! Joining game '{jobId}' place 1818 at 10.1.2.3");
        logWriter.WriteLine("2026-11-18T08:00:02.000Z,2.000,0,Info [FLog::GameJoinLoadTime] Report game_join_loadtime: placeid:1818 universeid:999888 userid:12345 referral_page:Home");
        logWriter.WriteLine("2026-11-18T08:00:03.000Z,3.000,0,Info [FLog::Network] UDMUX Address = 128.116.1.5, Port = 53640 | RCC Server Address = 10.1.2.3, Port = 53640");

        // Should NOT fire before serverId:
        await Task.Delay(80);
        joinTcs.Task.IsCompleted.Should().BeFalse();

        logWriter.WriteLine("2026-11-18T08:00:04.000Z,4.000,0,Info [FLog::Network] serverId: 128.116.1.5|53640");

        var joined = await joinTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
        joined.PlaceId.Should().Be("1818");
        joined.UniverseId.Should().Be("999888");
        joined.JobId.Should().Be(jobId);
        joined.ServerIp.Should().Be("128.116.1.5");
        joined.ServerPort.Should().Be(53640);
        joined.IsUdmux.Should().BeTrue();
        watcher.InGame.Should().BeTrue();
    }

    [Fact]
    public async Task JoinSequence_NonUdmux_ExtractsServerPortFromServerId()
    {
        using var logWriter = _env.CreateActiveLogStream("non_udmux.log");
        using var watcher = new ActivityWatcher(options: FastTestOptions());

        var joinTcs = new TaskCompletionSource<GameJoinEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.GameJoined += (_, e) => joinTcs.TrySetResult(e);

        watcher.StartWatching(logWriter.FilePath);

        logWriter.WriteLine("! Joining game 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee' place 4924922222 at 128.116.50.10");
        logWriter.WriteLine("serverId: 128.116.50.10|61234");

        var joined = await joinTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
        joined.PlaceId.Should().Be("4924922222");
        joined.ServerIp.Should().Be("128.116.50.10");
        joined.ServerPort.Should().Be(61234);
        joined.IsUdmux.Should().BeFalse();
    }

    [Fact]
    public async Task GameLeave_DisconnectAndTeleportRejoin_UpdatesHistoryAndServerType()
    {
        using var logWriter = _env.CreateActiveLogStream("disconnect_history.log");
        using var watcher = new ActivityWatcher(options: FastTestOptions());

        var firstJoin = new TaskCompletionSource<GameJoinEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var leaveTcs = new TaskCompletionSource<GameLeaveEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondJoin = new TaskCompletionSource<GameJoinEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        int joinCount = 0;

        watcher.GameJoined += (_, e) =>
        {
            if (Interlocked.Increment(ref joinCount) == 1) firstJoin.TrySetResult(e);
            else secondJoin.TrySetResult(e);
        };
        watcher.GameLeft += (_, e) => leaveTcs.TrySetResult(e);

        watcher.StartWatching(logWriter.FilePath);

        logWriter.WriteLine("! Joining game '11111111-1111-1111-1111-111111111111' place 1001 at 128.116.10.1");
        logWriter.WriteLine("serverId: 128.116.10.1|50000");
        await firstJoin.Task.WaitAsync(TimeSpan.FromSeconds(3));

        logWriter.WriteLine("[FLog::Network] Time to disconnect replication data: 0.123");
        await leaveTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));

        watcher.InGame.Should().BeFalse();
        watcher.History.Should().ContainSingle();
        watcher.History[0].PlaceId.Should().Be("1001");
        watcher.History[0].TimeLeft.Should().NotBeNull();

        // Teleport into reserved server
        logWriter.WriteLine("[FLog::UgcExperienceController] UgcExperienceController: doTeleport: joinScriptUrl=https://assetgame.roblox.com/game/PlaceLauncher.ashx?JoinTypeId%22%3a6%2c");
        logWriter.WriteLine("! Joining game '22222222-2222-2222-2222-222222222222' place 2002 at 128.116.20.2");
        logWriter.WriteLine("serverId: 128.116.20.2|50001");

        var second = await secondJoin.Task.WaitAsync(TimeSpan.FromSeconds(3));
        second.PlaceId.Should().Be("2002");
        watcher.CurrentActivity.Should().NotBeNull();
        watcher.CurrentActivity!.IsTeleport.Should().BeTrue();
        watcher.CurrentActivity!.ServerType.Should().Be(ServerType.Reserved);
    }

    [Fact]
    public async Task BloxstrapRpc_ValidMalformedAndRateLimited_FiltersInvalidPayloads()
    {
        using var logWriter = _env.CreateActiveLogStream("rpc_test.log");
        using var watcher = new ActivityWatcher(options: FastTestOptions());

        var rpcTcs = new TaskCompletionSource<BloxstrapRpcEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new List<BloxstrapRpcEventArgs>();
        watcher.BloxstrapRpcReceived += (_, e) =>
        {
            received.Add(e);
            if (e.Command == "SetRichPresence") rpcTcs.TrySetResult(e);
        };

        watcher.StartWatching(logWriter.FilePath);

        // Malformed JSON
        logWriter.WriteLine("[FLog::Output] [BloxstrapRPC] {bad json");
        // Empty command
        logWriter.WriteLine("[FLog::Output] [BloxstrapRPC] {\"command\":\"\"}");
        // Oversized SetLaunchData (>200 chars)
        logWriter.WriteLine($"[FLog::Output] [BloxstrapRPC] {{\"command\":\"SetLaunchData\",\"data\":\"{new string('x', 201)}\"}}");
        // Valid RPC
        logWriter.WriteLine("[FLog::Output] [BloxstrapRPC] {\"command\":\"SetRichPresence\",\"data\":{\"details\":\"Farming\",\"state\":\"Wave 10\"}}");

        var rpc = await rpcTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
        rpc.Command.Should().Be("SetRichPresence");
        rpc.RawJson.Should().Contain("Farming");
        received.Should().ContainSingle();
    }

    [Fact]
    public async Task StreamingBoundary_PartialLineEofAndFileTruncation_ReassemblesCleanly()
    {
        var logWriter = _env.CreateActiveLogStream("partial_and_trunc.log");
        string logPath = logWriter.FilePath;
        using var watcher = new ActivityWatcher(options: FastTestOptions());

        var joins = new List<GameJoinEventArgs>();
        var firstJoin = new TaskCompletionSource<GameJoinEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.GameJoined += (_, e) =>
        {
            joins.Add(e);
            firstJoin.TrySetResult(e);
        };

        watcher.StartWatching(logPath);

        // Write partial line without newline
        logWriter.Write("! Joining game '33333333-3333-3333-3333-333333333333' ");
        await Task.Delay(80);
        joins.Should().BeEmpty();

        // Complete the line + serverId
        logWriter.Write("place 777 at 128.116.77.7\n");
        logWriter.WriteLine("serverId: 128.116.77.7|51000");

        var joined = await firstJoin.Task.WaitAsync(TimeSpan.FromSeconds(3));
        joined.PlaceId.Should().Be("777");
        joined.ServerPort.Should().Be(51000);

        // Simulate disconnect + file truncation recovery
        logWriter.WriteLine("Time to disconnect replication data: 0.1");
        await Task.Delay(60);

        // Release SimulatedLogWriter lock, then truncate live while watcher is actively watching
        logWriter.Dispose();
        var secondJoin = new TaskCompletionSource<GameJoinEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.GameJoined += (_, e) => secondJoin.TrySetResult(e);

        using (var truncStream = new FileStream(logPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            truncStream.SetLength(0);
            byte[] newBytes = Encoding.UTF8.GetBytes(
                "! Joining game '44444444-4444-4444-4444-444444444444' place 888 at 128.116.88.8\nserverId: 128.116.88.8|52000\n");
            truncStream.Write(newBytes);
            truncStream.Flush();
        }

        var rejoined = await secondJoin.Task.WaitAsync(TimeSpan.FromSeconds(3));
        rejoined.PlaceId.Should().Be("888");
    }

    [Fact]
    public async Task DirectoryWatching_And_WatcherSessionChildPidTracking_WorkDeterministically()
    {
        using var watcher = new ActivityWatcher(options: FastTestOptions());
        var joinTcs = new TaskCompletionSource<GameJoinEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.GameJoined += (_, e) => joinTcs.TrySetResult(e);

        // Watch directory before log file exists
        watcher.StartWatching(_env.RobloxLogsPath);
        await Task.Delay(60);

        using var logWriter = _env.CreateActiveLogStream("0.640.0_20261118T080000Z_Player_dir.log");
        logWriter.WriteLine("! Joining game '55555555-5555-5555-5555-555555555555' place 999 at 128.116.99.9");
        logWriter.WriteLine("serverId: 128.116.99.9|53000");

        var joined = await joinTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
        joined.PlaceId.Should().Be("999");

        // Verify WatcherSession closes AutoclosePids + discovered child PIDs on exit while guarding PID reuse
        var fakeMonitor = new FakeProcessLifecycleMonitor(
            mainPid: 4000,
            childPids: new[] { 4001, 4002 },
            autocloseStartTimes: new Dictionary<int, DateTime>
            {
                [5000] = new DateTime(2026, 11, 18, 8, 0, 0, DateTimeKind.Utc)
            });

        var watcherData = new WatcherData
        {
            ProcessId = 4000,
            LogFile = logWriter.FilePath,
            AutoclosePids = new List<int> { 5000 }
        };

        using var session = new WatcherSession(watcherData, watcher, fakeMonitor, gracefulCloseTimeout: TimeSpan.FromMilliseconds(10));
        await session.RunAsync();

        watcher.IsWatching.Should().BeFalse();
        fakeMonitor.TerminatedPids.Should().BeEquivalentTo(new[] { 5000, 4001, 4002 });

        // Verify real ProcessLifecycleMonitor Toolhelp32 query doesn't crash on current process
        var realMonitor = new ProcessLifecycleMonitor();
        Action queryAct = () => realMonitor.GetChildProcessIds(Environment.ProcessId);
        queryAct.Should().NotThrow();
    }

    [Fact]
    public async Task ConcurrentDirectoryFileCreationDuringStreaming_DoesNotThrowOrCorruptCarryBuffer()
    {
        using var watcher = new ActivityWatcher(options: new ActivityWatcherOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(5),
            RpcRateLimit = TimeSpan.Zero,
            DirectoryScanWindow = TimeSpan.FromSeconds(15)
        });

        var observedLines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var finalJoinTcs = new TaskCompletionSource<GameJoinEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.LogLineRead += (_, line) => observedLines.Enqueue(line);
        watcher.GameJoined += (_, e) =>
        {
            if (e.PlaceId == "999004")
            {
                finalJoinTcs.TrySetResult(e);
            }
        };

        watcher.StartWatching(_env.RobloxLogsPath);

        var writers = new List<SimulatedLogWriter>();
        try
        {
            for (int i = 1; i <= 3; i++)
            {
                var writer = _env.CreateActiveLogStream($"0.640.0_20261118T08000{i}Z_Player_concurrent_{i}.log");
                writers.Add(writer);

                // Write a complete line plus an incomplete trailing line (kept in _carryBuffer)
                writer.WriteLine($"2026-11-18T08:00:0{i}.000Z,1.0,0,Info [FLog::Output] Stream_{i}_Complete");
                writer.Write($"CARRY_TAIL_{i}_");
                await Task.Delay(30);
            }

            // Create final Player log while previous files have partial lines in _carryBuffer
            var finalWriter = _env.CreateActiveLogStream("0.640.0_20261118T080004Z_Player_concurrent_4.log");
            writers.Add(finalWriter);

            // Split the join line across a small delay to exercise carry-buffer reassembly on the new file
            finalWriter.Write("! Joining game '00000004-0000-0000-0000-000000000000' ");
            await Task.Delay(25);
            finalWriter.Write("place 999004 at 128.116.10.4\n");
            finalWriter.WriteLine("serverId: 128.116.10.4|53004");

            var joined = await finalJoinTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            joined.PlaceId.Should().Be("999004");
            observedLines.Should().NotBeEmpty();
            observedLines.Should().NotContain(l => l.Contains("CARRY_TAIL_"));
            observedLines.Should().AllSatisfy(l => l.Should().NotContain("\0"));
        }
        finally
        {
            foreach (var w in writers)
            {
                w.Dispose();
            }
        }
    }

    [Fact]
    public async Task StopWatching_WhileActivelyReadingAndProcessing_ExitsCleanlyWithoutDeadlock()
    {
        using var logWriter = _env.CreateActiveLogStream("stop_deadlock_check.log");
        using var watcher = new ActivityWatcher(options: new ActivityWatcherOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(5),
            RpcRateLimit = TimeSpan.Zero
        });

        var readStarted = new ManualResetEventSlim(false);
        watcher.LogLineRead += (_, _) =>
        {
            // Access lock-protected properties during callback while StopWatching may be invoked
            _ = watcher.IsWatching;
            _ = watcher.InGame;
            readStarted.Set();
        };

        watcher.StartWatching(logWriter.FilePath);

        using var writerCts = new CancellationTokenSource();
        var writerTask = Task.Run(async () =>
        {
            int idx = 0;
            while (!writerCts.IsCancellationRequested)
            {
                logWriter.WriteLine($"2026-11-18T08:00:01.000Z,1.000,0,Info [FLog::Output] Heartbeat {idx++}");
                await Task.Delay(5, writerCts.Token);
            }
        });

        readStarted.Wait(TimeSpan.FromSeconds(2)).Should().BeTrue();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        watcher.StopWatching();
        sw.Stop();

        writerCts.Cancel();
        try { await writerTask; } catch (OperationCanceledException) { }

        watcher.IsWatching.Should().BeFalse();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1.5));
    }

    [Fact]
    public async Task StopWatching_CalledInsideLogLineReadCallback_CompletesWithoutSelfWaitTimeout()
    {
        using var logWriter = _env.CreateActiveLogStream("callback_stop_logline.log");
        using var watcher = new ActivityWatcher(options: FastTestOptions());

        var callbackDone = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.LogLineRead += (_, _) =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            watcher.StopWatching();
            sw.Stop();
            callbackDone.TrySetResult(sw.Elapsed);
        };

        watcher.StartWatching(logWriter.FilePath);
        logWriter.WriteLine("2026-11-18T08:00:01.000Z,1.000,0,Info [FLog::Output] CallbackSelfStop");

        TimeSpan stopElapsed = await callbackDone.Task.WaitAsync(TimeSpan.FromSeconds(3));
        stopElapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(500));
        watcher.IsWatching.Should().BeFalse();
    }

    [Fact]
    public async Task StopWatching_CalledInsideGameJoinedCallback_CompletesWithoutSelfWaitTimeout()
    {
        using var logWriter = _env.CreateActiveLogStream("callback_stop_gamejoin.log");
        using var watcher = new ActivityWatcher(options: FastTestOptions());

        var callbackDone = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.GameJoined += (_, _) =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            watcher.StopWatching();
            sw.Stop();
            callbackDone.TrySetResult(sw.Elapsed);
        };

        watcher.StartWatching(logWriter.FilePath);
        logWriter.WriteLine("! Joining game '11111111-2222-3333-4444-555555555555' place 1818 at 128.116.10.1");
        logWriter.WriteLine("serverId: 128.116.10.1|53640");

        TimeSpan stopElapsed = await callbackDone.Task.WaitAsync(TimeSpan.FromSeconds(3));
        stopElapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(500));
        watcher.IsWatching.Should().BeFalse();
    }

    public void Dispose()
    {
        _env.Dispose();
    }

    private sealed class FakeProcessLifecycleMonitor : IProcessLifecycleMonitor
    {
        private readonly int _mainPid;
        private readonly IReadOnlyList<int> _childPids;
        private readonly Dictionary<int, DateTime> _startTimes;

        public List<int> TerminatedPids { get; } = new();

        public FakeProcessLifecycleMonitor(int mainPid, IReadOnlyList<int> childPids, Dictionary<int, DateTime> autocloseStartTimes)
        {
            _mainPid = mainPid;
            _childPids = childPids;
            _startTimes = autocloseStartTimes;
        }

        public IReadOnlyList<int> GetChildProcessIds(int parentPid) =>
            parentPid == _mainPid ? _childPids : Array.Empty<int>();

        public bool TryGetProcessStartTimeUtc(int pid, out DateTime startTimeUtc) =>
            _startTimes.TryGetValue(pid, out startTimeUtc);

        public bool IsProcessRunning(int pid, DateTime? expectedStartTimeUtc = null) => false;

        public bool TerminateProcessSafely(int pid, DateTime? expectedStartTimeUtc, TimeSpan gracefulTimeout)
        {
            TerminatedPids.Add(pid);
            return true;
        }

        public Task WaitForProcessExitAsync(int pid, DateTime? expectedStartTimeUtc, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
