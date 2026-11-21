using System.IO.Pipes;
using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace BaBaBooeyStrap.Tests.Mocks;

public sealed class MockHarnessTests
{
    // =========================================================================
    // 1. MockHttpMessageHandler Tests
    // =========================================================================

    [Fact]
    public async Task MockHttpMessageHandler_AuthTicketWorkflow_HandlesCsrfChallengeAndTicketAcquisition()
    {
        // Arrange
        const string cookie = ".ROBLOSECURITY=_test_secret_cookie_value_";
        const string expectedCsrf = "csrf_token_secret_123";
        const string expectedTicket = "auth_ticket_roblox_456";

        var handler = new MockHttpMessageHandler()
            .SetupAuthTicketWorkflow(validCookie: cookie, csrfToken: expectedCsrf, authTicket: expectedTicket);
        using var client = handler.ToHttpClient();

        const string authEndpoint = "https://auth.roblox.com/v1/authentication-ticket";

        // Act 1: Call without cookie -> 401 Unauthorized
        using var unauthReq = new HttpRequestMessage(HttpMethod.Post, authEndpoint);
        using var unauthResp = await client.SendAsync(unauthReq);
        unauthResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Act 2: Call with cookie but missing CSRF token -> 403 Forbidden with x-csrf-token header
        using var csrfReq = new HttpRequestMessage(HttpMethod.Post, authEndpoint);
        csrfReq.Headers.Add("Cookie", cookie);
        using var csrfResp = await client.SendAsync(csrfReq);
        csrfResp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        csrfResp.Headers.TryGetValues("x-csrf-token", out var csrfValues).Should().BeTrue();
        string receivedCsrf = csrfValues!.First();
        receivedCsrf.Should().Be(expectedCsrf);

        // Act 3: Retry call with valid CSRF token -> 200 OK with rbx-authentication-ticket header
        using var ticketReq = new HttpRequestMessage(HttpMethod.Post, authEndpoint);
        ticketReq.Headers.Add("Cookie", cookie);
        ticketReq.Headers.Add("X-CSRF-TOKEN", receivedCsrf);
        using var ticketResp = await client.SendAsync(ticketReq);
        ticketResp.StatusCode.Should().Be(HttpStatusCode.OK);
        ticketResp.Headers.TryGetValues("rbx-authentication-ticket", out var ticketValues).Should().BeTrue();
        ticketValues!.First().Should().Be(expectedTicket);

        // Assert recorded calls
        handler.AssertCalled(HttpMethod.Post, "auth.roblox.com/v1/authentication-ticket", expectedTimes: 3);
    }

    [Fact]
    public async Task MockHttpMessageHandler_ShareLinkWorkflow_ReturnsPlaceIdAndLinkCode()
    {
        // Arrange
        const string linkId = "1234567890abcdef";
        const long expectedPlaceId = 1818;
        const string expectedLinkCode = "test_vip_code_xyz";

        var handler = new MockHttpMessageHandler()
            .SetupShareLinkWorkflow(linkId, expectedPlaceId, expectedLinkCode);
        using var client = handler.ToHttpClient();

        // Act
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://apis.roblox.com/sharelinks/v1/resolve-link");
        req.Content = new StringContent(JsonSerializer.Serialize(new { linkId }), Encoding.UTF8, "application/json");
        using var resp = await client.SendAsync(req);

        // Assert
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        string json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("statusCode").GetInt32().Should().Be(200);
        doc.RootElement.GetProperty("placeId").GetInt64().Should().Be(expectedPlaceId);
        doc.RootElement.GetProperty("linkCode").GetString().Should().Be(expectedLinkCode);
    }

