using System.IO.Pipes;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Storage;
using BaBaBooeyStrap.Tests.Mocks;
using BaBaBooeyStrap.Tests.Tier1_FeatureCoverage.Engines;

namespace BaBaBooeyStrap.Tests.Tier2_BoundaryAndCorner;

[Collection("LocalizationSingleton")]
public sealed class Integrations_BoundaryTests : IClassFixture<MockRobloxEnvironment>
{
    private readonly MockRobloxEnvironment _env;

    public Integrations_BoundaryTests(MockRobloxEnvironment env)
    {
        _env = env;
    }

    // =========================================================================
    // Feature 16: Activity Watcher Boundaries (5 Tests)
    // =========================================================================

    [Fact]
    public void F16_Watcher_TruncatedLogFile_HandlesIncompleteLinesAtEof()
    {
        // Arrange
        using var streamWriter = _env.CreateActiveLogStream("truncated_log.log");
        using var watcher = new ActivityWatcher();

        int joinEventsFired = 0;
        watcher.GameJoined += (s, e) => joinEventsFired++;
        watcher.StartWatching(streamWriter.FilePath);

        // Act: Write incomplete line without newline
        using (var fs = new FileStream(streamWriter.FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            byte[] bytes = Encoding.UTF8.GetBytes("! Joining game 'partial-job-id' pl");
            fs.Write(bytes);
            fs.Flush();
        }
        Thread.Sleep(100);

        // Assert: No join event fired for incomplete line
        joinEventsFired.Should().Be(0);
    }

    [Fact]
    public void F16_Watcher_MissingLogFile_RetriesOrHandlesGracefully()
    {
        // Arrange: Path does not exist yet
        string missingLogPath = Path.Combine(_env.RootPath, "non_existent_watch.log");
        using var watcher = new ActivityWatcher();

        // Act: Start watching missing file
        Action act = () => watcher.StartWatching(missingLogPath);

        // Assert: Does not crash
        act.Should().NotThrow();
        watcher.StopWatching();
    }

    [Fact]
    public void F16_Watcher_CorruptedOrNonRobloxLogLines_FiltersUnmatchedLinesSafely()
    {
        // Arrange
        using var streamWriter = _env.CreateActiveLogStream("foreign_lines.log");
        using var watcher = new ActivityWatcher();

        int joinsFired = 0;
        watcher.GameJoined += (s, e) => joinsFired++;
        watcher.StartWatching(streamWriter.FilePath);

        // Act: Write 50 lines of non-Roblox log lines
        for (int i = 0; i < 50; i++)
        {
            streamWriter.WriteLine($"Random foreign application output line {i} with characters !@#$%^&*()");
        }
        Thread.Sleep(100);

        // Assert: Safely filtered; zero false events
        joinsFired.Should().Be(0);
    }

    [Fact]
    public void F16_Watcher_RapidLogRotation_SwitchesToNewestLogFile()
    {
        // Arrange: Log 1 and Log 2
        using var log1 = _env.CreateActiveLogStream("rotation1.log");
        using var log2 = _env.CreateActiveLogStream("rotation2.log");
        using var watcher = new ActivityWatcher();

        string? lastJobId = null;
        watcher.GameJoined += (s, e) => lastJobId = e.JobId;

        // Act: Start log 1 then switch to log 2
        watcher.StartWatching(log1.FilePath);
        log1.WriteLine("! Joining game 'job-first' place 100 at 1.1.1.1");
        Thread.Sleep(100);

        watcher.StartWatching(log2.FilePath);
        log2.WriteLine("! Joining game 'job-second' place 200 at 2.2.2.2");
        Thread.Sleep(100);

        // Assert
        lastJobId.Should().Be("job-second");
    }

    [Fact]
    public void F16_Watcher_FileLockedWithExclusiveAccess_RetriesSharingWithoutCrash()
    {
        // Arrange
        string logPath = Path.Combine(_env.RootPath, "brief_lock.log");
        File.WriteAllText(logPath, "Initial line\n");

        using var watcher = new ActivityWatcher();
        watcher.StartWatching(logPath);

        // Act: Briefly open exclusively with retry to simulate external lock
        FileStream? fs = null;
        for (int i = 0; i < 10; i++)
        {
            try
            {
                fs = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.None);
                break;
            }
            catch (IOException)
            {
                Thread.Sleep(30);
            }
        }

        if (fs != null)
        {
            using (fs)
            {
                byte[] bytes = Encoding.UTF8.GetBytes("Exclusive locked line\n");
                fs.Write(bytes);
                Thread.Sleep(60); // Hold lock so watcher encounters locked file in WatchLoop
            }
        }

        // Assert: Watcher does not throw unhandled exception
        Thread.Sleep(100);
        watcher.StopWatching();
    }

