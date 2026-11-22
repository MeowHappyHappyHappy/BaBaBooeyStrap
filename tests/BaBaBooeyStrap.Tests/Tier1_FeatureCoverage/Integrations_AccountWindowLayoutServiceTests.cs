using BaBaBooeyStrap.Core.Interop;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Integrations.Window;

namespace BaBaBooeyStrap.Tests.Tier1_FeatureCoverage;

public sealed class Integrations_AccountWindowLayoutServiceTests
{
    private static readonly MonitorInfo PrimaryMonitor = new(
        1,
        @"\\.\DISPLAY1",
        true,
        new User32.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 },
        new User32.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1040 });

    private static readonly MonitorInfo SecondaryMonitor = new(
        2,
        @"\\.\DISPLAY2",
        false,
        new User32.RECT { Left = 1920, Top = 0, Right = 3840, Bottom = 1080 },
        new User32.RECT { Left = 1920, Top = 0, Right = 3840, Bottom = 1040 });

    [Fact]
    public void CaptureBounds_PreservesNormalRectWhenMaximized_AndIgnoresMinimizedWindow()
    {
        nint hwnd = 0x1001;
        var interop = new LayoutMockInterop();
        interop.RegisterWindow(hwnd, new User32.RECT { Left = 120, Top = 80, Right = 1120, Bottom = 780 }, zoomed: true, iconic: false);

        var monitorProvider = new MockMonitorProvider([PrimaryMonitor], PrimaryMonitor, _ => PrimaryMonitor);
        var service = new AccountWindowLayoutService(interop, monitorProvider);

        var bounds = service.CaptureBounds(hwnd);
        bounds.Should().NotBeNull();
        bounds!.Left.Should().Be(120);
        bounds.Top.Should().Be(80);
        bounds.Width.Should().Be(1000);
        bounds.Height.Should().Be(700);
        bounds.IsMaximized.Should().BeTrue();
        bounds.MonitorDeviceName.Should().Be(@"\\.\DISPLAY1");

        // When minimized, CaptureBounds returns null so saved normal/maximized state is preserved
        interop.SetWindowState(hwnd, new User32.RECT { Left = -32000, Top = -32000, Right = -31840, Bottom = -31960 }, zoomed: false, iconic: true);
        service.CaptureBounds(hwnd).Should().BeNull();
    }

    [Fact]
    public void ComputeRestoredRect_ClampsAndMovesToPrimaryWhenMonitorDisconnectedOrOffScreen()
    {
        var savedOnDisplay2 = new AccountWindowBounds
        {
            Left = 2100,
            Top = 100,
            Width = 1000,
            Height = 700,
            MonitorDeviceName = @"\\.\DISPLAY2"
        };

        // When Display2 is connected, keep exact bounds
        var connectedRect = AccountWindowLayoutService.ComputeRestoredRect(
            savedOnDisplay2,
            [PrimaryMonitor, SecondaryMonitor],
            PrimaryMonitor);
        connectedRect.Left.Should().Be(2100);
        connectedRect.Top.Should().Be(100);
        connectedRect.Width.Should().Be(1000);
        connectedRect.Height.Should().Be(700);

        // When Display2 is disconnected, center on primary work area preserving size
        var disconnectedRect = AccountWindowLayoutService.ComputeRestoredRect(
            savedOnDisplay2,
            [PrimaryMonitor],
            PrimaryMonitor);
        disconnectedRect.Left.Should().Be(460);
        disconnectedRect.Top.Should().Be(170);
        disconnectedRect.Width.Should().Be(1000);
        disconnectedRect.Height.Should().Be(700);
    }

    [Fact]
    public void TryRestoreWindow_SkipsWhenDisabledOrBorderlessFullscreen_AndRestoresMaximizedState()
    {
        nint hwnd = 0x2001;
        var interop = new LayoutMockInterop();
        interop.RegisterWindow(hwnd, new User32.RECT { Left = 0, Top = 0, Right = 800, Bottom = 600 }, zoomed: false, iconic: false);
        var monitorProvider = new MockMonitorProvider([PrimaryMonitor], PrimaryMonitor, _ => PrimaryMonitor);
        var service = new AccountWindowLayoutService(interop, monitorProvider);

        var saved = new AccountWindowBounds
        {
            Left = 100,
            Top = 50,
            Width = 1280,
            Height = 720,
            IsMaximized = true,
            MonitorDeviceName = @"\\.\DISPLAY1"
        };

        service.TryRestoreWindow(hwnd, saved, new Settings { RestoreAccountWindowBounds = false }).Should().BeFalse();
        interop.SetWindowPosCalls.Should().Be(0);

        service.TryRestoreWindow(hwnd, saved, new Settings { RestoreAccountWindowBounds = true, FakeBorderlessFullscreen = true }).Should().BeFalse();
        interop.SetWindowPosCalls.Should().Be(0);

        service.TryRestoreWindow(hwnd, saved, new Settings { RestoreAccountWindowBounds = true, FakeBorderlessFullscreen = false }).Should().BeTrue();
        interop.SetWindowPosCalls.Should().Be(1);
        interop.GetNormalRect(hwnd).Left.Should().Be(100);
        interop.GetNormalRect(hwnd).Top.Should().Be(50);
        interop.GetNormalRect(hwnd).Width.Should().Be(1280);
        interop.GetNormalRect(hwnd).Height.Should().Be(720);
        interop.ShowWindowHistory.Should().Contain((hwnd, User32.SW_SHOWMAXIMIZED));
    }

    [Fact]
    public void ProcessInstances_SavesBoundsOnClose_AndRestoresOnceOnNextLaunch()
    {
        var accId = Guid.NewGuid();
        var accountKey = AccountWindowLayoutService.GetAccountKey(accId, 101)!;
        var settings = new Settings { RestoreAccountWindowBounds = true };
        var state = new State();
        int saveCount = 0;

        nint hwnd1 = 0x3001;
        var interop = new LayoutMockInterop();
        interop.RegisterWindow(hwnd1, new User32.RECT { Left = 250, Top = 140, Right = 1250, Bottom = 840 }, zoomed: false, iconic: false);
        var monitorProvider = new MockMonitorProvider([PrimaryMonitor], PrimaryMonitor, _ => PrimaryMonitor);
        var service = new AccountWindowLayoutService(interop, monitorProvider);

        var inst1 = new RunningRobloxInstance(
            ProcessId: 1001,
            WindowHandle: hwnd1,
            SlotIndex: 1,
            AccountId: accId,
            UserId: 101,
            Username: "PlayerOne",
            DisplayName: "PlayerOne",
            Notes: "",
            IsExternal: false,
            IsMinimized: false,
            IsFocused: true,
            MonitorDeviceName: PrimaryMonitor.DeviceName,
            MonitorIndex: 1,
            StartedAtUtc: DateTimeOffset.UtcNow);

        service.ProcessInstances([inst1], settings, state, () => saveCount++);
        state.AccountWindowBounds.Should().ContainKey(accountKey);
        state.AccountWindowBounds[accountKey].Left.Should().Be(250);
        state.AccountWindowBounds[accountKey].Width.Should().Be(1000);

        // Window closes -> dirty state is flushed immediately
        service.ProcessInstances([], settings, state, () => saveCount++);
        saveCount.Should().BeGreaterThanOrEqualTo(1);

        // Relaunch on a new HWND -> restores saved bounds once
        nint hwnd2 = 0x3002;
        interop.RegisterWindow(hwnd2, new User32.RECT { Left = 0, Top = 0, Right = 800, Bottom = 600 }, zoomed: false, iconic: false);
        var inst2 = inst1 with { ProcessId = 1002, WindowHandle = hwnd2 };

        service.ProcessInstances([inst2], settings, state, () => saveCount++);
        interop.SetWindowPosCalls.Should().Be(1);
        interop.GetNormalRect(hwnd2).Left.Should().Be(250);
        interop.GetNormalRect(hwnd2).Top.Should().Be(140);
        interop.GetNormalRect(hwnd2).Width.Should().Be(1000);
        interop.GetNormalRect(hwnd2).Height.Should().Be(700);

        // Subsequent refresh does not repeatedly call SetWindowPos
        service.ProcessInstances([inst2], settings, state, () => saveCount++);
        interop.SetWindowPosCalls.Should().Be(1);
    }

    private sealed class MockMonitorProvider(
        IReadOnlyList<MonitorInfo> monitors,
        MonitorInfo primary,
        Func<nint, MonitorInfo> fromWindow) : IMonitorProvider
    {
        public IReadOnlyList<MonitorInfo> GetMonitors() => monitors;
        public MonitorInfo GetPrimaryMonitor() => primary;
        public MonitorInfo GetMonitorFromCursor() => primary;
        public MonitorInfo GetMonitorFromWindow(nint hWnd) => fromWindow(hWnd);
        public MonitorInfo? FindMonitorByDeviceName(string? deviceName) =>
            monitors.FirstOrDefault(m => string.Equals(m.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class LayoutMockInterop : IWin32WindowInterop
    {
        private readonly Dictionary<nint, (User32.RECT Rect, bool Zoomed, bool Iconic)> _windows = new();
        public int SetWindowPosCalls { get; private set; }
        public List<(nint Hwnd, int Cmd)> ShowWindowHistory { get; } = new();

        public void RegisterWindow(nint hwnd, User32.RECT rect, bool zoomed, bool iconic) =>
            _windows[hwnd] = (rect, zoomed, iconic);

        public void SetWindowState(nint hwnd, User32.RECT rect, bool zoomed, bool iconic) =>
            _windows[hwnd] = (rect, zoomed, iconic);

        public User32.RECT GetNormalRect(nint hwnd) => _windows[hwnd].Rect;

        public bool IsWindow(nint hWnd) => _windows.ContainsKey(hWnd);
        public bool IsWindowVisible(nint hWnd) => _windows.ContainsKey(hWnd);
        public uint GetWindowThreadProcessId(nint hWnd, out uint processId) { processId = 0; return 1; }
        public string GetClassName(nint hWnd) => "WINDOWSCLIENT";
        public string GetWindowText(nint hWnd) => "Roblox";
        public bool SetWindowText(nint hWnd, string text) => true;
        public nint GetWindowLongPtr(nint hWnd, int nIndex) => nint.Zero;
        public nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong) => nint.Zero;
        public bool GetWindowRect(nint hWnd, out User32.RECT rect)
        {
            if (_windows.TryGetValue(hWnd, out var state))
            {
                rect = state.Rect;
                return true;
            }
            rect = default;
            return false;
        }
        public bool TryGetNormalWindowRect(nint hWnd, out User32.RECT rect) => GetWindowRect(hWnd, out rect);
        public bool IsZoomed(nint hWnd) => _windows.TryGetValue(hWnd, out var state) && state.Zoomed;
        public bool IsIconic(nint hWnd) => _windows.TryGetValue(hWnd, out var state) && state.Iconic;
        public bool ShowWindow(nint hWnd, int nCmdShow)
        {
            ShowWindowHistory.Add((hWnd, nCmdShow));
            if (_windows.TryGetValue(hWnd, out var state))
            {
                if (nCmdShow == User32.SW_SHOWMAXIMIZED)
                {
                    _windows[hWnd] = (state.Rect, true, false);
                }
                else if (nCmdShow == User32.SW_RESTORE)
                {
                    _windows[hWnd] = (state.Rect, false, false);
                }
            }
            return true;
        }
        public User32.RECT GetMonitorRectFromWindow(nint hWnd) => PrimaryMonitor.MonitorBounds;
        public int GetSystemMetrics(int nIndex) => 1920;
        public bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags)
        {
            SetWindowPosCalls++;
            if (_windows.TryGetValue(hWnd, out var state))
            {
                _windows[hWnd] = (new User32.RECT { Left = x, Top = y, Right = x + cx, Bottom = y + cy }, state.Zoomed, state.Iconic);
                return true;
            }
            return false;
        }
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
