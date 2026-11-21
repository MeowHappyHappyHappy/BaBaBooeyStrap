using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using BaBaBooeyStrap.Core.Models.Enums;
using BaBaBooeyStrap.Integrations.Discord;
using BaBaBooeyStrap.Tests.Mocks;

namespace BaBaBooeyStrap.Tests.Integrations;

public sealed class DiscordRpcServiceTests
{
    [Fact]
    public async Task ConnectAsync_ScansPipeSlotsAndCompletesOpcode0Handshake()
    {
        string prefix = $"test-scan-{Guid.NewGuid():N}";
        using var server = new MockDiscordIpcServer($"{prefix}-3");
        server.Start();

        using var service = new DiscordRpcService(new DiscordRpcOptions
        {
            ClientId = "1005469189907173486",
            PipeNamePrefix = prefix,
            PipeScanStart = 0,
            PipeScanEnd = 5,
            PerPipeConnectTimeout = TimeSpan.FromMilliseconds(40),
            AutoReconnect = false
        });

        bool connected = await service.ConnectAsync();

        connected.Should().BeTrue();
        service.IsConnected.Should().BeTrue();

        bool gotHandshake = await server.WaitForFrameAsync(
            f => f.Opcode == 0 && f.Json.Contains("1005469189907173486"),
            TimeSpan.FromSeconds(2));
        gotHandshake.Should().BeTrue();
    }

    [Fact]
    public async Task Opcodes_PingPongAndDisconnectClose_HandledPerWireProtocol()
    {
        string pipeName = $"test-opcodes-{Guid.NewGuid():N}";
        using var server = new MockDiscordIpcServer(pipeName);
        server.Start();

        using var service = new DiscordRpcService(new DiscordRpcOptions
        {
            PipeNameOverride = pipeName,
            AutoReconnect = false
        });

        (await service.ConnectAsync()).Should().BeTrue();

        // Client sends PING (3), server replies PONG (4)
        await service.SendPingAsync();
        bool pingRecorded = await server.WaitForFrameAsync(f => f.Opcode == 3, TimeSpan.FromSeconds(2));
        pingRecorded.Should().BeTrue();

        // Server sends PING (3), client replies PONG (4)
        await server.SendPingAsync("{\"nonce\":\"hb1\"}");
        bool pongRecorded = await server.WaitForFrameAsync(
            f => f.Opcode == 4 && f.Json.Contains("hb1"),
            TimeSpan.FromSeconds(2));
        pongRecorded.Should().BeTrue();

        // Disconnect sends CLOSE (2)
        service.Disconnect();
        service.IsConnected.Should().BeFalse();
        bool closeRecorded = await server.WaitForFrameAsync(f => f.Opcode == 2, TimeSpan.FromSeconds(2));
        closeRecorded.Should().BeTrue();
    }

