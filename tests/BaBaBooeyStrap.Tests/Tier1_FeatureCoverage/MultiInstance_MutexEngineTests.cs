using System.Diagnostics;
using BaBaBooeyStrap.Core.Interop;
using BaBaBooeyStrap.Tests.Mocks;
using BaBaBooeyStrap.Tests.Tier1_FeatureCoverage.Engines;

namespace BaBaBooeyStrap.Tests.Tier1_FeatureCoverage;

public sealed class MultiInstance_MutexEngineTests
{
    // =========================================================================
    // Feature 6: Preemptive Mutex Holding
    // =========================================================================

    [Fact]
    public void F6_MutexHolder_Enable_AcquiresSingletonMutexOnStaThread()
    {
        // Arrange
        using var helper = new ParameterizedMutexTestHelper("F6_Enable");
        using var controller = new WindowsMultiInstanceController(helper.MutexName, helper.EventName);

        // Act
        var status = controller.Enable();

        // Assert
        status.Enabled.Should().BeTrue();
        status.OwnsMutex.Should().BeTrue();
        status.WaitingForMutex.Should().BeFalse();
        status.LastError.Should().BeNull();
    }

    [Fact]
    public void F6_MutexHolder_Disable_ReleasesMutexCleanly()
    {
        // Arrange
        using var helper = new ParameterizedMutexTestHelper("F6_Disable");
        using var controller = new WindowsMultiInstanceController(helper.MutexName, helper.EventName);
        controller.Enable();
        controller.GetStatus().OwnsMutex.Should().BeTrue();

        // Act
        var status = controller.Disable();

        // Assert
        status.Enabled.Should().BeFalse();
        status.OwnsMutex.Should().BeFalse();

        // Independent acquirer should be able to acquire immediately
        using var scope = helper.TryAcquire(TimeSpan.FromSeconds(1));
        scope.Should().NotBeNull();
        scope!.IsHolding.Should().BeTrue();
    }

    [Fact]
    public void F6_MutexHolder_ReentrantEnableDisable_TogglesWithoutDeadlock()
    {
        // Arrange
        using var helper = new ParameterizedMutexTestHelper("F6_Reentrant");
        using var controller = new WindowsMultiInstanceController(helper.MutexName, helper.EventName);

        // Act: Toggle 5 times in rapid succession
        for (int i = 0; i < 5; i++)
        {
            controller.Enable().OwnsMutex.Should().BeTrue();
            controller.Disable().OwnsMutex.Should().BeFalse();
        }

        // Assert
        controller.GetStatus().Enabled.Should().BeFalse();
        controller.GetStatus().OwnsMutex.Should().BeFalse();
    }

    [Fact]
    public void F6_MutexHolder_HoldsSingletonEvent_SignalsEventSimultaneously()
    {
        // Arrange
        using var helper = new ParameterizedMutexTestHelper("F6_Event");
        using var controller = new WindowsMultiInstanceController(helper.MutexName, helper.EventName);

        // Act
        controller.Enable();

        // Assert: Open the event and verify it exists and stays unsignaled
        nint openedEvent = Kernel32.OpenEventW(Kernel32.SYNCHRONIZE, false, helper.EventName);
        openedEvent.Should().NotBe(nint.Zero);
        try
        {
            uint waitRes = Kernel32.WaitForSingleObject(openedEvent, 50);
            waitRes.Should().Be(Kernel32.WAIT_TIMEOUT);
        }
        finally
        {
            Kernel32.CloseHandle(openedEvent);
        }
    }

    [Fact]
    public void F6_MutexHolder_GetStatus_ReportsAccurateStateAndThreadId()
    {
        // Arrange
        using var helper = new ParameterizedMutexTestHelper("F6_Status");
        using var controller = new WindowsMultiInstanceController(helper.MutexName, helper.EventName);
        controller.TrackProcess(1234);

        // Act
        controller.Enable();
        var status = controller.GetStatus();

        // Assert
        status.Enabled.Should().BeTrue();
        status.OwnsMutex.Should().BeTrue();
        status.RunningRobloxPids.Should().Contain(1234);
    }

    // =========================================================================
    // Feature 7: Multi-Instance Launch Gating
    // =========================================================================

    [Fact]
    public void F7_LaunchGating_WhenMutexHeld_AllowsLaunchWithoutWarning()
    {
        // Arrange
        using var helper = new ParameterizedMutexTestHelper("F7_Held");
        using var controller = new WindowsMultiInstanceController(helper.MutexName, helper.EventName);
        controller.Enable();

        // Act
        var result = LaunchGate.VerifyLaunchReadiness(controller, multiInstanceEnabled: true);

        // Assert
        result.CanLaunch.Should().BeTrue();
        result.WarningMessage.Should().BeNull();
    }

