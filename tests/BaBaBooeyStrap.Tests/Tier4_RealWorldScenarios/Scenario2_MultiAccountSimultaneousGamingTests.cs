using System.Diagnostics;
using BaBaBooeyStrap.Core.Interop;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Storage;
using BaBaBooeyStrap.MultiInstance;
using BaBaBooeyStrap.MultiInstance.Abstractions;
using BaBaBooeyStrap.Tests.Mocks;
using BaBaBooeyStrap.Tests.Tier1_FeatureCoverage.Engines;
using FluentAssertions;
using Xunit;

namespace BaBaBooeyStrap.Tests.Tier4_RealWorldScenarios;

public sealed class Scenario2_MultiAccountSimultaneousGamingTests
{
    [Fact]
    public async Task Scenario2_ThreeAccountsSequentialLaunch_Enforces1500msThrottleAndMaintainsMutexOwnership()
    {
        // 1. Arrange DPAPI Account Store with 3 accounts
        using var env = new MockRobloxEnvironment();
        string accountDb = Path.Combine(env.BaBaBooeyStrapPath, "accounts.dat");
        var store = new DpapiAccountStore(accountDb);

        var acc1 = new RobloxAccount { UserId = 10001, Username = "MainAccount", Cookie = "cookie_secret_1" };
        var acc2 = new RobloxAccount { UserId = 10002, Username = "AltAccount1", Cookie = "cookie_secret_2" };
        var acc3 = new RobloxAccount { UserId = 10003, Username = "AltAccount2", Cookie = "cookie_secret_3" };

        store.Upsert(acc1);
        store.Upsert(acc2);
        store.Upsert(acc3);

        var loadedAccounts = store.LoadAll();
        loadedAccounts.Count.Should().Be(3);

        // Verify per-account DPAPI cookie isolation & absence of plaintext secrets in accounts.dat
        loadedAccounts.Single(a => a.UserId == 10001).Cookie.Should().Be("cookie_secret_1");
        loadedAccounts.Single(a => a.UserId == 10002).Cookie.Should().Be("cookie_secret_2");
        loadedAccounts.Single(a => a.UserId == 10003).Cookie.Should().Be("cookie_secret_3");

        byte[] rawEncryptedBytes = File.ReadAllBytes(accountDb);
        string rawAsText = System.Text.Encoding.UTF8.GetString(rawEncryptedBytes);
        rawAsText.Should().NotContain("cookie_secret_1");
        rawAsText.Should().NotContain("cookie_secret_2");
        rawAsText.Should().NotContain("cookie_secret_3");
        rawAsText.Should().NotContain("MainAccount");

        // 2. Preemptive Mutex Initialization (F6, F9)
        using var mutexHelper = new ParameterizedMutexTestHelper("Tier4_MultiAcct");
        var activePids = new List<int>();
        using var controller = new WindowsMultiInstanceController(mutexHelper.MutexName, mutexHelper.EventName, () => activePids);
        var status = controller.Enable();
        status.OwnsMutex.Should().BeTrue();

        // 3. Launch Gate Verification (F7)
        var gateResult = LaunchGate.VerifyLaunchReadiness(controller, multiInstanceEnabled: true);
        gateResult.CanLaunch.Should().BeTrue();

        // 4. Process Supervisor & Window Probing Setup (F8)
        using var win1 = new NativeTestWindow("Roblox_Account_10001", visible: true);
        using var win2 = new NativeTestWindow("Roblox_Account_10002", visible: true);
        using var win3 = new NativeTestWindow("Roblox_Account_10003", visible: true);

        var simulatedWindows = new List<ClientWindowInfo>
        {
            new(5001, win1.Handle, true),
            new(5002, win2.Handle, true),
            new(5003, win3.Handle, true)
        };

        var supervisor = new ProcessSupervisor(() => simulatedWindows);

        // 5. Account 1 Launch
        await supervisor.ThrottleLaunchAsync();
        activePids.Add(5001);
        controller.TrackProcess(5001);
        var hwnd1 = await supervisor.WaitForNewClientMainWindowAsync(5001, TimeSpan.FromSeconds(2));
        hwnd1.Should().Be(win1.Handle);

        // 6. Account 2 Launch with default 1500ms launch throttle verification
        var sw = Stopwatch.StartNew();
        var delay2 = await supervisor.ThrottleLaunchAsync();
        activePids.Add(5002);
        controller.TrackProcess(5002);
        var hwnd2 = await supervisor.WaitForNewClientMainWindowAsync(5002, TimeSpan.FromSeconds(2));
        hwnd2.Should().Be(win2.Handle);
        delay2.TotalMilliseconds.Should().BeGreaterThanOrEqualTo(1400);
        sw.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(1400);

        // 7. Account 3 Launch with default 1500ms launch throttle
        var sw3 = Stopwatch.StartNew();
        var delay3 = await supervisor.ThrottleLaunchAsync();
        activePids.Add(5003);
        controller.TrackProcess(5003);
        var hwnd3 = await supervisor.WaitForNewClientMainWindowAsync(5003, TimeSpan.FromSeconds(2));
        hwnd3.Should().Be(win3.Handle);
        delay3.TotalMilliseconds.Should().BeGreaterThanOrEqualTo(1400);
        sw3.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(1400);

        // 8. Assert All 3 Running Concurrently with Mutex Intact
        var finalStatus = controller.GetStatus();
        finalStatus.RunningRobloxPids.Should().BeEquivalentTo(new[] { 5001, 5002, 5003 });
        finalStatus.OwnsMutex.Should().BeTrue();

        // 9. Teardown & Clean Release
        controller.Disable();
        controller.GetStatus().OwnsMutex.Should().BeFalse();
    }