    [Fact]
    public async Task MockHttpMessageHandler_CdnDeployment_ReturnsManifestAndPackageBytesWithMatchingMd5()
    {
        // Arrange
        const string cdnHost = "https://setup.rbxcdn.com";
        const string versionGuid = "version-test-guid-123";
        byte[] pkgBytes1 = Encoding.UTF8.GetBytes("RobloxAppExecutablePayload");
        byte[] pkgBytes2 = Encoding.UTF8.GetBytes("RobloxContentTexturesPayload");

        var packages = new Dictionary<string, byte[]>
        {
            ["RobloxApp.zip"] = pkgBytes1,
            ["content-textures.zip"] = pkgBytes2
        };

        var handler = new MockHttpMessageHandler()
            .SetupCdnDeployment(cdnHost, versionGuid, packages);
        using var client = handler.ToHttpClient();

        // Act 1: Get manifest
        string manifestUrl = $"{cdnHost}/{versionGuid}-rbxPkgManifest.txt";
        string manifest = await client.GetStringAsync(manifestUrl);
        string[] lines = manifest.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

        lines[0].Should().Be("v0");
        lines[1].Should().Be("RobloxApp.zip");
        string expectedMd5_1 = Convert.ToHexString(MD5.HashData(pkgBytes1)).ToLowerInvariant();
        lines[2].Should().Be(expectedMd5_1);

        lines[4].Should().Be("content-textures.zip");
        string expectedMd5_2 = Convert.ToHexString(MD5.HashData(pkgBytes2)).ToLowerInvariant();
        lines[5].Should().Be(expectedMd5_2);

        // Act 2: Download packages and verify hashes
        byte[] downloaded1 = await client.GetByteArrayAsync($"{cdnHost}/{versionGuid}-RobloxApp.zip");
        Convert.ToHexString(MD5.HashData(downloaded1)).ToLowerInvariant().Should().Be(expectedMd5_1);

        byte[] downloaded2 = await client.GetByteArrayAsync($"{cdnHost}/{versionGuid}-content-textures.zip");
        Convert.ToHexString(MD5.HashData(downloaded2)).ToLowerInvariant().Should().Be(expectedMd5_2);
    }

    // =========================================================================
    // 2. ParameterizedMutexTestHelper Tests
    // =========================================================================

    [Fact]
    public void ParameterizedMutexTestHelper_AcquiresAndReleasesLocalMutexSafely()
    {
        // Arrange
        using var helper = new ParameterizedMutexTestHelper("Unit_Acquire");
        helper.MutexName.Should().StartWith(@"Local\Unit_Acquire_ROBLOX_Mutex_");

        // Act 1: Mutex is initially not held
        helper.IsMutexHeld(TimeSpan.FromMilliseconds(50)).Should().BeFalse();

        // Act 2: Acquire mutex
        using (var holdScope = helper.TryAcquire(TimeSpan.FromSeconds(1)))
        {
            holdScope.Should().NotBeNull();
            holdScope!.IsHolding.Should().BeTrue();

            // Mutex is held on this thread
            // Another thread checking will see it held
            bool isHeldByExternal = false;
            var checkThread = new Thread(() =>
            {
                isHeldByExternal = helper.IsMutexHeld(TimeSpan.FromMilliseconds(50));
            });
            checkThread.Start();
            checkThread.Join();
            isHeldByExternal.Should().BeTrue();
        }

        // Act 3: Released after scope disposal
        helper.IsMutexHeld(TimeSpan.FromMilliseconds(50)).Should().BeFalse();
    }

    [Fact]
    public void ParameterizedMutexTestHelper_ExternalContention_BlocksSecondAcquirer()
    {
        // Arrange
        using var helper = new ParameterizedMutexTestHelper("Unit_Contention");

        // Act 1: External background thread holds mutex
        using (var externalHolder = helper.HoldMutexExternally())
        {
            externalHolder.IsHolding.Should().BeTrue();

            // Act 2: Second acquirer should be blocked and time out
            using var secondAcquire = helper.TryAcquire(TimeSpan.FromMilliseconds(100));
            secondAcquire.Should().BeNull();

            // Release external holder
            externalHolder.Release();
        }

        // Act 3: Now second acquirer succeeds
        using var subsequentAcquire = helper.TryAcquire(TimeSpan.FromSeconds(1));
        subsequentAcquire.Should().NotBeNull();
        subsequentAcquire!.IsHolding.Should().BeTrue();
    }