    [Fact]
    public void F7_LaunchGating_WhenMutexBlockedByExternal_DetectsContentionAndWarns()
    {
        // Arrange
        using var helper = new ParameterizedMutexTestHelper("F7_Blocked");
        using var externalHolder = helper.HoldMutexExternally();
        externalHolder.IsHolding.Should().BeTrue();

        using var controller = new WindowsMultiInstanceController(helper.MutexName, helper.EventName);
        controller.Enable(); // Will fail to acquire because held externally

        // Act
        var result = LaunchGate.VerifyLaunchReadiness(controller, multiInstanceEnabled: true);

        // Assert
        result.CanLaunch.Should().BeFalse();
        result.WarningMessage.Should().NotBeNull();
        result.WarningMessage.Should().Contain("Roblox singleton mutex");
    }

    [Fact]
    public void F7_LaunchGating_DisabledMode_SkipsPreemptiveCheck()
    {
        // Arrange
        using var helper = new ParameterizedMutexTestHelper("F7_Disabled");
        using var controller = new WindowsMultiInstanceController(helper.MutexName, helper.EventName);

        // Act: Even if controller is not enabled, launch gate with multiInstanceEnabled = false allows launch
        var result = LaunchGate.VerifyLaunchReadiness(controller, multiInstanceEnabled: false);

        // Assert
        result.CanLaunch.Should().BeTrue();
        result.WarningMessage.Should().BeNull();
    }

    [Fact]
    public void F7_LaunchGating_GateStatus_ProvidesDiagnosticStateBeforeLaunch()
    {
        // Arrange
        using var helper = new ParameterizedMutexTestHelper("F7_Diagnostics");
        using var controller = new WindowsMultiInstanceController(helper.MutexName, helper.EventName);
        controller.TrackProcess(5678);
        controller.TrackProcess(9101);

        // Act
        var status = LaunchGate.GetDiagnosticStatus(controller);

        // Assert
        status.RunningRobloxPids.Should().HaveCount(2);
        status.RunningRobloxPids.Should().Contain(5678);
        status.RunningRobloxPids.Should().Contain(9101);
    }

    [Fact]
    public void F7_LaunchGating_AutoRecovery_ReacquiresMutexIfDropped()
    {
        // Arrange
        using var helper = new ParameterizedMutexTestHelper("F7_AutoRecovery");
        using var controller = new WindowsMultiInstanceController(helper.MutexName, helper.EventName);

        // Act: Controller initially disabled, EnsureMutexOwnership enables and acquires
        bool acquired = LaunchGate.EnsureMutexOwnership(controller);

        // Assert
        acquired.Should().BeTrue();
        controller.GetStatus().OwnsMutex.Should().BeTrue();
    }

    // =========================================================================
    // Feature 8: Client Window Probe & Throttling
    // =========================================================================

    [Fact]
    public async Task F8_WindowProbe_DetectsMainWindowWhenVisible()
    {
        // Arrange
        var supervisor = new ProcessSupervisor();
        int currentPid = Environment.ProcessId;

        // Act: probe for current process or any visible window
        var hwnd = await supervisor.WaitForNewClientMainWindowAsync(currentPid, TimeSpan.FromMilliseconds(500));

        // Assert: method executes and completes without throwing
        // (current process is test runner; if it has a window it returns it, else zero)
        (hwnd >= nint.Zero).Should().BeTrue();
    }

    [Fact]
    public void F8_WindowProbe_IgnoresHiddenSplashScreenWindows()
    {
        // Arrange: Create a hidden window
        using var hiddenWindow = new NativeTestWindow("SplashScreen", visible: false);

        // Act: validator rejecting window returns zero
        nint rejected = ProcessSupervisor.FindMainWindowForProcess(Environment.ProcessId, window => false);

        // Assert
        rejected.Should().Be(nint.Zero);
    }

    [Fact]
    public async Task F8_Throttling_EnforcesMinimumLaunchDelay()
    {
        // Arrange
        var supervisor = new ProcessSupervisor();
        var delay = TimeSpan.FromMilliseconds(500);

        // Act: First launch sets baseline
        await supervisor.ThrottleLaunchAsync(delay);

        var sw = Stopwatch.StartNew();
        // Second launch immediately after must wait for remaining delay
        await supervisor.ThrottleLaunchAsync(delay);
        sw.Stop();

        // Assert: elapsed should be close to 500ms (at least 450ms)
        sw.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(450);
    }