    [Fact]
    public async Task Scenario2_SimultaneousLaunchContention_LaunchGateBlocksWhenMutexNotOwnedUntilAcquired()
    {
        using var mutexHelper = new ParameterizedMutexTestHelper("Tier4_Contention");

        // 1. External process acquires and holds the mutex
        using var externalHold = mutexHelper.HoldMutexExternally();
        externalHold.IsHolding.Should().BeTrue();

        // 2. Controller tries to enable while mutex is contended
        using var controller = new WindowsMultiInstanceController(mutexHelper.MutexName, mutexHelper.EventName, () => []);
        controller.Enable();

        var initialStatus = controller.GetStatus();
        initialStatus.OwnsMutex.Should().BeFalse();
        initialStatus.WaitingForMutex.Should().BeTrue();

        // LaunchGate must block launch while mutex is not owned
        var gateBefore = LaunchGate.VerifyLaunchReadiness(controller, multiInstanceEnabled: true);
        gateBefore.CanLaunch.Should().BeFalse();

        // 3. External process releases mutex
        externalHold.Dispose();

        // 4. Controller background thread automatically acquires mutex
        bool acquired = SpinWait.SpinUntil(() => controller.GetStatus().OwnsMutex, TimeSpan.FromSeconds(3));
        acquired.Should().BeTrue();

        var gateAfter = LaunchGate.VerifyLaunchReadiness(controller, multiInstanceEnabled: true);
        gateAfter.CanLaunch.Should().BeTrue();

        controller.Disable();
    }

    [Fact]
    public void Scenario2_ProcessExitAndUntrack_MaintainsMutexUntilExplicitControllerDisable()
    {
        using var mutexHelper = new ParameterizedMutexTestHelper("Tier4_ProcessExit");
        var activePids = new List<int> { 6001, 6002, 6003 };
        using var controller = new WindowsMultiInstanceController(mutexHelper.MutexName, mutexHelper.EventName, () => activePids);

        controller.Enable();
        controller.TrackProcess(6001);
        controller.TrackProcess(6002);
        controller.TrackProcess(6003);

        var supervisor = new ProcessSupervisor();
        supervisor.RegisterProcess(6001);
        supervisor.RegisterProcess(6002);
        supervisor.RegisterProcess(6003);

        controller.GetStatus().OwnsMutex.Should().BeTrue();
        controller.GetStatus().RunningRobloxPids.Should().HaveCount(3);

        // Account 1 (PID 6001) terminates
        activePids.Remove(6001);
        controller.UntrackProcess(6001);
        supervisor.UnregisterProcess(6001);

        // Mutex MUST remain owned even though process count changed
        var statusAfterOneExit = controller.GetStatus();
        statusAfterOneExit.OwnsMutex.Should().BeTrue();
        statusAfterOneExit.RunningRobloxPids.Should().BeEquivalentTo(new[] { 6002, 6003 });

        // Account 2 and Account 3 terminate
        activePids.Clear();
        controller.UntrackProcess(6002);
        controller.UntrackProcess(6003);
        supervisor.ClearTrackedProcesses();

        // Mutex STILL remains owned waiting for subsequent launches
        var statusAllExited = controller.GetStatus();
        statusAllExited.OwnsMutex.Should().BeTrue();
        statusAllExited.RunningRobloxPids.Should().BeEmpty();

        // Only explicit disable releases mutex
        controller.Disable();
        controller.GetStatus().OwnsMutex.Should().BeFalse();
    }

    [Fact]
    public async Task Scenario2_WindowProbeDiscovery_TracksMainWindowHandlesForMultipleConcurrentPids()
    {
        using var winA = new NativeTestWindow("RobloxGameWindow_Alpha", visible: true);
        using var winB = new NativeTestWindow("RobloxGameWindow_Beta", visible: true);

        var windowList = new List<ClientWindowInfo>
        {
            new(7001, winA.Handle, true),
            new(7002, winB.Handle, true)
        };

        var supervisor = new ProcessSupervisor(() => windowList);

        var hwndA = await supervisor.WaitForNewClientMainWindowAsync(7001, TimeSpan.FromSeconds(2));
        var hwndB = await supervisor.WaitForNewClientMainWindowAsync(7002, TimeSpan.FromSeconds(2));

        hwndA.Should().Be(winA.Handle);
        hwndB.Should().Be(winB.Handle);

        hwndA.Should().NotBe(nint.Zero);
        hwndB.Should().NotBe(nint.Zero);
        User32.IsWindowVisible(hwndA).Should().BeTrue();
        User32.IsWindowVisible(hwndB).Should().BeTrue();
    }
}