    [Fact]
    public void ParameterizedMutexTestHelper_AbandonedMutex_CanBeAcquiredGracefully()
    {
        // Arrange
        using var helper = new ParameterizedMutexTestHelper("Unit_Abandoned");

        // Act: Worker thread acquires mutex and terminates without releasing
        helper.SimulateAbandonedMutex();

        // Calling thread attempts to acquire - Win32 returns WAIT_ABANDONED
        // TryAcquire should handle WAIT_ABANDONED gracefully and return a valid hold scope
        using var scope = helper.TryAcquire(TimeSpan.FromSeconds(1));
        scope.Should().NotBeNull();
        scope!.IsHolding.Should().BeTrue();
    }

    [Fact]
    public void ParameterizedMutexTestHelper_NamedEvent_CanBeCreatedAndSignaled()
    {
        // Arrange
        using var helper = new ParameterizedMutexTestHelper("Unit_Event");
        helper.EventName.Should().StartWith(@"Local\Unit_Event_ROBLOX_Event_");

        nint eventHandle = helper.CreateNamedEvent(manualReset: true, initialState: false);
        eventHandle.Should().NotBe(nint.Zero);

        // Initially nonsignaled
        ParameterizedMutexTestHelper.WaitForSingleObject(eventHandle, 10).Should().Be(ParameterizedMutexTestHelper.WaitTimeout);

        // Signal event
        ParameterizedMutexTestHelper.SetEvent(eventHandle).Should().BeTrue();
        ParameterizedMutexTestHelper.WaitForSingleObject(eventHandle, 50).Should().Be(ParameterizedMutexTestHelper.WaitObject0);

        // Reset event
        ParameterizedMutexTestHelper.ResetEvent(eventHandle).Should().BeTrue();
        ParameterizedMutexTestHelper.WaitForSingleObject(eventHandle, 10).Should().Be(ParameterizedMutexTestHelper.WaitTimeout);
    }

    // =========================================================================
    // 3. MockRobloxEnvironment Tests
    // =========================================================================

    [Fact]
    public void MockRobloxEnvironment_ProvisionsDirectoriesAndDummyInstallationCleanly()
    {
        // Arrange & Act
        using var env = new MockRobloxEnvironment();

        // Assert directory tree
        Directory.Exists(env.RootPath).Should().BeTrue();
        Directory.Exists(env.BaBaBooeyStrapPath).Should().BeTrue();
        Directory.Exists(env.RobloxPath).Should().BeTrue();
        Directory.Exists(env.RobloxLogsPath).Should().BeTrue();
        Directory.Exists(env.RobloxVersionsPath).Should().BeTrue();
        Directory.Exists(env.ModificationsPath).Should().BeTrue();

        // Act: Create dummy installation
        string versionDir = env.CreateDummyRobloxInstallation("version-abc1234");
        Directory.Exists(versionDir).Should().BeTrue();
        File.Exists(Path.Combine(versionDir, "RobloxPlayerBeta.exe")).Should().BeTrue();
        File.Exists(Path.Combine(versionDir, "RobloxPlayerLauncher.exe")).Should().BeTrue();
        File.ReadAllText(Path.Combine(versionDir, "version.txt")).Should().Be("version-abc1234");
    }

    [Fact]
    public void MockRobloxEnvironment_GlobalBasicSettingsXml_SupportsFramerateCapAndReadOnly()
    {
        // Arrange
        using var env = new MockRobloxEnvironment();

        // Act: Create sample XML with read-only lock
        env.CreateSampleGlobalBasicSettingsXml(fpsCap: 165, readOnly: true);

        // Assert
        File.Exists(env.GlobalSettingsXmlPath).Should().BeTrue();
        var attr = File.GetAttributes(env.GlobalSettingsXmlPath);
        attr.HasFlag(FileAttributes.ReadOnly).Should().BeTrue();

        var doc = XDocument.Load(env.GlobalSettingsXmlPath);
        var fpsElement = doc.Descendants("int").FirstOrDefault(e => (string?)e.Attribute("name") == "FramerateCap");
        fpsElement.Should().NotBeNull();
        fpsElement!.Value.Should().Be("165");
    }