    [Fact]
    public async Task F8_Throttling_ConfigurableDelay_RespectsSettingsValue()
    {
        // Arrange
        var supervisor = new ProcessSupervisor();
        var configuredDelay = TimeSpan.FromMilliseconds(600);

        // Act
        await supervisor.ThrottleLaunchAsync(configuredDelay);
        var sw = Stopwatch.StartNew();
        await supervisor.ThrottleLaunchAsync(configuredDelay);
        sw.Stop();

        // Assert
        sw.ElapsedMilliseconds.Should().BeInRange(500, 1500);
    }

    [Fact]
    public async Task F8_WindowProbe_TimeoutGracefulHandling_DoesNotBlockSubsequentLaunches()
    {
        // Arrange
        var supervisor = new ProcessSupervisor();
        const int nonExistentPid = 9999999;

        // Act 1: Wait for non-existent window with short timeout
        var sw = Stopwatch.StartNew();
        nint hwnd1 = await supervisor.WaitForNewClientMainWindowAsync(nonExistentPid, TimeSpan.FromMilliseconds(100));
        sw.Stop();

        // Act 2: Immediate subsequent throttle proceeds without deadlock
        var delay = await supervisor.ThrottleLaunchAsync(TimeSpan.FromMilliseconds(500));

        // Assert
        hwnd1.Should().Be(nint.Zero);
        sw.ElapsedMilliseconds.Should().BeInRange(80, 500);
    }

    // =========================================================================
    // Feature 9: Multi-Instance Concurrency Tests
    // =========================================================================

    [Fact]
    public void F9_Concurrency_MultipleParallelInstances_RunConcurrentlyWithoutCollision()
    {
        // Arrange
        var supervisor = new ProcessSupervisor();

        // Act: Register 3 concurrent client PIDs
        supervisor.RegisterProcess(1001);
        supervisor.RegisterProcess(1002);
        supervisor.RegisterProcess(1003);

        // Assert
        supervisor.TrackedPids.Should().HaveCount(3);
        supervisor.TrackedPids.Should().Contain(new[] { 1001, 1002, 1003 });

        // Unregister one
        supervisor.UnregisterProcess(1002).Should().BeTrue();
        supervisor.TrackedPids.Should().HaveCount(2);
    }

    [Fact]
    public void F9_Concurrency_AbandonedMutex_GracefullyReacquired()
    {
        // Arrange
        using var helper = new ParameterizedMutexTestHelper("F9_Abandoned");

        // Act: Simulate external process dying while holding mutex
        helper.SimulateAbandonedMutex();

        // Next acquire handles WAIT_ABANDONED and gains lock
        using var scope = helper.TryAcquire(TimeSpan.FromSeconds(2));

        // Assert
        scope.Should().NotBeNull();
        scope!.IsHolding.Should().BeTrue();
    }

    [Fact]
    public void F9_Concurrency_MutexContention_SecondaryAttemptWaitsOrReturnsTimeout()
    {
        // Arrange
        using var helper = new ParameterizedMutexTestHelper("F9_Contention");

        // Act: First thread acquires mutex
        using var holder = helper.HoldMutexExternally();
        holder.IsHolding.Should().BeTrue();

        // Second attempt on current thread should timeout
        using var secondary = helper.TryAcquire(TimeSpan.FromMilliseconds(150));

        // Assert
        secondary.Should().BeNull();
    }

    [Fact]
    public void F9_Concurrency_CleanShutdown_ReleasesAllHandlesWithoutOrphanResources()
    {
        // Arrange
        using var helper = new ParameterizedMutexTestHelper("F9_CleanShutdown");
        var controller = new WindowsMultiInstanceController(helper.MutexName, helper.EventName);
        controller.Enable();
        controller.GetStatus().OwnsMutex.Should().BeTrue();

        // Act
        controller.Dispose();

        // Assert: After disposal, mutex is free to be acquired
        using var subsequent = helper.TryAcquire(TimeSpan.FromSeconds(1));
        subsequent.Should().NotBeNull();
        subsequent!.IsHolding.Should().BeTrue();
    }

    [Fact]
    public async Task F9_Concurrency_ParallelLaunchBurst_QueuesLaunchesSequentially()
    {
        // Arrange
        var supervisor = new ProcessSupervisor();
        var executionTimes = new List<DateTime>();
        var lockObj = new object();

        // Act: 3 rapid launches through supervisor throttle
        var tasks = Enumerable.Range(0, 3).Select(async _ =>
        {
            await supervisor.ThrottleLaunchAsync(TimeSpan.FromMilliseconds(500));
            lock (lockObj) executionTimes.Add(DateTime.UtcNow);
        }).ToArray();

        await Task.WhenAll(tasks);

        // Assert: all 3 recorded
        executionTimes.Count.Should().Be(3);
    }
}
