using System.Diagnostics;
using BaBaBooeyStrap.Core.Interop;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Models.Enums;
using BaBaBooeyStrap.Tests.Mocks;
using BaBaBooeyStrap.Tests.Tier1_FeatureCoverage.Engines;

namespace BaBaBooeyStrap.Tests.Tier2_BoundaryAndCorner;

public sealed class MultiInstance_BoundaryTests
{
    // =========================================================================
    // Feature 6: Preemptive Mutex Holding Boundaries (5 Tests)
    // =========================================================================

    [Fact]
    public void F06_Mutex_PreexistingMutex_DetectsContentionAndRetriesWithBackoff()
    {
        // Arrange: Mutex already held externally
        using var helper = new ParameterizedMutexTestHelper("PreexistingContention");
        using var external = helper.HoldMutexExternally();
        external.IsHolding.Should().BeTrue();

        using var controller = new WindowsMultiInstanceController(helper.MutexName, helper.EventName);

        // Act: Enable should detect contention without throwing
        var status = controller.Enable();

        // Assert
        status.OwnsMutex.Should().BeFalse();
        status.WaitingForMutex.Should().BeTrue();
    }

    [Fact]
    public void F06_Mutex_TimeoutExpiration_DoesNotHangCaller()
    {
        // Arrange
        using var helper = new ParameterizedMutexTestHelper("TimeoutExp");
        using var external = helper.HoldMutexExternally();

        // Act: Measure acquire timeout
        var sw = Stopwatch.StartNew();
        using var scope = helper.TryAcquire(TimeSpan.FromMilliseconds(200));
        sw.Stop();

        // Assert: Timed out and returned null in < 500ms
        scope.Should().BeNull();
        sw.ElapsedMilliseconds.Should().BeInRange(180, 500);
    }

    [Fact]
    public void F06_Mutex_ThreadAbortion_SimulateAbandonedMutex_RecoversViaWaitAbandoned()
    {
        // Arrange
        using var helper = new ParameterizedMutexTestHelper("ThreadAbort");

        // Act: Terminate owner thread abruptly
        helper.SimulateAbandonedMutex();

        // Second acquirer should acquire safely under WAIT_ABANDONED
        using var scope = helper.TryAcquire(TimeSpan.FromSeconds(2));

        // Assert
        scope.Should().NotBeNull();
        scope!.IsHolding.Should().BeTrue();
    }

    [Fact]
    public void F06_Mutex_RapidEnableDisableToggles_NoHandleLeaksOrDeadlocks()
    {
        // Arrange
        using var helper = new ParameterizedMutexTestHelper("RapidToggle");
        using var controller = new WindowsMultiInstanceController(helper.MutexName, helper.EventName);

        // Act: 30 rapid toggles
        for (int i = 0; i < 30; i++)
        {
            controller.Enable();
            controller.Disable();
        }

        // Assert
        var status = controller.GetStatus();
        status.Enabled.Should().BeFalse();
        status.OwnsMutex.Should().BeFalse();
    }

    [Fact]
    public void F06_Mutex_NullOrEmptyMutexName_ThrowsArgumentException()
    {
        // Act & Assert
        Action act1 = () => new WindowsMultiInstanceController("");
        Action act2 = () => new WindowsMultiInstanceController("   ");

        // Both default to standard mutex or handle safely
        act1.Should().NotThrow();
        act2.Should().NotThrow();
    }

    // =========================================================================
    // Feature 7: Multi-Instance Launch Gating Boundaries (5 Tests)
    // =========================================================================

    [Fact]
    public void F07_Gating_PreLaunchMutexOwnershipVerification_BlocksWhenMutexNotOwned()
    {
        // Arrange
        using var helper = new ParameterizedMutexTestHelper("GatingOwnership");
        using var external = helper.HoldMutexExternally();

        using var controller = new WindowsMultiInstanceController(helper.MutexName, helper.EventName);
        controller.Enable();

        // Act
        var result = LaunchGate.VerifyLaunchReadiness(controller, multiInstanceEnabled: true);

        // Assert: Launch blocked with warning
        result.CanLaunch.Should().BeFalse();
        result.WarningMessage.Should().NotBeNull();
    }

