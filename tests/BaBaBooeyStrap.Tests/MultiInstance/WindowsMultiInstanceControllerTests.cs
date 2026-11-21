using System.Diagnostics;
using BaBaBooeyStrap.Core.Interop;
using BaBaBooeyStrap.MultiInstance;
using BaBaBooeyStrap.MultiInstance.Abstractions;
using BaBaBooeyStrap.Tests.Mocks;

namespace BaBaBooeyStrap.Tests.MultiInstance;

public sealed class WindowsMultiInstanceControllerTests
{
    [Fact]
    public void InitialState_ReportsNotEnabledAndNoMutexOwnership()
    {
        using var helper = new ParameterizedMutexTestHelper("InitialState");
        using var controller = new WindowsMultiInstanceController(helper.MutexName, helper.EventName);

        var status = controller.GetStatus();

        status.Enabled.Should().BeFalse();
        status.OwnsMutex.Should().BeFalse();
        status.WaitingForMutex.Should().BeFalse();
        status.LastError.Should().BeNull();
    }

    [Fact]
    public void Enable_AcquiresMutexAndEventOnDedicatedStaThread()
    {
        using var helper = new ParameterizedMutexTestHelper("EnableSTA");
        using var controller = new WindowsMultiInstanceController(helper.MutexName, helper.EventName);

        var status = controller.Enable();

        status.Enabled.Should().BeTrue();
        status.OwnsMutex.Should().BeTrue();
        status.WaitingForMutex.Should().BeFalse();
        status.LastError.Should().BeNull();

        // Verify the event exists and remains unsignaled (signaling ROBLOX_singletonEvent causes Roblox to terminate)
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
    public void Disable_ReleasesMutexAndAllowsExternalAcquisition()
    {
        using var helper = new ParameterizedMutexTestHelper("DisableRelease");
        using var controller = new WindowsMultiInstanceController(helper.MutexName, helper.EventName);

        controller.Enable();
        controller.GetStatus().OwnsMutex.Should().BeTrue();

        var disabledStatus = controller.Disable();

        disabledStatus.Enabled.Should().BeFalse();
        disabledStatus.OwnsMutex.Should().BeFalse();

        // Independent scope should be able to acquire immediately
        using var externalScope = helper.TryAcquire(TimeSpan.FromSeconds(1));
        externalScope.Should().NotBeNull();
        externalScope!.IsHolding.Should().BeTrue();
    }

    [Fact]
    public void ReentrantEnableDisable_TogglesCleanlyWithoutDeadlock()
    {
        using var helper = new ParameterizedMutexTestHelper("ReentrantToggle");
        using var controller = new WindowsMultiInstanceController(helper.MutexName, helper.EventName);

        for (int i = 0; i < 10; i++)
        {
            var enabledStatus = controller.Enable();
            enabledStatus.OwnsMutex.Should().BeTrue();

            var disabledStatus = controller.Disable();
            disabledStatus.OwnsMutex.Should().BeFalse();
        }

        controller.GetStatus().Enabled.Should().BeFalse();
    }

    [Fact]
    public void ContendedMutex_WaitsInBackgroundAndAcquiresWhenReleased()
    {
        using var helper = new ParameterizedMutexTestHelper("ContendedWait");
        var externalHolder = helper.HoldMutexExternally();
        externalHolder.IsHolding.Should().BeTrue();

        using var controller = new WindowsMultiInstanceController(helper.MutexName, helper.EventName);

        // Enable should return within 500ms indicating WaitingForMutex
        var waitingStatus = controller.Enable();
        waitingStatus.Enabled.Should().BeTrue();
        waitingStatus.OwnsMutex.Should().BeFalse();
        waitingStatus.WaitingForMutex.Should().BeTrue();

        // Release external lock
        externalHolder.Release();

        // Controller background worker should acquire
        bool acquired = SpinWait.SpinUntil(() => controller.GetStatus().OwnsMutex, TimeSpan.FromSeconds(2));
        acquired.Should().BeTrue();

        var finalStatus = controller.GetStatus();
        finalStatus.OwnsMutex.Should().BeTrue();
        finalStatus.WaitingForMutex.Should().BeFalse();
    }

    [Fact]
    public void AbandonedMutex_RecoversOwnershipSeamlessly()
    {
        using var helper = new ParameterizedMutexTestHelper("AbandonedRecovery");

        // Simulate external process holding mutex and dying abruptly
        helper.SimulateAbandonedMutex();

        using var controller = new WindowsMultiInstanceController(helper.MutexName, helper.EventName);
        var status = controller.Enable();

        status.Enabled.Should().BeTrue();
        status.OwnsMutex.Should().BeTrue();
        status.WaitingForMutex.Should().BeFalse();
    }

    [Fact]
    public void Dispose_ShutsDownThreadAndReleasesHandlesCleanly()
    {
        using var helper = new ParameterizedMutexTestHelper("CleanDispose");
        var controller = new WindowsMultiInstanceController(helper.MutexName, helper.EventName);

        controller.Enable();
        controller.GetStatus().OwnsMutex.Should().BeTrue();

        controller.Dispose();

        // Subsequent external acquisition succeeds without hanging or leaking
        using var scope = helper.TryAcquire(TimeSpan.FromSeconds(1));
        scope.Should().NotBeNull();
        scope!.IsHolding.Should().BeTrue();

        // Disable after Dispose is safe no-op
        var disabled = controller.Disable();
        disabled.Enabled.Should().BeFalse();

        // Enable after Dispose throws ObjectDisposedException
        Action act = () => controller.Enable();
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void Constructor_SafeFallbacksOnNullOrWhitespaceNames()
    {
        using var c1 = new WindowsMultiInstanceController(null, null);
        using var c2 = new WindowsMultiInstanceController("", "   ");

        c1.GetStatus().Should().NotBeNull();
        c2.GetStatus().Should().NotBeNull();
    }

    [Fact]
    public void ProcessTracking_TracksAndUntracksManualPidsSorted()
    {
        using var helper = new ParameterizedMutexTestHelper("PidTracking");
        using var controller = new WindowsMultiInstanceController(helper.MutexName, helper.EventName);

        controller.TrackProcess(4000);
        controller.TrackProcess(2000);
        controller.TrackProcess(3000);

        var status = controller.GetStatus();
        status.RunningRobloxPids.Should().Equal(new[] { 2000, 3000, 4000 });

        controller.UntrackProcess(3000);
        controller.GetStatus().RunningRobloxPids.Should().Equal(new[] { 2000, 4000 });
    }

    [Fact]
    public void ProcessEnumerator_CustomDelegateInjectsSortedProcesses()
    {
        using var helper = new ParameterizedMutexTestHelper("CustomEnumerator");
        using var controller = new WindowsMultiInstanceController(
            helper.MutexName,
            helper.EventName,
            processEnumerator: () => new[] { 9999, 1111, 5555 });

        var status = controller.GetStatus();
        status.RunningRobloxPids.Should().Equal(new[] { 1111, 5555, 9999 });
    }

    [Fact]
    public async Task ConcurrentEnableDisable_DoesNotOrphanWorkerThreadOrLeakMutex()
    {
        using var helper = new ParameterizedMutexTestHelper("RapidChurn");
        using var controller = new WindowsMultiInstanceController(helper.MutexName, helper.EventName);

        var workerThreadField = typeof(WindowsMultiInstanceController)
            .GetField("_workerThread", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        for (int i = 0; i < 50; i++)
        {
            var t1 = Task.Run(() => controller.Enable());
            var t2 = Task.Run(() => controller.Disable());
            await Task.WhenAll(t1, t2);

            var status = controller.GetStatus();
            var thread = (Thread?)workerThreadField.GetValue(controller);

            if (status.Enabled)
            {
                thread.Should().NotBeNull($"worker thread must not be null when Enabled=true at iteration {i}");
                thread!.IsAlive.Should().BeTrue($"worker thread must be alive when Enabled=true at iteration {i}");
            }
        }

        // Final disable to ensure clean shutdown
        controller.Disable();
        var finalStatus = controller.GetStatus();
        finalStatus.Enabled.Should().BeFalse();
        finalStatus.OwnsMutex.Should().BeFalse();

        var finalThread = (Thread?)workerThreadField.GetValue(controller);
        finalThread.Should().BeNull("worker thread must be null after clean Disable");

        // Independent scope must acquire immediately with no orphaned background thread holding mutex
        using var externalScope = helper.TryAcquire(TimeSpan.FromSeconds(2));
        externalScope.Should().NotBeNull("external acquisition must succeed, proving no orphaned thread holds mutex");
        externalScope!.IsHolding.Should().BeTrue();
        externalScope.Release();

        // Controller can re-enable cleanly post-churn
        var reEnabled = controller.Enable();
        reEnabled.Enabled.Should().BeTrue();
        reEnabled.OwnsMutex.Should().BeTrue();

        controller.Disable();
    }

    [Fact]
    public async Task MutexHandover_StrictMutualExclusion_NoDualOwnership()
    {
        using var helper = new ParameterizedMutexTestHelper("HandoverMutex");
        using var c1 = new WindowsMultiInstanceController(helper.MutexName, helper.EventName);
        using var c2 = new WindowsMultiInstanceController(helper.MutexName, helper.EventName);

        c1.Enable();
        c1.GetStatus().OwnsMutex.Should().BeTrue();

        c2.Enable();
        c2.GetStatus().WaitingForMutex.Should().BeTrue();

        int dualOwnershipViolations = 0;
        using var cts = new CancellationTokenSource();

        var sampler = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                var s1 = c1.GetStatus();
                var s2 = c2.GetStatus();
                var s1Check = c1.GetStatus();

                // If s1 changed between before and after reading s2, the read of (s1, s2) was torn across a handover.
                // Dual ownership is confirmed only if s1 was owned both before and after reading s2.
                if (s1.OwnsMutex && s2.OwnsMutex && s1Check.OwnsMutex)
                {
                    Interlocked.Increment(ref dualOwnershipViolations);
                }
            }
        });

        // Alternate ownership back and forth across 10 rounds
        for (int round = 0; round < 10; round++)
        {
            c1.Disable();
            bool c2Acquired = SpinWait.SpinUntil(() => c2.GetStatus().OwnsMutex, TimeSpan.FromSeconds(2));
            c2Acquired.Should().BeTrue($"Controller 2 should acquire mutex when Controller 1 disables in round {round}");

            c1.Enable(); // c1 is now waiting

            c2.Disable();
            bool c1Acquired = SpinWait.SpinUntil(() => c1.GetStatus().OwnsMutex, TimeSpan.FromSeconds(2));
            c1Acquired.Should().BeTrue($"Controller 1 should acquire mutex when Controller 2 disables in round {round}");

            c2.Enable(); // c2 is now waiting
        }

        cts.Cancel();
        await sampler;

        dualOwnershipViolations.Should().Be(0, "Dual mutex ownership must never occur during handover!");

        c1.Disable();
        c2.Disable();
    }