    [Fact]
    public void MockRobloxEnvironment_ActiveLogStream_StreamsLinesAndCanBeReadConcurrently()
    {
        // Arrange
        using var env = new MockRobloxEnvironment();

        // Act: Start streaming writer
        using var streamWriter = env.CreateActiveLogStream("test_stream.log");
        streamWriter.WriteLine("Initial Log Header");
        streamWriter.WriteLine("! Joining game 'test-job-id-999' place 1818 at 127.0.0.1");

        // Assert: Read concurrently using FileShare.ReadWrite
        using var readStream = new FileStream(streamWriter.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(readStream, Encoding.UTF8);
        string allText = reader.ReadToEnd();

        allText.Should().Contain("Initial Log Header");
        allText.Should().Contain("! Joining game 'test-job-id-999'");

        // Write another line
        streamWriter.WriteLine("universeid:9999 userid:1111");
        string remaining = reader.ReadToEnd();
        remaining.Should().Contain("universeid:9999");
    }

    [Fact]
    public void MockRobloxEnvironment_Dispose_CleansUpAllDirectoriesEvenWithReadOnlyFiles()
    {
        string rootPath;
        using (var env = new MockRobloxEnvironment())
        {
            rootPath = env.RootPath;
            env.CreateSampleGlobalBasicSettingsXml(fpsCap: 240, readOnly: true);
            env.CreateDummyRobloxInstallation();
            Directory.Exists(rootPath).Should().BeTrue();
        }

        // Assert: After Dispose(), the entire temporary root folder is deleted
        Directory.Exists(rootPath).Should().BeFalse();
    }

    // =========================================================================
    // 4. MockDiscordIpcServer Tests
    // =========================================================================

    [Fact]
    public async Task MockDiscordIpcServer_HandlesHandshakeAndPresenceFramesViaNamedPipe()
    {
        // Arrange
        string testPipe = $"test-discord-ipc-{Guid.NewGuid():N}";
        using var server = new MockDiscordIpcServer(testPipe);
        server.Start();

        // Act 1: Connect Named Pipe Client
        using var client = new NamedPipeClientStream(".", testPipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(3000);
        client.IsConnected.Should().BeTrue();

        // Act 2: Send Handshake Frame (Opcode 0)
        byte[] handshakePayload = Encoding.UTF8.GetBytes("{\"v\":1,\"client_id\":\"1234567890\"}");
        byte[] handshakeFrame = new byte[8 + handshakePayload.Length];
        BitConverter.GetBytes(0).CopyTo(handshakeFrame, 0); // opcode 0
        BitConverter.GetBytes(handshakePayload.Length).CopyTo(handshakeFrame, 4); // length
        handshakePayload.CopyTo(handshakeFrame, 8);
        await client.WriteAsync(handshakeFrame);
        await client.FlushAsync();

        // Read Handshake Response (Opcode 1)
        byte[] respHeader = new byte[8];
        await client.ReadExactlyAsync(respHeader, 0, 8);
        int respOpcode = BitConverter.ToInt32(respHeader, 0);
        int respLength = BitConverter.ToInt32(respHeader, 4);
        byte[] respPayload = new byte[respLength];
        await client.ReadExactlyAsync(respPayload, 0, respLength);
        string respJson = Encoding.UTF8.GetString(respPayload);

        respOpcode.Should().Be(1);
        respJson.Should().Contain("\"evt\":\"READY\"");

        // Act 3: Send Activity Frame (Opcode 1)
        byte[] activityPayload = Encoding.UTF8.GetBytes("{\"cmd\":\"SET_ACTIVITY\",\"args\":{\"activity\":{\"details\":\"Testing Roblox Multi-Instance\"}}}");
        byte[] activityFrame = new byte[8 + activityPayload.Length];
        BitConverter.GetBytes(1).CopyTo(activityFrame, 0); // opcode 1
        BitConverter.GetBytes(activityPayload.Length).CopyTo(activityFrame, 4); // length
        activityPayload.CopyTo(activityFrame, 8);
        await client.WriteAsync(activityFrame);
        await client.FlushAsync();

        // Assert server recorded the message
        bool messageReceived = await server.WaitForMessageAsync(m => m.Contains("Testing Roblox Multi-Instance"), TimeSpan.FromSeconds(3));
        messageReceived.Should().BeTrue();
    }
}
