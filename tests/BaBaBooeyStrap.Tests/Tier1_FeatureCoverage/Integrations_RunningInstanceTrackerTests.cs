using BaBaBooeyStrap.Core.Interop;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Integrations.Window;

namespace BaBaBooeyStrap.Tests.Tier1_FeatureCoverage;

public sealed class Integrations_RunningInstanceTrackerTests
{
    [Fact]
    public void RegisterLaunchedAccount_AssignsSlotsAndFormatsWindowTitlesWithoutRecursiveWrapping()
    {
        var settings = new Settings
        {
            AutoTagMultiInstanceWindowTitle = true,
            MultiInstanceWindowTitleTemplate = "[#{Index}] {Account} ({Notes}) - {OriginalTitle}"
        };

        var interop = new TrackerMockInterop();
        var mon1 = new MonitorInfo(1, @"\\.\DISPLAY1", true,
            new User32.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 },
            new User32.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1040 });
        var monitorProvider = new SingleMonitorProvider(mon1);

        var observed = new List<ObservedRobloxWindow>();
        using var tracker = new RunningInstanceTracker(
            () => settings,
            interop,
            monitorProvider,
            windowProbe: () => observed.Select(w => w with { WindowTitle = interop.GetWindowText(w.WindowHandle) }).ToList());

        var acc1 = new RobloxAccount { Id = Guid.NewGuid(), UserId = 101, Username = "MainAccount", DisplayName = "Main", Notes = "Boss" };
        var acc2 = new RobloxAccount { Id = Guid.NewGuid(), UserId = 202, Username = "AltAccount", DisplayName = "Alt", Notes = "AFK" };

        nint hwnd1 = 0x5001;
        nint hwnd2 = 0x5002;
        interop.SetWindowText(hwnd1, "Roblox");
        interop.SetWindowText(hwnd2, "Roblox");
        observed.Add(new ObservedRobloxWindow(1111, hwnd1, "Roblox", false, true, mon1));
        observed.Add(new ObservedRobloxWindow(2222, hwnd2, "Roblox", true, false, mon1));

        tracker.RegisterLaunchedAccount(1111, acc1);
        tracker.RegisterLaunchedAccount(2222, acc2);

        interop.GetWindowText(hwnd1).Should().Be("[#1] MainAccount (Boss) - Roblox");
        interop.GetWindowText(hwnd2).Should().Be("[#2] AltAccount (AFK) - Roblox");
        int writesAfterInitial = interop.SetTitleCalls;

        // Repeated refreshes do not recursively wrap or make redundant writes
        tracker.RefreshNow();
        tracker.RefreshNow();
        interop.GetWindowText(hwnd1).Should().Be("[#1] MainAccount (Boss) - Roblox");
        interop.SetTitleCalls.Should().Be(writesAfterInitial);

        var list = tracker.GetRunningInstances();
        list.Count.Should().Be(2);
        list[0].IsFocused.Should().BeTrue();
        list[1].IsMinimized.Should().BeTrue();
        list[0].EffectiveLabel.Should().Be("MainAccount");
    }

    [Fact]
    public void ExternalWindow_InitiallyShowsPidLabel_ThenBackfillsAccountOnTryAssociateUserId()
    {
        var settings = new Settings { AutoTagMultiInstanceWindowTitle = true };
        var interop = new TrackerMockInterop();
        var mon1 = new MonitorInfo(1, @"\\.\DISPLAY1", true,
            new User32.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 },
            new User32.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1040 });

        var knownAccounts = new Dictionary<long, RobloxAccount>
        {
            [999888] = new RobloxAccount { Id = Guid.NewGuid(), UserId = 999888, Username = "BrowserUser", DisplayName = "Browser", Notes = "Alt" }
        };

        nint hwnd = 0x6001;
        interop.SetWindowText(hwnd, "Roblox");
        var observed = new List<ObservedRobloxWindow>
        {
            new(4242, hwnd, "Roblox", false, false, mon1)
        };

        using var tracker = new RunningInstanceTracker(
            () => settings,
            interop,
            new SingleMonitorProvider(mon1),
            accountLookupByUserId: uid => knownAccounts.TryGetValue(uid, out var acc) ? acc : null,
            windowProbe: () => observed.Select(w => w with { WindowTitle = interop.GetWindowText(w.WindowHandle) }).ToList());

        tracker.RefreshNow();
        var initial = tracker.GetRunningInstances();
        initial.Should().ContainSingle();
        initial[0].IsExternal.Should().BeTrue();
        initial[0].EffectiveLabel.Should().Be("Roblox");
        interop.GetWindowText(hwnd).Should().Be("[#1] Roblox");

        int eventCount = 0;
        tracker.InstancesChanged += (_, _) => eventCount++;

        bool associated = tracker.TryAssociateUserId(4242, 999888);
        associated.Should().BeTrue();
        eventCount.Should().Be(1);

        var updated = tracker.GetRunningInstances();
        updated[0].IsExternal.Should().BeFalse();
        updated[0].EffectiveLabel.Should().Be("BrowserUser");
        interop.GetWindowText(hwnd).Should().Be("[#1] BrowserUser - Roblox");
    }

    [Fact]
    public void ExternallyManagedTitle_SkipsTrackerTitleWrites_AndDisablingAutoTagRestoresOriginalTitle()
    {
        var settings = new Settings { AutoTagMultiInstanceWindowTitle = true };
        var interop = new TrackerMockInterop();
        var mon1 = new MonitorInfo(1, @"\\.\DISPLAY1", true,
            new User32.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 },
            new User32.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1040 });

        nint hwnd = 0x7001;
        interop.SetWindowText(hwnd, "Roblox");
        var observed = new List<ObservedRobloxWindow>
        {
            new(5555, hwnd, "Roblox", false, false, mon1)
        };

        using var tracker = new RunningInstanceTracker(
            () => settings,
            interop,
            new SingleMonitorProvider(mon1),
            windowProbe: () => observed.Select(w => w with { WindowTitle = interop.GetWindowText(w.WindowHandle) }).ToList());

        tracker.MarkTitleManagedExternally(5555, true);
        tracker.RegisterLaunchedAccount(5555, new RobloxAccount { UserId = 55, Username = "Hero" });

        // Tracker must NOT overwrite title while externally managed by WindowManipulationService
        interop.GetWindowText(hwnd).Should().Be("Roblox");
        tracker.GetPlaceholdersForPid(5555)["Account"].Should().Be("Hero");
        tracker.GetPlaceholdersForPid(5555)["Index"].Should().Be("1");

        // Releasing external management lets tracker tag the title
        tracker.MarkTitleManagedExternally(5555, false);
        interop.GetWindowText(hwnd).Should().Be("[#1] Hero - Roblox");

        // Disabling AutoTag restores original title
        settings.AutoTagMultiInstanceWindowTitle = false;
        tracker.RefreshNow();
        interop.GetWindowText(hwnd).Should().Be("Roblox");

        // Re-enabling AutoTag and simulating Roblox resetting title to "Roblox" re-tags cleanly
        settings.AutoTagMultiInstanceWindowTitle = true;
        tracker.RefreshNow();
        interop.GetWindowText(hwnd).Should().Be("[#1] Hero - Roblox");

        interop.SetWindowText(hwnd, "Roblox");
        tracker.RefreshNow();
        interop.GetWindowText(hwnd).Should().Be("[#1] Hero - Roblox");
    }

    [Theory]
    [InlineData("JP", "PlayerOne", "[#1] [JP] PlayerOne - Roblox")]
    [InlineData("", "PlayerOne", "[#1] PlayerOne - Roblox")]
    [InlineData("JP", "", "[#1] [JP] Roblox")]
    [InlineData("", "", "[#1] Roblox")]
    public void WindowTitleFormatter_DefaultTemplate_FormatsAccountAndServerCombinationsCleanly(
        string server, string account, string expected)
    {
        var placeholders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Index"] = "1",
            ["Server"] = server,
            ["Account"] = account,
            ["OriginalTitle"] = "Roblox"
        };

        WindowTitleFormatter.Format(Settings.DefaultMultiInstanceWindowTitleTemplate, placeholders)
            .Should().Be(expected);
    }

    [Fact]
    public void UpdateServerCode_UpdatesAndClearsServerAbbreviationInWindowTitle()
    {
        var settings = new Settings { AutoTagMultiInstanceWindowTitle = true };
        var interop = new TrackerMockInterop();
        var mon1 = new MonitorInfo(1, @"\\.\DISPLAY1", true,
            new User32.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 },
            new User32.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1040 });

        nint hwnd = 0x7002;
        interop.SetWindowText(hwnd, "Roblox");
        var observed = new List<ObservedRobloxWindow>
        {
            new(6666, hwnd, "Roblox", false, false, mon1)
        };

        using var tracker = new RunningInstanceTracker(
            () => settings,
            interop,
            new SingleMonitorProvider(mon1),
            windowProbe: () => observed.Select(w => w with { WindowTitle = interop.GetWindowText(w.WindowHandle) }).ToList());

        tracker.RegisterLaunchedAccount(6666, new RobloxAccount { UserId = 66, Username = "PlayerOne" });
        interop.GetWindowText(hwnd).Should().Be("[#1] PlayerOne - Roblox");

        tracker.UpdateServerCode(6666, "Japan");
        interop.GetWindowText(hwnd).Should().Be("[#1] [JP] PlayerOne - Roblox");

        tracker.UpdateServerCode(6666, "US");
        interop.GetWindowText(hwnd).Should().Be("[#1] [US] PlayerOne - Roblox");

        tracker.UpdateServerCode(6666, "Unknown");
        interop.GetWindowText(hwnd).Should().Be("[#1] PlayerOne - Roblox");
    }

    [Fact]
    public void PreRegisteredWindowlessPid_DoesNotReserveSlot_AndClosingMiddleSlotCompactsImmediately()
    {
        var settings = new Settings { AutoTagMultiInstanceWindowTitle = true };
        var interop = new TrackerMockInterop();
        var mon1 = new MonitorInfo(1, @"\\.\DISPLAY1", true,
            new User32.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 },
            new User32.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1040 });

        nint hwnd1 = 0x8001;
        nint hwnd2 = 0x8002;
        nint hwnd3 = 0x8003;
        interop.SetWindowText(hwnd1, "Roblox");
        interop.SetWindowText(hwnd2, "Roblox");
        interop.SetWindowText(hwnd3, "Roblox");

        var observed = new List<ObservedRobloxWindow>();
        using var tracker = new RunningInstanceTracker(
            () => settings,
            interop,
            new SingleMonitorProvider(mon1),
            windowProbe: () => observed.Select(w => w with { WindowTitle = interop.GetWindowText(w.WindowHandle) }).ToList());

        // Simulate a launcher/stub PID that was pre-registered but never opened a visible window
        tracker.RegisterLaunchedAccount(1000, new RobloxAccount { UserId = 1, Username = "StubPid" });

        // First real visible window opens on PID 1001 -> must receive #1, not #2
        observed.Add(new ObservedRobloxWindow(1001, hwnd1, "Roblox", false, true, mon1));
        tracker.RegisterLaunchedAccount(1001, new RobloxAccount { UserId = 1, Username = "AccOne" });

        tracker.GetRunningInstances().Select(i => (i.ProcessId, i.SlotIndex))
            .Should().Equal((1001, 1));
        interop.GetWindowText(hwnd1).Should().Be("[#1] AccOne - Roblox");

        // Second and third real windows open
        observed.Add(new ObservedRobloxWindow(1002, hwnd2, "Roblox", false, false, mon1));
        tracker.RegisterLaunchedAccount(1002, new RobloxAccount { UserId = 2, Username = "AccTwo" });
        observed.Add(new ObservedRobloxWindow(1003, hwnd3, "Roblox", false, false, mon1));
        tracker.RegisterLaunchedAccount(1003, new RobloxAccount { UserId = 3, Username = "AccThree" });

        tracker.GetRunningInstances().Select(i => (i.ProcessId, i.SlotIndex))
            .Should().Equal((1001, 1), (1002, 2), (1003, 3));

        // Close #1 -> #2 and #3 immediately compact to #1 and #2 and update window titles
        observed.RemoveAll(w => w.ProcessId == 1001);
        tracker.RefreshNow();

        tracker.GetRunningInstances().Select(i => (i.ProcessId, i.SlotIndex))
            .Should().Equal((1002, 1), (1003, 2));
        interop.GetWindowText(hwnd2).Should().Be("[#1] AccTwo - Roblox");
        interop.GetWindowText(hwnd3).Should().Be("[#2] AccThree - Roblox");
    }

    private sealed class SingleMonitorProvider(MonitorInfo monitor) : IMonitorProvider
    {
        public IReadOnlyList<MonitorInfo> GetMonitors() => [monitor];
        public MonitorInfo GetPrimaryMonitor() => monitor;
        public MonitorInfo GetMonitorFromCursor() => monitor;
        public MonitorInfo GetMonitorFromWindow(nint hWnd) => monitor;
        public MonitorInfo? FindMonitorByDeviceName(string? deviceName) => monitor;
    }

    private sealed class TrackerMockInterop : IWin32WindowInterop
    {
        private readonly Dictionary<nint, string> _titles = new();
        public int SetTitleCalls { get; private set; }

        public bool IsWindow(nint hWnd) => _titles.ContainsKey(hWnd);
        public bool IsWindowVisible(nint hWnd) => _titles.ContainsKey(hWnd);
        public uint GetWindowThreadProcessId(nint hWnd, out uint processId) { processId = 0; return 1; }
        public string GetClassName(nint hWnd) => "WINDOWSCLIENT";
        public string GetWindowText(nint hWnd) => _titles.TryGetValue(hWnd, out string? t) ? t : string.Empty;
        public bool SetWindowText(nint hWnd, string text)
        {
            SetTitleCalls++;
            _titles[hWnd] = text;
            return true;
        }
        public nint GetWindowLongPtr(nint hWnd, int nIndex) => nint.Zero;
        public nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong) => nint.Zero;
        public bool GetWindowRect(nint hWnd, out User32.RECT rect) { rect = default; return false; }
        public User32.RECT GetMonitorRectFromWindow(nint hWnd) => new() { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
        public int GetSystemMetrics(int nIndex) => 1920;
        public bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags) => true;
        public nint SendMessage(nint hWnd, uint msg, nint wParam, nint lParam) => nint.Zero;
        public nint LoadImageIcon(string filePath, int cx, int cy) => nint.Zero;
        public (nint SmallIcon, nint LargeIcon) ExtractFirstIconPair(string executablePath) => (nint.Zero, nint.Zero);
        public bool DestroyIcon(nint hIcon) => true;
        public nint SetWinEventHook(uint eventMin, uint eventMax, User32.WinEventDelegate callback, uint idProcess, uint idThread, uint flags) => nint.Zero;
        public bool UnhookWinEvent(nint hWinEventHook) => true;
        public uint GetCurrentThreadId() => 1;
        public bool PeekMessage(out User32.MSG msg, nint hWnd, uint filterMin, uint filterMax, uint removeMsg) { msg = default; return false; }
        public int GetMessage(out User32.MSG msg, nint hWnd, uint filterMin, uint filterMax) { msg = default; return 0; }
        public bool TranslateMessage(ref User32.MSG msg) => false;
        public nint DispatchMessage(ref User32.MSG msg) => nint.Zero;
        public bool PostThreadMessage(uint idThread, uint msg, nint wParam, nint lParam) => true;
    }
}
