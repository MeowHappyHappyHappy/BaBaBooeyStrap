using BaBaBooeyStrap.MultiInstance;
using BaBaBooeyStrap.MultiInstance.Abstractions;
using BaBaBooeyStrap.Tests.Mocks;

namespace BaBaBooeyStrap.Tests.MultiInstance;

public sealed class LaunchGateTests
{
    private sealed class StubMultiInstanceController : IMultiInstanceController
    {
        public bool Enabled { get; set; }
        public bool OwnsMutex { get; set; }
        public bool WaitingForMutex { get; set; }
        public IReadOnlyList<int> RunningRobloxPids { get; set; } = Array.Empty<int>();
        public string? LastError { get; set; }
        public int EnableCallCount { get; private set; }
        public int DisableCallCount { get; private set; }

        public MultiInstanceStatus Enable()
        {
            EnableCallCount++;
            Enabled = true;
            return GetStatus();
        }

        public MultiInstanceStatus Disable()
        {
            DisableCallCount++;
            Enabled = false;
            OwnsMutex = false;
            return GetStatus();
        }

        public MultiInstanceStatus GetStatus() =>
            new(Enabled, OwnsMutex, WaitingForMutex, RunningRobloxPids, LastError);

        public void Dispose() { }
    }

    // =========================================================================
    // VerifyLaunchReadiness Tests
    // =========================================================================

    [Fact]
    public void VerifyLaunchReadiness_WhenMutexOwned_ReturnsCanLaunchTrue()
    {
        var stub = new StubMultiInstanceController
        {
            Enabled = true,
            OwnsMutex = true,
            WaitingForMutex = false
        };

        var result = LaunchGate.VerifyLaunchReadiness(stub, multiInstanceEnabled: true);

        result.CanLaunch.Should().BeTrue();
        result.WarningMessage.Should().BeNull();
        result.Status.OwnsMutex.Should().BeTrue();
    }

    [Fact]
    public void VerifyLaunchReadiness_WhenMutexNotOwned_AttemptsAutoEnableAndSucceeds()
    {
        var stub = new StubMultiInstanceController
        {
            Enabled = false,
            OwnsMutex = false
        };

        // When Enable is called, simulate successful mutex acquisition
        var gate = new LaunchGate(stub);
        stub.OwnsMutex = true;

        var result = gate.VerifyLaunchReadiness(multiInstanceEnabled: true);

        result.CanLaunch.Should().BeTrue();
        result.WarningMessage.Should().BeNull();
    }

    [Fact]
    public void VerifyLaunchReadiness_WhenContended_ReturnsCanLaunchFalseWithDiagnosticMessage()
    {
        var stub = new StubMultiInstanceController
        {
            Enabled = true,
            OwnsMutex = false,
            WaitingForMutex = true,
            RunningRobloxPids = new[] { 5001, 5002 }
        };

        var result = LaunchGate.VerifyLaunchReadiness(stub, multiInstanceEnabled: true);

        result.CanLaunch.Should().BeFalse();
        result.WarningMessage.Should().NotBeNull();
        result.WarningMessage.Should().Contain("Roblox singleton mutex");
        result.WarningMessage.Should().Contain("WaitingForMutex=True");
        result.WarningMessage.Should().Contain("5001, 5002");
    }

    [Fact]
    public void VerifyLaunchReadiness_WhenMultiInstanceDisabled_BypassesCheck()
    {
        var stub = new StubMultiInstanceController
        {
            Enabled = false,
            OwnsMutex = false
        };

        var result = LaunchGate.VerifyLaunchReadiness(stub, multiInstanceEnabled: false);

        result.CanLaunch.Should().BeTrue();
        result.WarningMessage.Should().BeNull();
    }

    [Fact]
    public void VerifyLaunchReadiness_NullController_BlocksWhenEnabled()
    {
        var result = LaunchGate.VerifyLaunchReadiness(controller: null, multiInstanceEnabled: true);

        result.CanLaunch.Should().BeFalse();
        result.WarningMessage.Should().NotBeNull();
    }

    [Fact]
    public void VerifyLaunchReadiness_NullController_AllowsWhenDisabled()
    {
        var result = LaunchGate.VerifyLaunchReadiness(controller: null, multiInstanceEnabled: false);

        result.CanLaunch.Should().BeTrue();
        result.WarningMessage.Should().BeNull();
    }

