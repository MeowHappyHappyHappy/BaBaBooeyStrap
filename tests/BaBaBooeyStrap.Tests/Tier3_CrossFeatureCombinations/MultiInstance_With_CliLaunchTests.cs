using System.Diagnostics;
using BaBaBooeyStrap.Core.Interop;
using BaBaBooeyStrap.Core.Models.Enums;
using BaBaBooeyStrap.MultiInstance;
using BaBaBooeyStrap.MultiInstance.Abstractions;
using BaBaBooeyStrap.Tests.Mocks;
using BaBaBooeyStrap.Tests.Tier1_FeatureCoverage.Engines;
using FluentAssertions;
using Xunit;

namespace BaBaBooeyStrap.Tests.Tier3_CrossFeatureCombinations;

public sealed class MultiInstance_With_CliLaunchTests : IClassFixture<MockRobloxEnvironment>
{
    private readonly MockRobloxEnvironment _env;

    public MultiInstance_With_CliLaunchTests(MockRobloxEnvironment env)
    {
        _env = env;
    }

    [Fact]
    public async Task T3_MultiInstance_CliLaunch_SuccessfulMultiLaunchWithThrottling()
    {
        using var helper = new ParameterizedMutexTestHelper("T3_MultiCli_Throttling");
        using var controller = new WindowsMultiInstanceController(helper.MutexName, helper.EventName);
        var supervisor = new ProcessSupervisor();

        controller.Enable();
        var status = controller.GetStatus();
        status.OwnsMutex.Should().BeTrue();

        var router1 = new LaunchSettingsRouter(new[] { "roblox-player:1+launchmode:play+gameinfo:ticket_acc1" });
        router1.Settings.RobloxLaunchMode.Should().Be(LaunchMode.Player);

        var gateResult1 = LaunchGate.VerifyLaunchReadiness(controller, multiInstanceEnabled: true);
        gateResult1.CanLaunch.Should().BeTrue();

        var t0 = DateTime.UtcNow;
        supervisor.RegisterProcess(4001);

        var router2 = new LaunchSettingsRouter(new[] { "roblox-player:1+launchmode:play+gameinfo:ticket_acc2" });
        router2.Settings.RobloxLaunchMode.Should().Be(LaunchMode.Player);

        var gateResult2 = LaunchGate.VerifyLaunchReadiness(controller, multiInstanceEnabled: true);
        gateResult2.CanLaunch.Should().BeTrue();

        await supervisor.ThrottleLaunchAsync(TimeSpan.FromMilliseconds(1500));
        supervisor.RegisterProcess(4002);

        var elapsed = (DateTime.UtcNow - t0).TotalMilliseconds;
        elapsed.Should().BeGreaterThanOrEqualTo(1400);

        controller.GetStatus().OwnsMutex.Should().BeTrue();
        supervisor.TrackedPids.Should().Contain(new[] { 4001, 4002 });
    }

    [Fact]
    public void T3_MultiInstance_CliLaunch_BlockedMutex_RejectsLaunchAndReportsDiagnostic()
    {
        using var helper = new ParameterizedMutexTestHelper("T3_MultiCli_Blocked");
        using var externalHolder = helper.HoldMutexExternally();
        using var controller = new WindowsMultiInstanceController(helper.MutexName, helper.EventName);

        controller.Enable();
        var router = new LaunchSettingsRouter(new[] { "roblox-player:1+launchmode:play+gameinfo:ticket123" });
        router.Settings.RobloxLaunchMode.Should().Be(LaunchMode.Player);

        var gateResult = LaunchGate.VerifyLaunchReadiness(controller, multiInstanceEnabled: true);

        gateResult.CanLaunch.Should().BeFalse();
        gateResult.WarningMessage.Should().NotBeNullOrWhiteSpace();
        gateResult.Status.OwnsMutex.Should().BeFalse();
        gateResult.Status.WaitingForMutex.Should().BeTrue();
    }

    [Fact]
    public async Task T3_MultiInstance_CliLaunch_WindowProbeDetection_TriggersNextLaunchSequence()
    {
        const int simulatedPid = 4410;
        using var testWindow = new NativeTestWindow("RobloxTestClient_4410", visible: true);

        var probeDelegate = new Func<IReadOnlyList<ClientWindowInfo>>(() =>
            new[] { new ClientWindowInfo(simulatedPid, testWindow.Handle, true) });

        var supervisor = new ProcessSupervisor(probeDelegate);
        var router = new LaunchSettingsRouter(new[] { "roblox-player:1+launchmode:play+gameinfo:ticket_probe" });
        router.Settings.RobloxLaunchMode.Should().Be(LaunchMode.Player);

        var windowHwnd = await supervisor.WaitForNewClientMainWindowAsync(simulatedPid, TimeSpan.FromSeconds(3));

        windowHwnd.Should().Be(testWindow.Handle);
        supervisor.RegisterProcess(simulatedPid);
        supervisor.TrackedPids.Should().Contain(simulatedPid);
    }

    [Fact]
    public async Task T3_MultiInstance_CliLaunch_RapidCliArguments_QueuesLaunchesSequentiallyWithoutCollision()
    {
        using var helper = new ParameterizedMutexTestHelper("T3_MultiCli_Rapid");
        using var controller = new WindowsMultiInstanceController(helper.MutexName, helper.EventName);
        controller.Enable();

        var supervisor = new ProcessSupervisor();
        string[] tickets = { "ticket_acc1", "ticket_acc2", "ticket_acc3" };
        int[] pids = { 5001, 5002, 5003 };

        var tasks = tickets.Select((ticket, idx) => Task.Run(async () =>
        {
            var router = new LaunchSettingsRouter(new[] { $"roblox-player:1+launchmode:play+gameinfo:{ticket}" });
            router.Settings.RobloxLaunchMode.Should().Be(LaunchMode.Player);

            var gateResult = LaunchGate.VerifyLaunchReadiness(controller, multiInstanceEnabled: true);
            gateResult.CanLaunch.Should().BeTrue();

            await supervisor.ThrottleLaunchAsync(TimeSpan.FromMilliseconds(200));
            supervisor.RegisterProcess(pids[idx]);
        })).ToArray();

        await Task.WhenAll(tasks);

        controller.GetStatus().OwnsMutex.Should().BeTrue();
        supervisor.TrackedPids.Count.Should().Be(3);
        supervisor.TrackedPids.Should().Contain(pids);
    }

    [Fact]
    public void T3_MultiInstance_CliLaunch_ConcurrentClients_MaintainsSingletonEventSignaled()
    {
        using var helper = new ParameterizedMutexTestHelper("T3_MultiCli_EventSignal");
        using var controller = new WindowsMultiInstanceController(helper.MutexName, helper.EventName);
        controller.Enable();

        var supervisor = new ProcessSupervisor();
        supervisor.RegisterProcess(6001);
        supervisor.RegisterProcess(6002);
        supervisor.RegisterProcess(6003);

        nint hEvent = Kernel32.OpenEventW(Kernel32.SYNCHRONIZE, false, helper.EventName);
        hEvent.Should().NotBe(nint.Zero);

        try
        {
            uint waitRes1 = Kernel32.WaitForSingleObject(hEvent, 50);
            waitRes1.Should().Be(Kernel32.WAIT_TIMEOUT);

            supervisor.UnregisterProcess(6001);

            uint waitRes2 = Kernel32.WaitForSingleObject(hEvent, 50);
            waitRes2.Should().Be(Kernel32.WAIT_TIMEOUT);
        }
        finally
        {
            Kernel32.CloseHandle(hEvent);
        }
    }
}
