using BaBaBooeyStrap.Core.Interop;
using BaBaBooeyStrap.Integrations.Window;

namespace BaBaBooeyStrap.Tests.Tier1_FeatureCoverage;

public sealed class Integrations_WindowTilingServiceTests
{
    [Theory]
    [InlineData(1, 0, 0, 1920, 1040)]
    [InlineData(2, 0, 0, 1920, 1040)]
    [InlineData(3, 0, 0, 1920, 1040)]
    [InlineData(4, -1920, 0, 0, 1080)]
    [InlineData(5, 0, 0, 2560, 1400)]
    [InlineData(6, -2560, -100, 0, 1340)]
    [InlineData(9, 0, 0, 1920, 1080)]
    public void CalculateGridRects_CoversWorkAreaWithoutOverlapOrOutOfBounds(
        int count, int left, int top, int right, int bottom)
    {
        var workArea = new User32.RECT { Left = left, Top = top, Right = right, Bottom = bottom };
        var rects = WindowTilingService.CalculateGridRects(workArea, count);

        rects.Count.Should().Be(count);

        foreach (var r in rects)
        {
            r.Width.Should().BeGreaterThan(0);
            r.Height.Should().BeGreaterThan(0);
            r.Left.Should().BeGreaterOrEqualTo(workArea.Left);
            r.Top.Should().BeGreaterOrEqualTo(workArea.Top);
            r.Right.Should().BeLessOrEqualTo(workArea.Right);
            r.Bottom.Should().BeLessOrEqualTo(workArea.Bottom);
        }

        for (int i = 0; i < rects.Count; i++)
        {
            for (int j = i + 1; j < rects.Count; j++)
            {
                bool overlaps =
                    rects[i].Left < rects[j].Right &&
                    rects[i].Right > rects[j].Left &&
                    rects[i].Top < rects[j].Bottom &&
                    rects[i].Bottom > rects[j].Top;
                overlaps.Should().BeFalse($"rect {i} and {j} should not overlap");
            }
        }
    }