    [Fact]
    public async Task UpdatePresenceAsync_FormatsPayloadLimitsPaddingAndUnescapedDeepLink()
    {
        string pipeName = $"test-payload-{Guid.NewGuid():N}";
        using var server = new MockDiscordIpcServer(pipeName);
        server.Start();

        using var service = new DiscordRpcService(new DiscordRpcOptions
        {
            PipeNameOverride = pipeName,
            UpdateThrottle = TimeSpan.Zero,
            HideButtons = false,
            StatusDisplayType = DiscordRPCStatusDisplay.Details,
            AutoReconnect = false
        });

        (await service.ConnectAsync()).Should().BeTrue();

        // 1) Test 1-char padding with \u2800\u2800 and unescaped '&' in deep link
        string deepLink = DiscordRpcService.BuildRobloxDeepLink(
            "189707",
            jobId: "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            launchData: "mode=hard");

        deepLink.Should().Be("roblox://experiences/start?placeId=189707&gameInstanceId=aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee&launchData=mode%3Dhard");

        await service.UpdatePresenceAsync(new DiscordActivity(
            Details: "X",
            State: new string('B', 200),
            LargeImageKey: "https://tr.rbxcdn.com/icon.png",
            LargeImageText: "Natural Disaster Survival",
            SmallImageKey: "roblox",
            SmallImageText: "Roblox",
            StartTimestamps: 1731918900,
            JoinSecret: "ignored-when-buttons-present",
            DeepLinkUrl: deepLink));

        bool received = await server.WaitForFrameAsync(
            f => f.Opcode == 1 && f.Json.Contains("SET_ACTIVITY"),
            TimeSpan.FromSeconds(2));
        received.Should().BeTrue();

        string rawJson = server.ReceivedFrames.Last(f => f.Opcode == 1).Json;
        rawJson.Should().Contain("X\u2800\u2800");
        rawJson.Should().Contain("roblox://experiences/start?placeId=189707&gameInstanceId=");
        rawJson.Should().NotContain("\\u0026");

        using var doc = JsonDocument.Parse(rawJson);
        var activityElem = doc.RootElement.GetProperty("args").GetProperty("activity");
        activityElem.GetProperty("state").GetString()!.Length.Should().Be(128);
        activityElem.GetProperty("status_display_type").GetInt32().Should().Be(2);
        activityElem.TryGetProperty("secrets", out _).Should().BeFalse();

        // 2) ClearPresenceAsync sends activity: null
        await service.ClearPresenceAsync();
        bool cleared = await server.WaitForMessageAsync(m => m.Contains("\"activity\":null"), TimeSpan.FromSeconds(2));
        cleared.Should().BeTrue();
    }

    [Fact]
    public async Task AutoReconnect_OnUnexpectedDisconnect_UsesExponentialBackoffAndRestoresPresence()
    {
        string pipeName = $"test-reconnect-{Guid.NewGuid():N}";
        using var server = new MockDiscordIpcServer(pipeName);
        server.Start();

        using var service = new DiscordRpcService(new DiscordRpcOptions
        {
            PipeNameOverride = pipeName,
            UpdateThrottle = TimeSpan.Zero,
            AutoReconnect = true,
            InitialReconnectDelay = TimeSpan.FromMilliseconds(40),
            MaxReconnectDelay = TimeSpan.FromMilliseconds(400)
        });

        (await service.ConnectAsync()).Should().BeTrue();

        await service.UpdatePresenceAsync(new DiscordActivity(
            "Playing Obby",
            "Stage 42",
            null, null, null, null, null, null, null));

        (await server.WaitForMessageAsync(m => m.Contains("Playing Obby"), TimeSpan.FromSeconds(2))).Should().BeTrue();
        server.ClearMessages();

        // Drop pipe mid-session
        server.SimulateDisconnect();

        // Should reconnect automatically and restore cached activity
        bool reconnected = await server.WaitForConnectionCountAsync(2, TimeSpan.FromSeconds(5));
        reconnected.Should().BeTrue();

        bool restored = await server.WaitForMessageAsync(m => m.Contains("Playing Obby"), TimeSpan.FromSeconds(3));
        restored.Should().BeTrue();
        service.IsConnected.Should().BeTrue();
        service.ReconnectDelaysObserved.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ReadFrameAsync_RejectsOversizedOrNegativeFrameLength()
    {
        using var ms = new MemoryStream();
        byte[] badHeader = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(badHeader.AsSpan(0, 4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(badHeader.AsSpan(4, 4), 999_999);
        ms.Write(badHeader);
        ms.Position = 0;

        Func<Task> act = () => DiscordRpcService.ReadFrameAsync(ms, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidDataException>();

        using var msNeg = new MemoryStream();
        BinaryPrimitives.WriteInt32LittleEndian(badHeader.AsSpan(4, 4), -500);
        msNeg.Write(badHeader);
        msNeg.Position = 0;

        Func<Task> actNeg = () => DiscordRpcService.ReadFrameAsync(msNeg, CancellationToken.None);
        await actNeg.Should().ThrowAsync<InvalidDataException>();
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"evt\":123}")]
    [InlineData("{\"cmd\":\"DISPATCH\"}")]
    public async Task ConnectAsync_MalformedOrNonReadyHandshake_ReturnsFalseWithoutThrowingAndFailsOverToNextSlot(string badHandshakeJson)
    {
        string prefix = $"test-malformed-{Guid.NewGuid():N}";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Slot 0 returns malformed / non-READY JSON in response to Opcode 0
        Task slot0Server = Task.Run(async () =>
        {
            using var pipe = new NamedPipeServerStream($"{prefix}-0", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await pipe.WaitForConnectionAsync(cts.Token);
            byte[] header = new byte[8];
            await pipe.ReadExactlyAsync(header, 0, 8, cts.Token);
            int len = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
            if (len > 0)
            {
                byte[] buf = new byte[len];
                await pipe.ReadExactlyAsync(buf, 0, len, cts.Token);
            }

            byte[] payload = Encoding.UTF8.GetBytes(badHandshakeJson);
            byte[] resp = new byte[8 + payload.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(resp.AsSpan(0, 4), 1);
            BinaryPrimitives.WriteInt32LittleEndian(resp.AsSpan(4, 4), payload.Length);
            payload.CopyTo(resp, 8);
            await pipe.WriteAsync(resp, cts.Token);
            await pipe.FlushAsync(cts.Token);
        }, cts.Token);

        // First verify single-slot connection returns false cleanly without throwing InvalidOperationException
        using (var singleSlotService = new DiscordRpcService(new DiscordRpcOptions
        {
            PipeNameOverride = $"{prefix}-0",
            DirectPipeConnectTimeout = TimeSpan.FromMilliseconds(500),
            AutoReconnect = false
        }))
        {
            bool ok = await singleSlotService.ConnectAsync(cts.Token);
            ok.Should().BeFalse();
            singleSlotService.IsConnected.Should().BeFalse();
        }
        await slot0Server;

        // Now verify multi-slot scan fails over from bad slot 0 to valid slot 1
        string failoverPrefix = $"test-failover-{Guid.NewGuid():N}";
        Task slot0Bad = Task.Run(async () =>
        {
            using var pipe = new NamedPipeServerStream($"{failoverPrefix}-0", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await pipe.WaitForConnectionAsync(cts.Token);
            byte[] header = new byte[8];
            await pipe.ReadExactlyAsync(header, 0, 8, cts.Token);
            int len = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
            if (len > 0)
            {
                byte[] buf = new byte[len];
                await pipe.ReadExactlyAsync(buf, 0, len, cts.Token);
            }

            byte[] payload = Encoding.UTF8.GetBytes(badHandshakeJson);
            byte[] resp = new byte[8 + payload.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(resp.AsSpan(0, 4), 1);
            BinaryPrimitives.WriteInt32LittleEndian(resp.AsSpan(4, 4), payload.Length);
            payload.CopyTo(resp, 8);
            await pipe.WriteAsync(resp, cts.Token);
            await pipe.FlushAsync(cts.Token);
        }, cts.Token);

        using var slot1Good = new MockDiscordIpcServer($"{failoverPrefix}-1");
        slot1Good.Start();

        using var scanService = new DiscordRpcService(new DiscordRpcOptions
        {
            PipeNamePrefix = failoverPrefix,
            PipeScanStart = 0,
            PipeScanEnd = 1,
            PerPipeConnectTimeout = TimeSpan.FromMilliseconds(500),
            AutoReconnect = false
        });

        bool failoverConnected = await scanService.ConnectAsync(cts.Token);
        failoverConnected.Should().BeTrue();
        scanService.IsConnected.Should().BeTrue();
        await slot0Bad;
    }

    [Fact]
    public void BuildSetActivityJson_PadsSingleCharStateAndHoverTexts_AndBoundsOversizedPayloads()
    {
        using var service = new DiscordRpcService(new DiscordRpcOptions
        {
            HideButtons = false,
            GamePageUrl = new string('g', 70_000)
        });

        string paddedJson = service.BuildSetActivityJson(new DiscordActivity(
            Details: "A",
            State: "B",
            LargeImageKey: "large",
            LargeImageText: "C",
            SmallImageKey: "small",
            SmallImageText: "D",
            StartTimestamps: null,
            JoinSecret: null,
            DeepLinkUrl: "roblox://experiences/start?placeId=1"));

        using var doc = JsonDocument.Parse(paddedJson);
        var act = doc.RootElement.GetProperty("args").GetProperty("activity");
        act.GetProperty("details").GetString().Should().Be("A\u2800\u2800");
        act.GetProperty("state").GetString().Should().Be("B\u2800\u2800");
        act.GetProperty("assets").GetProperty("large_text").GetString().Should().Be("C\u2800\u2800");
        act.GetProperty("assets").GetProperty("small_text").GetString().Should().Be("D\u2800\u2800");

        // Oversized fields must stay well under MaxFrameLength
        string boundedJson = service.BuildSetActivityJson(new DiscordActivity(
            Details: "Playing",
            State: "In Game",
            LargeImageKey: new string('k', 70_000),
            LargeImageText: "Hover",
            SmallImageKey: new string('s', 70_000),
            SmallImageText: "Small",
            StartTimestamps: null,
            JoinSecret: new string('j', 70_000),
            DeepLinkUrl: new string('u', 70_000)));

        Encoding.UTF8.GetByteCount(boundedJson).Should().BeLessOrEqualTo(DiscordRpcService.MaxFrameLength);
    }

    [Fact]
    public async Task AutoReconnect_RestoresPresenceEvenWhenUpdateThrottleExceedsReconnectDelay()
    {
        string pipeName = $"test-throttle-reconnect-{Guid.NewGuid():N}";
        using var server = new MockDiscordIpcServer(pipeName);
        server.Start();

        using var service = new DiscordRpcService(new DiscordRpcOptions
        {
            PipeNameOverride = pipeName,
            UpdateThrottle = TimeSpan.FromSeconds(10),
            AutoReconnect = true,
            InitialReconnectDelay = TimeSpan.FromMilliseconds(30),
            MaxReconnectDelay = TimeSpan.FromMilliseconds(100)
        });

        (await service.ConnectAsync()).Should().BeTrue();

        await service.UpdatePresenceAsync(new DiscordActivity(
            "Playing Ranked",
            "Round 3",
            null, null, null, null, null, null, null));

        (await server.WaitForMessageAsync(m => m.Contains("Playing Ranked"), TimeSpan.FromSeconds(2))).Should().BeTrue();
        server.ClearMessages();

        // Disconnect quickly while UpdateThrottle (10s) has NOT elapsed
        server.SimulateDisconnect();

        bool reconnected = await server.WaitForConnectionCountAsync(2, TimeSpan.FromSeconds(5));
        reconnected.Should().BeTrue();

        bool restored = await server.WaitForMessageAsync(m => m.Contains("Playing Ranked"), TimeSpan.FromSeconds(3));
        restored.Should().BeTrue();
    }

    [Fact]
    public async Task Disconnect_WhenPipePeerStopsReading_CompletesPromptlyWithoutHanging()
    {
        string pipeName = $"test-disconnect-nonreading-{Guid.NewGuid():N}";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        var serverHandshakeTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(cts.Token);

            // Read Opcode 0 handshake frame
            byte[] header = new byte[8];
            await server.ReadExactlyAsync(header, 0, 8, cts.Token);
            int len = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
            if (len > 0)
            {
                byte[] buf = new byte[len];
                await server.ReadExactlyAsync(buf, 0, len, cts.Token);
            }

            // Reply with Opcode 1 DISPATCH READY and then stop reading without closing the pipe
            byte[] payload = Encoding.UTF8.GetBytes("{\"cmd\":\"DISPATCH\",\"evt\":\"READY\"}");
            byte[] resp = new byte[8 + payload.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(resp.AsSpan(0, 4), 1);
            BinaryPrimitives.WriteInt32LittleEndian(resp.AsSpan(4, 4), payload.Length);
            payload.CopyTo(resp, 8);
            await server.WriteAsync(resp, cts.Token);
            await server.FlushAsync(cts.Token);
        }, cts.Token);

        using var service = new DiscordRpcService(new DiscordRpcOptions
        {
            PipeNameOverride = pipeName,
            DirectPipeConnectTimeout = TimeSpan.FromSeconds(2),
            AutoReconnect = false
        });

        bool connected = await service.ConnectAsync(cts.Token);
        connected.Should().BeTrue();
        service.IsConnected.Should().BeTrue();
        await serverHandshakeTask;

        // Server handle stays open and never reads again; Disconnect() and Dispose() must complete promptly
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Task disconnectTask = Task.Run(() =>
        {
            service.Disconnect();
            service.Dispose();
        });

        bool completed = await Task.WhenAny(disconnectTask, Task.Delay(TimeSpan.FromSeconds(3))) == disconnectTask;
        sw.Stop();

        completed.Should().BeTrue("Disconnect/Dispose must not hang when the IPC peer stops reading");
        await disconnectTask;
        sw.ElapsedMilliseconds.Should().BeLessThan(1000);
        service.IsConnected.Should().BeFalse();
    }
}