    [Fact]
    public async Task ConcurrentDynamicContention_MultipleControllers_MaintainsAtMostOneOwner()
    {
        using var helper = new ParameterizedMutexTestHelper("DynamicContention");
        var controllers = new List<WindowsMultiInstanceController>();
        for (int i = 0; i < 4; i++)
        {
            controllers.Add(new WindowsMultiInstanceController(helper.MutexName, helper.EventName));
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            int multiOwnerViolations = 0;

            var monitor = Task.Run(() =>
            {
                while (!cts.IsCancellationRequested)
                {
                    var snapshot = controllers.Select(c => c.GetStatus().OwnsMutex).ToArray();
                    var trueIndices = snapshot.Select((val, idx) => (val, idx)).Where(x => x.val).Select(x => x.idx).ToList();
                    if (trueIndices.Count > 1)
                    {
                        // Verify non-torn read: confirm that the multiple controllers truly overlap in ownership
                        bool verifiedOverlap = trueIndices.All(idx => controllers[idx].GetStatus().OwnsMutex);
                        if (verifiedOverlap)
                        {
                            Interlocked.Increment(ref multiOwnerViolations);
                        }
                    }
                }
            });

            var workerTasks = controllers.Select(ctrl => Task.Run(() =>
            {
                while (!cts.IsCancellationRequested)
                {
                    ctrl.Enable();
                    Thread.Sleep(5);
                    ctrl.Disable();
                    Thread.Sleep(5);
                }
            })).ToArray();

            await Task.WhenAll(workerTasks);
            await monitor;

            multiOwnerViolations.Should().Be(0, "At no point may more than 1 controller report OwnsMutex=true!");
        }
        finally
        {
            foreach (var ctrl in controllers)
            {
                ctrl.Disable();
                ctrl.Dispose();
            }
        }

        // Mutex is cleanly released to external scope
        using var externalScope = helper.TryAcquire(TimeSpan.FromSeconds(2));
        externalScope.Should().NotBeNull();
        externalScope!.IsHolding.Should().BeTrue();
    }
}