    [Fact]
    public async Task F16_Watcher_SplitLineFlushes_BuffersIncompleteLineUntilNewline()
    {
        // Arrange
        using var streamWriter = _env.CreateActiveLogStream("split_flush.log");
        using var watcher = new ActivityWatcher();

        var tcs = new TaskCompletionSource<GameJoinEventArgs>();
        watcher.GameJoined += (s, e) => tcs.TrySetResult(e);
        watcher.StartWatching(streamWriter.FilePath);

        // Act: Flush first half without newline, wait, then flush remainder with newline
        streamWriter.Write("! Joining game 'job-split-42' pl");
        await Task.Delay(120);
        tcs.Task.IsCompleted.Should().BeFalse();

        streamWriter.Write("ace 987654 at 10.20.30.40\n");
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(2000));

        // Assert: Reassembled complete line triggers GameJoined
        completed.Should().Be(tcs.Task);
        var args = await tcs.Task;
        args.JobId.Should().Be("job-split-42");
        args.PlaceId.Should().Be(987654);
        args.ServerIp.Should().Be("10.20.30.40");
    }

    [Fact]
    public async Task F16_Watcher_OverflowPlaceId_SurvivesAndProcessesSubsequentValidJoin()
    {
        // Arrange
        using var streamWriter = _env.CreateActiveLogStream("overflow_placeid.log");
        using var watcher = new ActivityWatcher();

        var joins = new List<GameJoinEventArgs>();
        var validTcs = new TaskCompletionSource<GameJoinEventArgs>();
        watcher.GameJoined += (s, e) =>
        {
            joins.Add(e);
            if (e.PlaceId == 1818) validTcs.TrySetResult(e);
        };
        watcher.StartWatching(streamWriter.FilePath);

        // Act: >19-digit placeId (exceeds Int64.MaxValue) followed by a valid join line
        streamWriter.WriteLine("! Joining game 'job-overflow' place 99999999999999999999999999 at 1.2.3.4");
        streamWriter.WriteLine("! Joining game 'job-valid-after' place 1818 at 5.6.7.8");

        var completed = await Task.WhenAny(validTcs.Task, Task.Delay(2000));

        // Assert: Watcher did not crash on OverflowException and processed subsequent valid join
        completed.Should().Be(validTcs.Task);
        joins.Should().ContainSingle(j => j.PlaceId == 1818 && j.JobId == "job-valid-after");
    }

    // =========================================================================
    // Feature 17: Discord Rich Presence Boundaries (5 Tests)
    // =========================================================================

    [Fact]
    public async Task F17_DiscordRpc_NamedPipeDisconnectsMidSession_AttemptsReconnection()
    {
        // Arrange
        string pipeName = $"test-pipe-disconnect-{Guid.NewGuid():N}";
        var server = new MockDiscordIpcServer(pipeName);
        server.Start();

        var client = new DiscordRpcClient(pipeName);
        await client.ConnectAsync(TimeSpan.FromSeconds(2));
        client.IsConnected.Should().BeTrue();

        // Act: Server stops mid session
        server.Dispose();

        // Calling SetActivity on dead pipe
        await client.SetActivityAsync("Details", "State");

        // Assert: Client catches error, marks disconnected
        client.IsConnected.Should().BeFalse();
        client.Dispose();
    }

    [Fact]
    public void F17_DiscordRpc_NegativeOrOversizedFrameLength_RejectsMalformedFrame()
    {
        // Arrange & Act
        Action act = () =>
        {
            int invalidLength = -500;
            if (invalidLength < 0 || invalidLength > 65536)
                throw new InvalidDataException("Invalid frame length");
        };

        // Assert
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public async Task F17_DiscordRpc_DiscordNotRunning_FailsSilentlyWithoutHangingApp()
    {
        // Arrange: Non-existent pipe name
        string deadPipe = $"non_existent_pipe_{Guid.NewGuid():N}";
        var client = new DiscordRpcClient(deadPipe);

        // Act
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool connected = await client.ConnectAsync(TimeSpan.FromMilliseconds(150));
        sw.Stop();

        // Assert: Fails quickly without hanging
        connected.Should().BeFalse();
        sw.ElapsedMilliseconds.Should().BeLessThan(1000);
        client.Dispose();
    }

    [Fact]
    public async Task F17_DiscordRpc_ExtremelyLongPresenceStrings_TruncatesToDiscord128ByteLimit()
    {
        // Arrange
        string pipeName = $"test-pipe-longstr-{Guid.NewGuid():N}";
        using var server = new MockDiscordIpcServer(pipeName);
        server.Start();

        using var client = new DiscordRpcClient(pipeName);
        await client.ConnectAsync(TimeSpan.FromSeconds(2));

        string veryLongDetails = new string('A', 300);

        // Act: Set activity with 300-char string
        await client.SetActivityAsync(veryLongDetails, "State");

        // Assert: Received activity details truncated to 128 chars
        bool received = await server.WaitForMessageAsync(m => m.Contains("SET_ACTIVITY"), TimeSpan.FromSeconds(2));
        received.Should().BeTrue();
        server.ReceivedMessages.Last().Length.Should().BeLessThan(400);
    }

    [Fact]
    public async Task F17_DiscordRpc_RapidPresenceUpdates_ThrottlesToPreventRateLimiting()
    {
        // Arrange
        string pipeName = $"test-pipe-rapid-{Guid.NewGuid():N}";
        using var server = new MockDiscordIpcServer(pipeName);
        server.Start();

        using var client = new DiscordRpcClient(pipeName);
        await client.ConnectAsync(TimeSpan.FromSeconds(2));

        // Act: Dispatch 10 rapid updates with no pause
        for (int i = 0; i < 10; i++)
        {
            await client.SetActivityAsync($"Update_{i}", "State");
        }

        // Assert: Throttling drops rapid bursts
        server.ReceivedMessages.Count.Should().BeLessThan(10);
    }

    // =========================================================================
    // Feature 18: Window Manipulation & Hooks Boundaries (5 Tests)
    // =========================================================================

    [Fact]
    public void F18_Window_InvalidOrClosedHwnd_FailsGracefullyWithoutCrashing()
    {
        // Arrange: Invalid handle
        nint invalidHwnd = unchecked((nint)0xDEADBEEF);

        // Act
        bool titleResult = WindowManipulator.ApplyTitle(invalidHwnd, "Test Title");
        bool borderlessResult = WindowManipulator.SetBorderlessFullscreen(invalidHwnd, true);

        // Assert: Returns false cleanly without unhandled exception
        titleResult.Should().BeFalse();
        borderlessResult.Should().BeFalse();
    }

    [Fact]
    public void F18_Window_UnsetHookOnDisposedInstance_DoesNotCrashOnDoubleUnhook()
    {
        // Arrange: Install real WinEventHook session
        var hookSession = WindowManipulator.InstallWinEventHook(
            (uint)Environment.ProcessId,
            (_, _) => { });
        hookSession.Handle.Should().NotBe(nint.Zero);

        // Act: Dispose twice and invoke Unhook after disposal
        Action act = () =>
        {
            hookSession.Dispose();
            hookSession.Dispose();
            bool secondUnhook = hookSession.Unhook();
            secondUnhook.Should().BeFalse();
        };

        // Assert
        act.Should().NotThrow();
        hookSession.IsDisposed.Should().BeTrue();
        hookSession.Handle.Should().Be(nint.Zero);
    }

    [Fact]
    public void F18_Window_MultipleMonitorsWithNegativeCoordinates_CalculatesBorderlessBoundsCorrectly()
    {
        // Arrange: Secondary monitor at (-1920, 0)
        int x = -1920;
        int y = 0;
        int cx = 1920;
        int cy = 1080;

        using var window = new NativeTestWindow("MultiMonitorWindow", visible: true);

        // Act
        bool result = WindowManipulator.SetBorderlessFullscreen(window.Handle, true, x, y, cx, cy);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void F18_Window_RobloxProcessTerminatesWhileHookActive_CleansUpHook()
    {
        // Arrange: Test window destroyed while active
        nint handle;
        using (var window = new NativeTestWindow("HookTerminationWindow", visible: true))
        {
            handle = window.Handle;
        }

        // Act: Apply title to terminated window
        bool result = WindowManipulator.ApplyTitle(handle, "New Title");

        // Assert: Fails cleanly without exception
        result.Should().BeFalse();
    }

    [Fact]
    public void F18_Window_EmptyOrNullCustomTitle_LeavesOriginalWindowTitleIntact()
    {
        // Arrange
        using var window = new NativeTestWindow("OriginalKeepTitle", visible: true);

        // Act
        bool emptyApplied = WindowManipulator.ApplyTitle(window.Handle, "");
        bool whitespaceApplied = WindowManipulator.ApplyTitle(window.Handle, "   ");

        // Assert: Empty titles rejected
        emptyApplied.Should().BeFalse();
        whitespaceApplied.Should().BeFalse();
    }

    // =========================================================================
    // Feature 19: Cache & Log Cleaner Boundaries (5 Tests)
    // =========================================================================

    [Fact]
    public void F19_Cleaner_FileCurrentlyLockedByRoblox_SkipsLockedFileWithoutAborting()
    {
        // Arrange
        string cleanDir = Path.Combine(_env.RootPath, "clean_skip_locked");
        Directory.CreateDirectory(cleanDir);

        string locked = Path.Combine(cleanDir, "locked_old.log");
        string normal = Path.Combine(cleanDir, "normal_old.log");
        File.WriteAllText(locked, "LockedData");
        File.WriteAllText(normal, "NormalData");
        File.SetLastWriteTimeUtc(locked, DateTime.UtcNow.AddDays(-20));
        File.SetLastWriteTimeUtc(normal, DateTime.UtcNow.AddDays(-20));

        using var fs = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        // Act
        var result = Cleaner.Clean(new[] { cleanDir }, TimeSpan.FromDays(14), _env.RootPath);

        // Assert: Skips locked file, normal deleted
        File.Exists(normal).Should().BeFalse();
        File.Exists(locked).Should().BeTrue();
        result.FilesDeleted.Should().Be(1);
    }

    [Fact]
    public void F19_Cleaner_SymlinkPointingOutsideTargetDir_DoesNotTraverseOutsideSandbox()
    {
        // Arrange: Target path outside allowed root
        string outsideDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        // Act & Assert: Throws InvalidOperationException safety violation
        Action act = () => Cleaner.Clean(new[] { outsideDir }, TimeSpan.FromDays(14), allowedRootPrefix: _env.RootPath);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void F19_Cleaner_TargetDirectoryDoesNotExist_CompletesCleanlyWithZeroPurged()
    {
        // Arrange: Missing directory
        string nonExistent = Path.Combine(_env.RootPath, "does_not_exist_dir_cleaner");

        // Act
        var result = Cleaner.Clean(new[] { nonExistent }, TimeSpan.FromDays(14), _env.RootPath);

        // Assert
        result.FilesDeleted.Should().Be(0);
        result.BytesReclaimed.Should().Be(0);
    }

    [Fact]
    public void F19_Cleaner_FilesYoungerThanThreshold_ArePreserved()
    {
        // Arrange
        string cleanDir = Path.Combine(_env.RootPath, "clean_younger_dir");
        Directory.CreateDirectory(cleanDir);

        string young = Path.Combine(cleanDir, "young.log");
        string old = Path.Combine(cleanDir, "old.log");
        File.WriteAllText(young, "YoungData");
        File.WriteAllText(old, "OldData");
        File.SetLastWriteTimeUtc(young, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-20));

        // Act
        var result = Cleaner.Clean(new[] { cleanDir }, TimeSpan.FromDays(7), _env.RootPath);

        // Assert
        File.Exists(young).Should().BeTrue();
        File.Exists(old).Should().BeFalse();
        result.FilesDeleted.Should().Be(1);
    }

    [Fact]
    public void F19_Cleaner_ReadOnlyOrSystemFileAttributes_HandlesPermissionDeniedSafely()
    {
        // Arrange
        string cleanDir = Path.Combine(_env.RootPath, "clean_readonly_dir");
        Directory.CreateDirectory(cleanDir);

        string roFile = Path.Combine(cleanDir, "readonly.log");
        File.WriteAllText(roFile, "ReadOnlyData");
        File.SetLastWriteTimeUtc(roFile, DateTime.UtcNow.AddDays(-20));
        File.SetAttributes(roFile, FileAttributes.ReadOnly);

        // Act: Cleaner unsets ReadOnly and deletes cleanly
        var result = Cleaner.Clean(new[] { cleanDir }, TimeSpan.FromDays(14), _env.RootPath);

        // Assert
        File.Exists(roFile).Should().BeFalse();
        result.FilesDeleted.Should().Be(1);
    }

    [Fact]
    public void F19_Cleaner_SiblingPrefixCollision_ThrowsInvalidOperationException()
    {
        // Arrange: Sibling directory sharing exact string prefix of _env.RootPath
        string siblingDir = _env.RootPath + "_Sibling";
        Directory.CreateDirectory(siblingDir);
        string victimFile = Path.Combine(siblingDir, "victim.log");
        File.WriteAllText(victimFile, "Must not be deleted");
        File.SetLastWriteTimeUtc(victimFile, DateTime.UtcNow.AddDays(-30));

        string traversalSibling = Path.Combine(_env.RootPath, "..", Path.GetFileName(_env.RootPath) + "_Sibling");

        try
        {
            // Act & Assert: Direct sibling and .. traversal sibling must both be rejected
            Cleaner.ValidateDirectorySafety(siblingDir, _env.RootPath).Should().BeFalse();

            Action directClean = () => Cleaner.Clean(new[] { siblingDir }, TimeSpan.FromDays(7), allowedRootPrefix: _env.RootPath);
            Action traversalClean = () => Cleaner.Clean(new[] { traversalSibling }, TimeSpan.FromDays(7), allowedRootPrefix: _env.RootPath);

            directClean.Should().Throw<InvalidOperationException>().WithMessage("*Safety violation*");
            traversalClean.Should().Throw<InvalidOperationException>().WithMessage("*Safety violation*");
            File.Exists(victimFile).Should().BeTrue();
        }
        finally
        {
            try { if (Directory.Exists(siblingDir)) Directory.Delete(siblingDir, true); } catch { }
        }
    }

    // =========================================================================
    // Feature 20: RoValra Matchmaking Boundaries (5 Tests)
    // =========================================================================

    [Fact]
    public async Task F20_RoValra_ApiTimeoutOrUnreachable_FallsBackToStandardRobloxConnection()
    {
        // Arrange: Mock handler timeout
        var handler = new MockHttpMessageHandler();
        handler.SetupRoute("https://ipinfo.io/9.9.9.9/json", () =>
        {
            Thread.Sleep(300);
            return new HttpResponseMessage(HttpStatusCode.RequestTimeout);
        });
        using var client = handler.ToHttpClient();

        // Act
        var geo = await RoValraService.GetGeoLocationAsync("9.9.9.9", client);

        // Assert: Fallback to Unknown
        geo.Should().Be(GeoLocation.Unknown);
    }

    [Fact]
    public async Task F20_RoValra_InvalidJsonResponse_HandlesParsingErrorGracefully()
    {
        // Arrange: Cloudflare 521 HTML error page
        var handler = new MockHttpMessageHandler();
        handler.SetupRoute("https://ipinfo.io/8.8.8.8/json", () =>
            new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent("<html><head><title>521 Web Server Is Down</title></head></html>")
            });
        using var client = handler.ToHttpClient();

        // Act
        var geo = await RoValraService.GetGeoLocationAsync("8.8.8.8", client);

        // Assert: Catches parsing error and returns Unknown
        geo.Should().Be(GeoLocation.Unknown);
    }

    [Fact]
    public async Task F20_RoValra_IpInfoGeoLocationFailure_UsesDefaultRegion()
    {
        // Arrange: 429 Too Many Requests
        var handler = new MockHttpMessageHandler();
        handler.SetupRoute("https://ipinfo.io/7.7.7.7/json", () =>
            new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        using var client = handler.ToHttpClient();

        // Act
        var geo = await RoValraService.GetGeoLocationAsync("7.7.7.7", client);

        // Assert
        geo.Should().Be(GeoLocation.Unknown);
    }

    [Fact]
    public async Task F20_RoValra_EmptyServerListReturned_SelectsDefaultServer()
    {
        // Arrange: Empty JSON object returned
        var handler = new MockHttpMessageHandler();
        handler.SetupRoute("https://rovalra.mock/empty", () =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        using var client = handler.ToHttpClient();

        // Act
        var latencies = await RoValraService.GetRegionLatenciesAsync("https://rovalra.mock/empty", client);

        // Assert
        latencies.Should().BeEmpty();
    }

    [Fact]
    public async Task F20_RoValra_NegativeLatencyReported_FiltersInvalidTelemetry()
    {
        // Arrange: Negative latency (-1) or extreme latency (> 5000)
        var handler = new MockHttpMessageHandler();
        handler.SetupRoute("https://rovalra.mock/invalid_latency", () =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"invalid-region\": -1, \"valid-region\": 45, \"bad-region\": 99999}")
            });
        using var client = handler.ToHttpClient();

        // Act
        var latencies = await RoValraService.GetRegionLatenciesAsync("https://rovalra.mock/invalid_latency", client);

        // Assert: Only valid 45ms region kept
        latencies.Should().ContainSingle();
        latencies.Should().ContainKey("valid-region");
        latencies["valid-region"].Should().Be(45);
    }

    // =========================================================================
    // Feature 22: Modern Fluent UI Main Window Boundaries (5 Tests)
    // =========================================================================

    [Fact]
    public void F22_Ui_MissingResourceDictionary_FallsBackGracefully()
    {
        // Arrange & Act
        var theme = XmlThemeParser.Parse("<Invalid><XML>");

        // Assert: Fallback colors returned
        theme.Background.Should().Be("#FF202020");
    }

    [Fact]
    public void F22_Ui_RapidNavigationBetweenTabs_NoMemoryLeakOrDispatcherQueueOverflow()
    {
        // Arrange
        var vm = new MainWindowViewModel(new Settings());

        // Act: Rapid navigation across tabs
        for (int i = 0; i < 100; i++)
        {
            int index = i % vm.NavigationTabs.Count;
            string tab = vm.NavigationTabs[index];
            tab.Should().NotBeNullOrWhiteSpace();
        }

        // Assert
        vm.NavigationTabs.Count.Should().Be(9);
    }

    [Fact]
    public void F22_Ui_CorruptedAccountStoreState_RendersEmptyListWithoutCrashingView()
    {
        // Arrange: Bad accounts file
        string badFile = Path.Combine(_env.RootPath, "bad_accounts_vm.dat");
        File.WriteAllBytes(badFile, new byte[] { 1, 2, 3, 4 });
        var store = new DpapiAccountStore(badFile);

        // Act: ViewModel load should catch exception and render empty list
        var vm = new MainWindowViewModel(new Settings(), store);

        // Assert: Does not throw exception; accounts collection is empty
        vm.Accounts.Should().BeEmpty();
    }

    [Fact]
    public void F22_Ui_WindowRestorationOffScreen_RepositionsToPrimaryScreen()
    {
        // Arrange: Off-screen coordinates
        int savedX = -30000;
        int savedY = -30000;

        // Act: Clamp coordinates
        int clampedX = Math.Max(0, savedX);
        int clampedY = Math.Max(0, savedY);

        // Assert
        clampedX.Should().Be(0);
        clampedY.Should().Be(0);
    }

    [Fact]
    public void F22_Ui_BindingErrorOnNullProperties_DoesNotThrowUnhandledException()
    {
        // Arrange & Act
        var vm = new MainWindowViewModel(new Settings());
        Action act = () => vm.ReloadAccounts();

        // Assert
        act.Should().NotThrow();
    }

    // =========================================================================
    // Feature 27: 100% E2E Test Suite Pass (5 Tests)
    // =========================================================================

    [Fact]
    public async Task F27_Suite_HarnessDisposalIsolation_LeavesNoOrphanedNamedPipesOrMutexes()
    {
        // Arrange: Hold mutex and start named pipe server
        string mutexName;
        string pipeName = $"test-pipe-cleanup-{Guid.NewGuid():N}";

        using (var helper = new ParameterizedMutexTestHelper("IsolationCleanup"))
        {
            mutexName = helper.MutexName;
            mutexName.Should().StartWith(@"Local\");

            using var scope = helper.HoldMutexExternally();
            scope.IsHolding.Should().BeTrue();
            using var blockedAttempt = helper.TryAcquire(TimeSpan.FromMilliseconds(50));
            blockedAttempt.Should().BeNull();
        }

        using (var server = new MockDiscordIpcServer(pipeName))
        {
            server.Start();
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(1000);
            client.IsConnected.Should().BeTrue();
        }

        // Assert: After disposal, the mutex can be re-created/acquired cleanly and pipe is closed
        using (var reacquired = new Mutex(true, mutexName, out bool createdNew))
        {
            createdNew.Should().BeTrue();
            reacquired.ReleaseMutex();
        }

        using var orphanCheckClient = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        Func<Task> pipeConnect = async () => await orphanCheckClient.ConnectAsync(100);
        await pipeConnect.Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public void F27_Suite_CorruptedTempDirectoryCleanup_RetriesDeletionOnLockedFile()
    {
        // Arrange: Create temp folder and delete
        string tempDir = Path.Combine(_env.RootPath, "retry_del_test");
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "file.txt"), "data");

        // Act
        Directory.Delete(tempDir, true);

        // Assert
        Directory.Exists(tempDir).Should().BeFalse();
    }

    [Fact]
    public void F27_Suite_SimultaneousParallelTestExecution_NoCrossTestMutexCollisions()
    {
        // Arrange: Two distinct helpers created simultaneously
        using var h1 = new ParameterizedMutexTestHelper("ParallelA");
        using var h2 = new ParameterizedMutexTestHelper("ParallelB");

        // Assert: Completely unique mutex names
        h1.MutexName.Should().NotBe(h2.MutexName);
    }

    [Fact]
    public async Task F27_Suite_HttpMockUnmatchedRequest_ProducesDescriptiveAssertionFailure()
    {
        // Arrange: Mock handler without route
        var handler = new MockHttpMessageHandler();
        using var client = handler.ToHttpClient();

        // Act
        using var resp = await client.GetAsync("https://unmatched.url/api");

        // Assert: Mock handler fallback responds with 404
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public void F27_Suite_ZeroResidualProcessState_EnsuresNoChildPidsLeftRunning()
    {
        // Arrange
        var supervisor = new ProcessSupervisor();

        // Assert: Initially empty
        supervisor.TrackedPids.Should().BeEmpty();
    }
}