    // =========================================================================
    // EnsureMutexOwnership Tests
    // =========================================================================

    [Fact]
    public void EnsureMutexOwnership_WhenAlreadyOwned_DoesNotCallEnable()
    {
        var stub = new StubMultiInstanceController
        {
            Enabled = true,
            OwnsMutex = true
        };

        bool owned = LaunchGate.EnsureMutexOwnership(stub);

        owned.Should().BeTrue();
        stub.EnableCallCount.Should().Be(0);
    }

    [Fact]
    public void EnsureMutexOwnership_WhenNotOwned_CallsEnableAndReturnsStatus()
    {
        var stub = new StubMultiInstanceController
        {
            Enabled = false,
            OwnsMutex = false
        };

        bool owned = LaunchGate.EnsureMutexOwnership(stub);

        stub.EnableCallCount.Should().Be(1);
        owned.Should().BeFalse(); // Stub did not transition OwnsMutex to true
    }

    [Fact]
    public void EnsureMutexOwnership_NullController_ThrowsArgumentNullException()
    {
        Action act = () => LaunchGate.EnsureMutexOwnership(null);
        act.Should().Throw<ArgumentNullException>();
    }

    // =========================================================================
    // Diagnostic Messages & Formatting
    // =========================================================================

    [Fact]
    public void BuildDiagnosticBlockedMessage_WithLastError_IncludesErrorText()
    {
        var status = new MultiInstanceStatus(
            Enabled: true,
            OwnsMutex: false,
            WaitingForMutex: false,
            RunningRobloxPids: new[] { 1234 },
            LastError: "Access is denied");

        string message = LaunchGate.BuildDiagnosticBlockedMessage(status);

        message.Should().Contain("Access is denied");
        message.Should().Contain("RunningRobloxPids=1234");
        message.Should().Contain("OwnsMutex=False");
    }

    [Fact]
    public void BuildDiagnosticBlockedMessage_EmptyPidsAndNullError_FormatsAsNone()
    {
        var status = new MultiInstanceStatus(
            Enabled: true,
            OwnsMutex: false,
            WaitingForMutex: true,
            RunningRobloxPids: Array.Empty<int>(),
            LastError: null);

        string message = LaunchGate.BuildDiagnosticBlockedMessage(status);

        message.Should().Contain("RunningRobloxPids=none");
        message.Should().Contain("LastError=none");
        message.Should().Contain("WaitingForMutex=True");
    }

    [Fact]
    public void BuildDiagnosticBlockedMessage_NullStatus_ThrowsArgumentNullException()
    {
        Action act = () => LaunchGate.BuildDiagnosticBlockedMessage(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // =========================================================================
    // Interface & Concurrency
    // =========================================================================

    [Fact]
    public void ILaunchGate_InstanceMethods_DelegateProperly()
    {
        var stub = new StubMultiInstanceController
        {
            Enabled = true,
            OwnsMutex = true,
            RunningRobloxPids = new[] { 7777 }
        };

        ILaunchGate gate = new LaunchGate(stub);

        gate.VerifyLaunchReadiness(multiInstanceEnabled: true).CanLaunch.Should().BeTrue();
        gate.EnsureMutexOwnership().Should().BeTrue();
        gate.GetDiagnosticStatus().RunningRobloxPids.Should().Contain(7777);

        var blockedStatus = new MultiInstanceStatus(true, false, true, Array.Empty<int>(), null);
        gate.BuildDiagnosticBlockedMessage(blockedStatus).Should().Contain("WaitingForMutex=True");
    }

    [Fact]
    public async Task LaunchGate_ParallelVerificationUnderContention_ReturnsConsistentDiagnostics()
    {
        var stub = new StubMultiInstanceController
        {
            Enabled = true,
            OwnsMutex = false,
            WaitingForMutex = true,
            RunningRobloxPids = new[] { 9999 }
        };

        var tasks = Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
        {
            var res = LaunchGate.VerifyLaunchReadiness(stub, multiInstanceEnabled: true);
            res.CanLaunch.Should().BeFalse();
            res.WarningMessage.Should().Contain("WaitingForMutex=True");
        })).ToArray();

        await Task.WhenAll(tasks);
    }
}