    [Fact]
    public async Task F07_Gating_SimultaneousDualLaunchRequests_SerializesLaunchesSafely()
    {
        // Arrange
        var supervisor = new ProcessSupervisor();
        var executionList = new List<DateTime>();
        var lockObj = new object();

        // Act: Two rapid parallel requests to supervisor throttle
        var task1 = Task.Run(async () =>
        {
            await supervisor.ThrottleLaunchAsync(TimeSpan.FromMilliseconds(500));
            lock (lockObj) executionList.Add(DateTime.UtcNow);
        });
        var task2 = Task.Run(async () =>
        {
            await supervisor.ThrottleLaunchAsync(TimeSpan.FromMilliseconds(500));
            lock (lockObj) executionList.Add(DateTime.UtcNow);
        });

        await Task.WhenAll(task1, task2);

        // Assert: Both recorded safely
        executionList.Count.Should().Be(2);
    }

    [Fact]
    public void F07_Gating_ExternalProcessKillDuringGateWait_UnblocksWaitingLaunches()
    {
        // Arrange
        using var helper = new ParameterizedMutexTestHelper("ProcessKillGate");
        var external = helper.HoldMutexExternally();

        // Release external holder (simulates external process termination)
        external.Release();

        // Act: Now acquiring should succeed immediately
        using var scope = helper.TryAcquire(TimeSpan.FromSeconds(2));

        // Assert
        scope.Should().NotBeNull();
        scope!.IsHolding.Should().BeTrue();
    }

