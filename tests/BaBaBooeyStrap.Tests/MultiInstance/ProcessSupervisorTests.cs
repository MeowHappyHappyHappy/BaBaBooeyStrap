using System.Diagnostics;
using BaBaBooeyStrap.MultiInstance;
using BaBaBooeyStrap.MultiInstance.Abstractions;

namespace BaBaBooeyStrap.Tests.MultiInstance;

public sealed class ProcessSupervisorTests
{
    // =========================================================================
    // Process Tracking & Enumeration
    // =========================================================================

    [Fact]
    public void RegisterProcess_ValidPid_AddsToTrackedList()
    {
        var supervisor = new ProcessSupervisor();
        supervisor.RegisterProcess(1234);

        supervisor.TrackedPids.Should().ContainSingle().Which.Should().Be(1234);
    }

    [Fact]
    public void RegisterProcess_DuplicatePid_DoesNotAddDuplicates()
    {
        var supervisor = new ProcessSupervisor();
        supervisor.RegisterProcess(1234);
        supervisor.RegisterProcess(1234);

        supervisor.TrackedPids.Should().ContainSingle().Which.Should().Be(1234);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void RegisterProcess_ZeroOrNegativePid_ThrowsArgumentOutOfRangeException(int invalidPid)
    {
        var supervisor = new ProcessSupervisor();
        Action act = () => supervisor.RegisterProcess(invalidPid);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void UnregisterProcess_ExistingPid_RemovesAndReturnsTrue()
    {
        var supervisor = new ProcessSupervisor();
        supervisor.RegisterProcess(1234);

        bool removed = supervisor.UnregisterProcess(1234);

        removed.Should().BeTrue();
        supervisor.TrackedPids.Should().BeEmpty();
    }

    [Fact]
    public void UnregisterProcess_NonExistentPid_ReturnsFalse()
    {
        var supervisor = new ProcessSupervisor();
        bool removed = supervisor.UnregisterProcess(9999);

        removed.Should().BeFalse();
    }

    [Fact]
    public void ClearTrackedProcesses_RemovesAllPids()
    {
        var supervisor = new ProcessSupervisor();
        supervisor.RegisterProcess(101);
        supervisor.RegisterProcess(102);
        supervisor.RegisterProcess(103);

        supervisor.ClearTrackedProcesses();

        supervisor.TrackedPids.Should().BeEmpty();
    }

    [Fact]
    public void TrackedPids_ReturnsDefensiveCopy()
    {
        var supervisor = new ProcessSupervisor();
        supervisor.RegisterProcess(101);

        var snapshot = supervisor.TrackedPids;
        supervisor.RegisterProcess(102);

        snapshot.Should().ContainSingle().Which.Should().Be(101);
        supervisor.TrackedPids.Should().HaveCount(2);
    }

    [Fact]
    public void GetRunningRobloxPids_WithMockProbe_ReturnsSortedAscendingDistinct()
    {
        var probe = new Func<IReadOnlyList<ClientWindowInfo>>(() => new[]
        {
            new ClientWindowInfo(5002, 100, true),
            new ClientWindowInfo(1001, 101, true),
            new ClientWindowInfo(3004, 102, true),
            new ClientWindowInfo(1001, 103, true), // Duplicate PID with different window
        });

        var supervisor = new ProcessSupervisor(probe);
        var pids = supervisor.GetRunningRobloxPids();

        pids.Should().Equal(new[] { 1001, 3004, 5002 });
    }

    [Fact]
    public void CaptureRunningClientPids_ReturnsHashSet()
    {
        var probe = new Func<IReadOnlyList<ClientWindowInfo>>(() => new[]
        {
            new ClientWindowInfo(1001, 101, true),
            new ClientWindowInfo(2002, 102, true)
        });

        var supervisor = new ProcessSupervisor(probe);
        var pidSet = supervisor.CaptureRunningClientPids();

        pidSet.Should().Contain(1001);
        pidSet.Should().Contain(2002);
        pidSet.Count.Should().Be(2);
    }

    // =========================================================================
    // Launch Throttling
    // =========================================================================

    [Fact]
    public async Task ThrottleLaunchAsync_DefaultDelay_EnforcesMinimum1500MsInterval()
    {
        var supervisor = new ProcessSupervisor();

        // Baseline call
        await supervisor.ThrottleLaunchAsync();

        var sw = Stopwatch.StartNew();
        // Second call with default delay
        await supervisor.ThrottleLaunchAsync();
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(1450);
    }

    [Fact]
    public async Task ThrottleLaunchAsync_ConfigurableDelay_RespectsConfiguredValue()
    {
        var supervisor = new ProcessSupervisor();
        var delay = TimeSpan.FromMilliseconds(600);

        await supervisor.ThrottleLaunchAsync(delay);

        var sw = Stopwatch.StartNew();
        await supervisor.ThrottleLaunchAsync(delay);
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(550);
    }

    [Fact]
    public async Task ThrottleLaunchAsync_ZeroOrNegativeDelay_ClampsTo500MsFloor()
    {
        var supervisor = new ProcessSupervisor();

        await supervisor.ThrottleLaunchAsync(TimeSpan.Zero);

        var sw = Stopwatch.StartNew();
        await supervisor.ThrottleLaunchAsync(TimeSpan.Zero);
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(450);
    }

    [Fact]
    public async Task ThrottleLaunchAsync_PreCancelledToken_ThrowsOperationCanceledException()
    {
        var supervisor = new ProcessSupervisor();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = async () => await supervisor.ThrottleLaunchAsync(TimeSpan.FromSeconds(1), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ThrottleLaunchAsync_SufficientElapsed_ReturnsZeroDelay()
    {
        var supervisor = new ProcessSupervisor();
        var delay = TimeSpan.FromMilliseconds(500);

        await supervisor.ThrottleLaunchAsync(delay);

        // Wait longer than required delay
        await Task.Delay(550);

        var sw = Stopwatch.StartNew();
        var appliedDelay = await supervisor.ThrottleLaunchAsync(delay);
        sw.Stop();

        appliedDelay.Should().Be(TimeSpan.Zero);
        sw.ElapsedMilliseconds.Should().BeLessThan(100);
    }

    [Fact]
    public async Task ThrottleLaunchAsync_ConcurrentBurst_SerializesLaunches()
    {
        var supervisor = new ProcessSupervisor();
        var executionTimes = new List<DateTime>();
        var lockObj = new object();

        var tasks = Enumerable.Range(0, 3).Select(async _ =>
        {
            await supervisor.ThrottleLaunchAsync(TimeSpan.FromMilliseconds(500));
            lock (lockObj) executionTimes.Add(DateTime.UtcNow);
        }).ToArray();

        await Task.WhenAll(tasks);

        executionTimes.Count.Should().Be(3);
        // Sequential spacing check between consecutive items
        var sorted = executionTimes.OrderBy(t => t).ToList();
        for (int i = 1; i < sorted.Count; i++)
        {
            var gap = sorted[i] - sorted[i - 1];
            gap.TotalMilliseconds.Should().BeGreaterThanOrEqualTo(400);
        }
    }

    // =========================================================================
    // Window Probing
    // =========================================================================

    [Fact]
    public async Task WaitForNewClientMainWindowAsync_DeltaProbe_DetectsNewVisibleWindow()
    {
        int pollCount = 0;
        var probe = new Func<IReadOnlyList<ClientWindowInfo>>(() =>
        {
            pollCount++;
            if (pollCount == 1)
            {
                // First poll: process 200 window is not visible yet
                return new[]
                {
                    new ClientWindowInfo(100, 1001, true),
                    new ClientWindowInfo(200, 2001, false)
                };
            }
            // Second poll: process 200 window becomes visible
            return new[]
            {
                new ClientWindowInfo(100, 1001, true),
                new ClientWindowInfo(200, 2001, true)
            };
        });

        var supervisor = new ProcessSupervisor(probe);
        var previousPids = new HashSet<int> { 100 };

        var result = await supervisor.WaitForNewClientMainWindowAsync(
            previousPids,
            timeout: TimeSpan.FromSeconds(2),
            pollInterval: TimeSpan.FromMilliseconds(50));

        result.WindowReady.Should().BeTrue();
        result.ProcessId.Should().Be(200);
        result.MainWindowHandle.Should().Be(2001);
        result.TimedOut.Should().BeFalse();
    }

    [Fact]
    public async Task WaitForNewClientMainWindowAsync_IgnoresHiddenOrZeroHwnd()
    {
        var probe = new Func<IReadOnlyList<ClientWindowInfo>>(() => new[]
        {
            new ClientWindowInfo(200, 0, true),       // Zero hwnd
            new ClientWindowInfo(201, 2001, false),   // Invisible
        });

        var supervisor = new ProcessSupervisor(probe);
        var previousPids = new HashSet<int>();

        var result = await supervisor.WaitForNewClientMainWindowAsync(
            previousPids,
            timeout: TimeSpan.FromMilliseconds(100),
            pollInterval: TimeSpan.FromMilliseconds(20));

        result.WindowReady.Should().BeFalse();
        result.TimedOut.Should().BeTrue();
        result.ProcessId.Should().BeNull();
        result.MainWindowHandle.Should().Be(nint.Zero);
    }

    [Fact]
    public async Task WaitForNewClientMainWindowAsync_DeltaProbe_NullPreviousPids_ThrowsArgumentNullException()
    {
        var supervisor = new ProcessSupervisor();
        Func<Task> act = async () => await supervisor.WaitForNewClientMainWindowAsync(previousPids: null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task WaitForNewClientMainWindowAsync_DeltaProbe_Cancellation_ThrowsOperationCanceledException()
    {
        var supervisor = new ProcessSupervisor();
        using var cts = new CancellationTokenSource(30);

        Func<Task> act = async () => await supervisor.WaitForNewClientMainWindowAsync(
            new HashSet<int>(),
            timeout: TimeSpan.FromSeconds(5),
            pollInterval: TimeSpan.FromMilliseconds(50),
            ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task WaitForNewClientMainWindowAsync_DeltaProbe_PreCancelledToken_ThrowsOperationCanceledException()
    {
        var supervisor = new ProcessSupervisor();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = async () => await supervisor.WaitForNewClientMainWindowAsync(
            new HashSet<int>(),
            timeout: TimeSpan.FromSeconds(5),
            pollInterval: TimeSpan.FromMilliseconds(50),
            ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task WaitForNewClientMainWindowAsync_SinglePid_PreCancelledToken_ThrowsOperationCanceledException()
    {
        var supervisor = new ProcessSupervisor();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = async () => await supervisor.WaitForNewClientMainWindowAsync(
            pid: 1234,
            timeout: TimeSpan.FromSeconds(5),
            ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task WaitForNewClientMainWindowAsync_SinglePid_ReturnsHwndWhenVisible()
    {
        var probe = new Func<IReadOnlyList<ClientWindowInfo>>(() => new[]
        {
            new ClientWindowInfo(300, 3001, true)
        });

        var supervisor = new ProcessSupervisor(probe);
        var hwnd = await supervisor.WaitForNewClientMainWindowAsync(300, TimeSpan.FromSeconds(1));

        hwnd.Should().Be(3001);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task WaitForNewClientMainWindowAsync_SinglePid_ZeroOrNegative_ReturnsZeroImmediately(int pid)
    {
        var supervisor = new ProcessSupervisor();
        var sw = Stopwatch.StartNew();
        var hwnd = await supervisor.WaitForNewClientMainWindowAsync(pid, TimeSpan.FromSeconds(2));
        sw.Stop();

        hwnd.Should().Be(nint.Zero);
        sw.ElapsedMilliseconds.Should().BeLessThan(50);
    }

    [Fact]
    public async Task WaitForNewClientMainWindowAsync_SinglePid_Timeout_ReturnsZero()
    {
        var probe = new Func<IReadOnlyList<ClientWindowInfo>>(() => Array.Empty<ClientWindowInfo>());
        var supervisor = new ProcessSupervisor(probe);

        var sw = Stopwatch.StartNew();
        var hwnd = await supervisor.WaitForNewClientMainWindowAsync(999, TimeSpan.FromMilliseconds(100));
        sw.Stop();

        hwnd.Should().Be(nint.Zero);
        sw.ElapsedMilliseconds.Should().BeInRange(80, 500);
    }
}