    [Fact]
    public void TileSingleMonitor_RestoresMinimizedAndCompensatesDwmFrameInsets()
    {
        var interop = new TilingMockInterop();
        var mon1 = new MonitorInfo(
            1,
            @"\\.\DISPLAY1",
            true,
            new User32.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 },
            new User32.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1040 });

        var monitors = new FakeMonitorProvider([mon1]);
        var svc = new WindowTilingService(interop, monitors);

        nint w1 = 0x1001;
        nint w2 = 0x1002;
        interop.Register(w1, isIconic: true, leftShadow: 7, rightShadow: 7, bottomShadow: 7);
        interop.Register(w2, isZoomed: true, leftShadow: 0, rightShadow: 0, bottomShadow: 0);

        int count = svc.TileSingleMonitor([w1, w2], mon1);

        count.Should().Be(2);
        interop.IsIconic(w1).Should().BeFalse();
        interop.IsZoomed(w2).Should().BeFalse();

        // w1 has 7px invisible left/right/bottom shadow compensated outward
        var b1 = interop.GetBounds(w1);
        b1.Left.Should().Be(-7);
        b1.Top.Should().Be(0);
        b1.Right.Should().Be(960 + 7);
        b1.Bottom.Should().Be(1040 + 7);

        // w2 sits in right half cleanly
        var b2 = interop.GetBounds(w2);
        b2.Left.Should().Be(960);
        b2.Top.Should().Be(0);
        b2.Right.Should().Be(1920);
        b2.Bottom.Should().Be(1040);
    }

    [Fact]
    public void TileSpreadAcrossMonitors_DistributesEvenlyAcrossTwoAndThreeMonitors()
    {
        var interop = new TilingMockInterop();
        var mon1 = new MonitorInfo(1, @"\\.\DISPLAY1", true,
            new User32.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 },
            new User32.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1040 });
        var mon2 = new MonitorInfo(2, @"\\.\DISPLAY2", false,
            new User32.RECT { Left = -1920, Top = 0, Right = 0, Bottom = 1080 },
            new User32.RECT { Left = -1920, Top = 0, Right = 0, Bottom = 1080 });
        var mon3 = new MonitorInfo(3, @"\\.\DISPLAY3", false,
            new User32.RECT { Left = 1920, Top = 0, Right = 3840, Bottom = 1080 },
            new User32.RECT { Left = 1920, Top = 0, Right = 3840, Bottom = 1080 });

        nint[] hwnds = [0x2001, 0x2002, 0x2003, 0x2004, 0x2005];
        foreach (nint h in hwnds) interop.Register(h);

        // 5 windows across 2 monitors -> 3 on mon1, 2 on mon2
        var svc2 = new WindowTilingService(interop, new FakeMonitorProvider([mon1, mon2]));
        svc2.TileSpreadAcrossMonitors(hwnds).Should().Be(5);
        interop.GetBounds(0x2001).Left.Should().BeGreaterOrEqualTo(0);
        interop.GetBounds(0x2003).Left.Should().BeGreaterOrEqualTo(0);
        interop.GetBounds(0x2004).Right.Should().BeLessOrEqualTo(0);
        interop.GetBounds(0x2005).Right.Should().BeLessOrEqualTo(0);

        // 4 windows across 3 monitors -> 2 on mon1, 1 on mon2, 1 on mon3
        var svc3 = new WindowTilingService(interop, new FakeMonitorProvider([mon1, mon2, mon3]));
        svc3.TileSpreadAcrossMonitors(hwnds.Take(4).ToList()).Should().Be(4);
        interop.GetBounds(0x2001).Left.Should().BeGreaterOrEqualTo(0);
        interop.GetBounds(0x2002).Left.Should().BeGreaterOrEqualTo(0);
        interop.GetBounds(0x2003).Right.Should().BeLessOrEqualTo(0);
        interop.GetBounds(0x2004).Left.Should().BeGreaterOrEqualTo(1920);
    }

    [Fact]
    public void TileMainAndSide_MultiMonitorPlacesMainFullAndSidesGrid_SingleMonitorFallsBackToGrid()
    {
        var interop = new TilingMockInterop();
        var primary = new MonitorInfo(1, @"\\.\DISPLAY1", true,
            new User32.RECT { Left = 0, Top = 0, Right = 2560, Bottom = 1440 },
            new User32.RECT { Left = 0, Top = 0, Right = 2560, Bottom = 1400 });
        var side = new MonitorInfo(2, @"\\.\DISPLAY2", false,
            new User32.RECT { Left = 2560, Top = 0, Right = 4480, Bottom = 1080 },
            new User32.RECT { Left = 2560, Top = 0, Right = 4480, Bottom = 1080 });

        nint main = 0x3001;
        nint alt1 = 0x3002;
        nint alt2 = 0x3003;
        interop.Register(main);
        interop.Register(alt1);
        interop.Register(alt2);

        var multiSvc = new WindowTilingService(interop, new FakeMonitorProvider([primary, side]));
        multiSvc.TileMainAndSide(main, [alt1, alt2]).Should().Be(3);

        var mainBounds = interop.GetBounds(main);
        mainBounds.Should().Be(primary.WorkArea);

        var alt1Bounds = interop.GetBounds(alt1);
        var alt2Bounds = interop.GetBounds(alt2);
        alt1Bounds.Left.Should().Be(2560);
        alt1Bounds.Right.Should().Be(3520);
        alt2Bounds.Left.Should().Be(3520);
        alt2Bounds.Right.Should().Be(4480);

        // Single monitor fallback -> combines main + sides into single-monitor grid
        var singleSvc = new WindowTilingService(interop, new FakeMonitorProvider([primary]));
        singleSvc.TileMainAndSide(main, [alt1, alt2]).Should().Be(3);
        interop.GetBounds(main).Width.Should().BeLessThan(primary.WorkArea.Width);
    }

    [Fact]
    public void WindowFocusHelper_MoveWindowToMonitor_CentersAndActivatesWindow()
    {
        var interop = new TilingMockInterop();
        var helper = new WindowFocusHelper(interop);
        var target = new MonitorInfo(2, @"\\.\DISPLAY2", false,
            new User32.RECT { Left = 1920, Top = 0, Right = 3840, Bottom = 1080 },
            new User32.RECT { Left = 1920, Top = 0, Right = 3840, Bottom = 1040 });

        nint hwnd = 0x4001;
        interop.Register(hwnd, isIconic: true, bounds: new User32.RECT { Left = 100, Top = 100, Right = 900, Bottom = 700 });

        bool moved = helper.MoveWindowToMonitor(hwnd, target);
        moved.Should().BeTrue();
        interop.IsIconic(hwnd).Should().BeFalse();
        interop.GetForegroundWindow().Should().Be(hwnd);

        var bounds = interop.GetBounds(hwnd);
        bounds.Width.Should().Be(800);
        bounds.Height.Should().Be(600);
        bounds.Left.Should().Be(1920 + (1920 - 800) / 2);
        bounds.Top.Should().Be((1040 - 600) / 2);
    }

    private sealed class FakeMonitorProvider(IReadOnlyList<MonitorInfo> monitors) : IMonitorProvider
    {
        public IReadOnlyList<MonitorInfo> GetMonitors() => monitors;
        public MonitorInfo GetPrimaryMonitor() => monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
        public MonitorInfo GetMonitorFromCursor() => monitors[0];
        public MonitorInfo GetMonitorFromWindow(nint hWnd) => monitors[0];
        public MonitorInfo? FindMonitorByDeviceName(string? deviceName) =>
            string.IsNullOrWhiteSpace(deviceName)
                ? null
                : monitors.FirstOrDefault(m => string.Equals(m.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class TilingMockInterop : IWin32WindowInterop
    {
        private sealed class State
        {
            public bool Iconic { get; set; }
            public bool Zoomed { get; set; }
            public User32.RECT Bounds { get; set; }
            public int LeftShadow { get; init; }
            public int RightShadow { get; init; }
            public int BottomShadow { get; init; }
        }

        private readonly Dictionary<nint, State> _windows = new();
        private nint _foreground;

        public void Register(
            nint hwnd,
            bool isIconic = false,
            bool isZoomed = false,
            int leftShadow = 0,
            int rightShadow = 0,
            int bottomShadow = 0,
            User32.RECT? bounds = null)
        {
            _windows[hwnd] = new State
            {
                Iconic = isIconic,
                Zoomed = isZoomed,
                Bounds = bounds ?? new User32.RECT { Left = 50, Top = 50, Right = 850, Bottom = 650 },
                LeftShadow = leftShadow,
                RightShadow = rightShadow,
                BottomShadow = bottomShadow
            };
        }

        public User32.RECT GetBounds(nint hwnd) => _windows[hwnd].Bounds;
        public bool IsWindow(nint hWnd) => _windows.ContainsKey(hWnd);
        public bool IsWindowVisible(nint hWnd) => _windows.ContainsKey(hWnd);
        public uint GetWindowThreadProcessId(nint hWnd, out uint processId) { processId = 1000; return (uint)(hWnd.ToInt64() & 0xFFFF); }
        public string GetClassName(nint hWnd) => "WINDOWSCLIENT";
        public string GetWindowText(nint hWnd) => "Roblox";
        public bool SetWindowText(nint hWnd, string text) => true;
        public nint GetWindowLongPtr(nint hWnd, int nIndex) => nint.Zero;
        public nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong) => nint.Zero;
        public bool GetWindowRect(nint hWnd, out User32.RECT rect)
        {
            if (_windows.TryGetValue(hWnd, out var s)) { rect = s.Bounds; return true; }
            rect = default;
            return false;
        }
        public bool IsIconic(nint hWnd) => _windows.TryGetValue(hWnd, out var s) && s.Iconic;
        public bool IsZoomed(nint hWnd) => _windows.TryGetValue(hWnd, out var s) && s.Zoomed;
        public bool ShowWindow(nint hWnd, int nCmdShow)
        {
            if (!_windows.TryGetValue(hWnd, out var s)) return false;
            if (nCmdShow == User32.SW_RESTORE) { s.Iconic = false; s.Zoomed = false; }
            else if (nCmdShow == User32.SW_MINIMIZE) { s.Iconic = true; }
            return true;
        }
        public nint GetForegroundWindow() => _foreground;
        public bool SetForegroundWindow(nint hWnd) { _foreground = hWnd; return true; }
        public bool BringWindowToTop(nint hWnd) => true;
        public bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach) => true;
        public bool AllowSetForegroundWindow(int dwProcessId) => true;
        public bool TryGetExtendedFrameBounds(nint hWnd, out User32.RECT rect)
        {
            if (!_windows.TryGetValue(hWnd, out var s)) { rect = default; return false; }
            rect = new User32.RECT
            {
                Left = s.Bounds.Left + s.LeftShadow,
                Top = s.Bounds.Top,
                Right = s.Bounds.Right - s.RightShadow,
                Bottom = s.Bounds.Bottom - s.BottomShadow
            };
            return true;
        }
        public User32.RECT GetMonitorRectFromWindow(nint hWnd) => new() { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
        public int GetSystemMetrics(int nIndex) => 1920;
        public bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags)
        {
            if (!_windows.TryGetValue(hWnd, out var s)) return false;
            if ((flags & User32.SWP_NOMOVE) == 0 && (flags & User32.SWP_NOSIZE) == 0)
            {
                s.Bounds = new User32.RECT { Left = x, Top = y, Right = x + cx, Bottom = y + cy };
            }
            return true;
        }
        public nint SendMessage(nint hWnd, uint msg, nint wParam, nint lParam) => nint.Zero;
        public nint LoadImageIcon(string filePath, int cx, int cy) => nint.Zero;
        public (nint SmallIcon, nint LargeIcon) ExtractFirstIconPair(string executablePath) => (nint.Zero, nint.Zero);
        public bool DestroyIcon(nint hIcon) => true;
        public nint SetWinEventHook(uint eventMin, uint eventMax, User32.WinEventDelegate callback, uint idProcess, uint idThread, uint flags) => nint.Zero;
        public bool UnhookWinEvent(nint hWinEventHook) => true;
        public uint GetCurrentThreadId() => 99;
        public bool PeekMessage(out User32.MSG msg, nint hWnd, uint filterMin, uint filterMax, uint removeMsg) { msg = default; return false; }
        public int GetMessage(out User32.MSG msg, nint hWnd, uint filterMin, uint filterMax) { msg = default; return 0; }
        public bool TranslateMessage(ref User32.MSG msg) => false;
        public nint DispatchMessage(ref User32.MSG msg) => nint.Zero;
        public bool PostThreadMessage(uint idThread, uint msg, nint wParam, nint lParam) => true;
    }
}