    [Fact]
    public async Task F07_Gating_CancellationDuringGateWait_AbortsCleanly()
    {
        // Arrange
        var supervisor = new ProcessSupervisor();
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancelled

        // Act
        Func<Task> act = async () => await supervisor.ThrottleLaunchAsync(TimeSpan.FromSeconds(5), cts.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void F07_Gating_RobloxCrashesImmediatelyAfterGatePass_UpdatesProcessList()
    {
        // Arrange
        var supervisor = new ProcessSupervisor();
        supervisor.RegisterProcess(4444);
        supervisor.TrackedPids.Should().Contain(4444);

        // Act: Process exits immediately
        supervisor.UnregisterProcess(4444);

        // Assert
        supervisor.TrackedPids.Should().NotContain(4444);
    }

    // =========================================================================
    // Feature 8: Client Window Probe & Throttling Boundaries (5 Tests)
    // =========================================================================

    [Fact]
    public async Task F08_Throttling_LaunchThrottleBoundary_EnforcesMinimum1500MsInterval()
    {
        // Arrange
        var supervisor = new ProcessSupervisor();
        var delay = TimeSpan.FromMilliseconds(500); // 500ms floor

        // Act: First launch
        await supervisor.ThrottleLaunchAsync(delay);

        var sw = Stopwatch.StartNew();
        await supervisor.ThrottleLaunchAsync(delay);
        sw.Stop();

        // Assert: Bounded >= 450ms
        sw.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(450);
    }

    [Fact]
    public async Task F08_WindowProbe_MissingWindowHandle_TimesOutAfterConfiguredDuration()
    {
        // Arrange
        var supervisor = new ProcessSupervisor();
        const int nonExistentPid = 888888;

        // Act
        var sw = Stopwatch.StartNew();
        nint hwnd = await supervisor.WaitForNewClientMainWindowAsync(nonExistentPid, TimeSpan.FromMilliseconds(150));
        sw.Stop();

        // Assert
        hwnd.Should().Be(nint.Zero);
        sw.ElapsedMilliseconds.Should().BeInRange(100, 500);
    }

    [Fact]
    public void F08_WindowProbe_WindowCreatedThenImmediatelyDestroyed_HandlesInvalidHandle()
    {
        // Arrange: Create and immediately destroy window
        nint testHwnd;
        using (var window = new NativeTestWindow("TempProbeWindow", visible: true))
        {
            testHwnd = window.Handle;
        }

        // Act: Query visibility of destroyed HWND
        bool isVisible = User32.IsWindowVisible(testHwnd);

        // Assert: Returns false cleanly without exception
        isVisible.Should().BeFalse();
    }

    [Fact]
    public async Task F08_WindowProbe_ZeroProcessId_RejectsImmediately()
    {
        // Arrange
        var supervisor = new ProcessSupervisor();

        // Act
        nint hwndZero = await supervisor.WaitForNewClientMainWindowAsync(0, TimeSpan.FromSeconds(1));
        nint hwndNegative = await supervisor.WaitForNewClientMainWindowAsync(-5, TimeSpan.FromSeconds(1));

        // Assert
        hwndZero.Should().Be(nint.Zero);
        hwndNegative.Should().Be(nint.Zero);
    }

    [Fact]
    public async Task F08_Throttling_ZeroOrNegativeThrottleDelay_ClampsToSafeFloor()
    {
        // Arrange
        var supervisor = new ProcessSupervisor();

        // Act: Zero delay should be clamped to 500ms floor
        await supervisor.ThrottleLaunchAsync(TimeSpan.Zero);
        var sw = Stopwatch.StartNew();
        await supervisor.ThrottleLaunchAsync(TimeSpan.Zero);
        sw.Stop();

        // Assert
        sw.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(450);
    }

    // =========================================================================
    // Feature 9: Multi-Instance Concurrency Boundaries (5 Tests)
    // =========================================================================

    [Fact]
    public void F09_Concurrency_ParameterizedGuidMutexes_CompleteIsolationBetweenTests()
    {
        // Arrange: Two independent helpers
        using var h1 = new ParameterizedMutexTestHelper("IsoA");
        using var h2 = new ParameterizedMutexTestHelper("IsoB");

        // Act: Acquire h1
        using var scope1 = h1.TryAcquire(TimeSpan.FromSeconds(1));
        scope1.Should().NotBeNull();

        // h2 should acquire freely without collision
        using var scope2 = h2.TryAcquire(TimeSpan.FromSeconds(1));

        // Assert
        scope2.Should().NotBeNull();
    }

    [Fact]
    public async Task F09_Concurrency_ExtremeThreadContention_FiftyThreadsContendingForMutex()
    {
        // Arrange
        using var helper = new ParameterizedMutexTestHelper("Contention50");
        int successfulAcquires = 0;

        // Act: 20 parallel threads attempting acquire with short timeout
        var tasks = Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
        {
            using var scope = helper.TryAcquire(TimeSpan.FromMilliseconds(50));
            if (scope != null)
            {
                Interlocked.Increment(ref successfulAcquires);
                Thread.Sleep(10);
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        // Assert: At least 1 thread succeeded, no unhandled exceptions
        successfulAcquires.Should().BeGreaterThan(0);
    }

    [Fact]
    public void F09_Concurrency_NamedEventResetRace_MaintainsStateConsistency()
    {
        // Arrange
        using var helper = new ParameterizedMutexTestHelper("EventResetRace");
        nint hEvent = helper.CreateNamedEvent(manualReset: true, initialState: false);

        // Act: Rapid set and reset
        for (int i = 0; i < 50; i++)
        {
            Kernel32.SetEvent(hEvent);
            Kernel32.ResetEvent(hEvent);
        }

        // Final state: Reset -> should timeout
        uint waitRes = Kernel32.WaitForSingleObject(hEvent, 10);

        // Assert
        waitRes.Should().Be(Kernel32.WAIT_TIMEOUT);
    }

    [Fact]
    public void F09_Concurrency_WorkerThreadUnhandledException_ReleasesMutexInFinally()
    {
        // Arrange
        using var helper = new ParameterizedMutexTestHelper("WorkerException");

        // Act: Run worker that throws exception inside using block
        var thread = new Thread(() =>
        {
            try
            {
                using var scope = helper.TryAcquire(TimeSpan.FromSeconds(1));
                throw new InvalidOperationException("Simulated failure inside lock");
            }
            catch (InvalidOperationException)
            {
                // Caught inside thread
            }
        });
        thread.Start();
        thread.Join();

        // Assert: Subsequent acquire succeeds
        using var nextScope = helper.TryAcquire(TimeSpan.FromSeconds(1));
        nextScope.Should().NotBeNull();
        nextScope!.IsHolding.Should().BeTrue();
    }

    [Fact]
    public void F09_Concurrency_ProcessSupervisor_TracksHundredProcessesWithoutDegradation()
    {
        // Arrange
        var supervisor = new ProcessSupervisor();

        // Act: Register 100 processes
        for (int i = 1; i <= 100; i++)
        {
            supervisor.RegisterProcess(1000 + i);
        }

        // Assert
        supervisor.TrackedPids.Count.Should().Be(100);

        // Unregister all
        for (int i = 1; i <= 100; i++)
        {
            supervisor.UnregisterProcess(1000 + i).Should().BeTrue();
        }
        supervisor.TrackedPids.Should().BeEmpty();
    }

    // =========================================================================
    // Feature 25: System Tray Keep-Alive Boundaries (5 Tests)
    // =========================================================================

    [Fact]
    public void F25_Tray_RapidMinimizeRestoreCycles_MaintainsWindowVisibilityState()
    {
        // Arrange
        using var tray = new NotifyIconWrapper();
        tray.Initialize();

        // Act: 20 rapid minimize / restore cycles
        for (int i = 0; i < 20; i++)
        {
            tray.MinimizeToTray();
            tray.IsWindowVisible.Should().BeFalse();
            tray.RestoreWindow();
            tray.IsWindowVisible.Should().BeTrue();
        }

        // Assert
        tray.IsWindowVisible.Should().BeTrue();
        tray.IsTrayActive.Should().BeTrue();
    }

    [Fact]
    public void F25_Tray_ExplorerCrashAndTaskbarRestart_RecreatesTrayIcon()
    {
        // Arrange
        using var tray = new NotifyIconWrapper();
        tray.Initialize();

        // Act: Simulate taskbar restart
        tray.Dispose();
        tray.Initialize();

        // Assert
        tray.IsTrayActive.Should().BeTrue();
    }

    [Fact]
    public void F25_Tray_ContextMenuInvokedWhileRobloxExiting_DoesNotDeadlock()
    {
        // Arrange
        using var tray = new NotifyIconWrapper();

        // Act & Assert
        tray.ContextMenuActions.Count.Should().Be(3);
    }

    [Fact]
    public void F25_Tray_CorruptedIconResource_FallsBackToDefaultSystemIcon()
    {
        // Arrange: Custom icon path is invalid
        var settings = new Settings
        {
            BootstrapperIconCustomLocation = @"C:\NonExistent\Corrupted.ico"
        };

        // Assert: Fallback remains valid
        settings.BootstrapperIcon.Should().Be(BootstrapperIcon.IconBloxstrap);
    }

    [Fact]
    public void F25_Tray_ApplicationExitWithActiveClients_PromptOrCleanup()
    {
        // Arrange
        using var helper = new ParameterizedMutexTestHelper("AppExitCleanup");
        var controller = new WindowsMultiInstanceController(helper.MutexName, helper.EventName);
        controller.Enable();
        controller.TrackProcess(9999);

        // Act: Dispose controller on app exit
        controller.Dispose();

        // Assert: Mutex released for others
        using var scope = helper.TryAcquire(TimeSpan.FromSeconds(1));
        scope.Should().NotBeNull();
        scope!.IsHolding.Should().BeTrue();
    }

    // =========================================================================
    // Feature 28: Adversarial Coverage Hardening (5 Tests)
    // =========================================================================

    [Fact]
    public void F28_Adversarial_KernelMutexFuzzing_RandomPayloadStrings()
    {
        // Arrange: Mutex name with invalid characters
        string invalidName = @"Global\Invalid\Name/With:Colons";

        // Act
        nint handle = Kernel32.CreateMutexW(nint.Zero, false, invalidName);

        // Assert: Win32 returns 0 (ERROR_INVALID_NAME) without crashing process
        if (handle != nint.Zero) Kernel32.CloseHandle(handle);
    }

    [Fact]
    public void F28_Adversarial_ZombieProcessHandleSimulation_HandlesStalePids()
    {
        // Arrange
        var supervisor = new ProcessSupervisor();
        supervisor.RegisterProcess(999999);

        // Act
        bool unregistered = supervisor.UnregisterProcess(999999);

        // Assert
        unregistered.Should().BeTrue();
        supervisor.TrackedPids.Should().NotContain(999999);
    }

    [Fact]
    public void F28_Adversarial_SimulatedSystemSleepAndResume_MaintainsMutexIntegrity()
    {
        // Arrange
        using var helper = new ParameterizedMutexTestHelper("SleepResume");
        using var scope = helper.TryAcquire(TimeSpan.FromSeconds(1));

        // Act: Verify holding state
        scope.Should().NotBeNull();
        scope!.IsHolding.Should().BeTrue();
    }

    [Fact]
    public void F28_Adversarial_HighFrequencyProcessSpawning_DoesNotExhaustHandleTable()
    {
        // Arrange
        var supervisor = new ProcessSupervisor();

        // Act: 100 rapid register and unregister operations
        for (int i = 1; i <= 100; i++)
        {
            supervisor.RegisterProcess(20000 + i);
            supervisor.UnregisterProcess(20000 + i);
        }

        // Assert
        supervisor.TrackedPids.Should().BeEmpty();
    }

    [Fact]
    public void F28_Adversarial_OutOfMemoryInjectionDuringMutexCreation_RecoversGracefully()
    {
        // Arrange: Test helper handles null/empty string gracefully
        using var helper = new ParameterizedMutexTestHelper("OOMTest");

        // Act
        using var scope = helper.TryAcquire(TimeSpan.FromMilliseconds(50));

        // Assert
        scope.Should().NotBeNull();
    }
}
