using System.Collections.Concurrent;
using BaBaBooeyStrap.Core.Interop;
using BaBaBooeyStrap.Integrations.Window;
using BaBaBooeyStrap.Tests.Mocks;
using BaBaBooeyStrap.Tests.Tier1_FeatureCoverage.Engines;

namespace BaBaBooeyStrap.Tests.Integrations;

public sealed class WindowManipulationTests : IDisposable
{
    private readonly MockRobloxEnvironment _env = new();

    [Fact]
    public void TitleFormatter_InterpolatesPlaceholdersCaseInsensitively()
    {
        string result = WindowTitleFormatter.Format(
            "BaBaBooeyStrap - {accountname} - {PlaceName} ({Unknown})",
            new Dictionary<string, string>
            {
                ["AccountName"] = "PlayerOne",
                ["placename"] = "Natural Disaster Survival"
            });

        result.Should().Be("BaBaBooeyStrap - PlayerOne - Natural Disaster Survival ({Unknown})");
        WindowTitleFormatter.Format("   ", null).Should().BeEmpty();
    }

    [Fact]
    public void SetCustomTitle_SkipsRedundantWrites_ToPreventNameChangeReentrancyLoop()
    {
        var mockInterop = new MockWin32WindowInterop();
        nint hWnd = 0x1234;
        mockInterop.RegisterWindow(hWnd, "WINDOWSCLIENT", "Roblox", 0x00CF0000, 0, new User32.RECT { Left = 0, Top = 0, Right = 800, Bottom = 600 });

        using var service = new WindowManipulationService(mockInterop);
        var placeholders = new Dictionary<string, string> { ["PlaceName"] = "Doors" };

        bool first = service.SetCustomTitle(hWnd, "{PlaceName} | BaBaBooeyStrap", placeholders);
        bool second = service.SetCustomTitle(hWnd, "{PlaceName} | BaBaBooeyStrap", placeholders);

        first.Should().BeTrue();
        second.Should().BeTrue();
        mockInterop.SetWindowTextCalls.Should().Be(1);
        mockInterop.GetWindowText(hWnd).Should().Be("Doors | BaBaBooeyStrap");

        // Invalid & empty checks
        service.SetCustomTitle(nint.Zero, "Title", placeholders).Should().BeFalse();
        service.SetCustomTitle(hWnd, "   ", placeholders).Should().BeFalse();
    }

    [Fact]
    public void SetCustomIcon_ValidatesIcoHeader_TracksAndDestroysOwnedIcons()
    {
        var mockInterop = new MockWin32WindowInterop();
        nint hWnd = 0x2234;
        mockInterop.RegisterWindow(hWnd, "WINDOWSCLIENT", "Roblox", 0x00CF0000, 0, new User32.RECT { Left = 0, Top = 0, Right = 800, Bottom = 600 });

        string validIco = Path.Combine(_env.RootPath, "valid.ico");
        // Minimal valid ICONDIR header (00 00 01 00 01 00) + padding
        File.WriteAllBytes(validIco, new byte[] { 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 16, 16, 0, 0, 1, 0, 32, 0 });

        string badIco = Path.Combine(_env.RootPath, "corrupt.ico");
        File.WriteAllBytes(badIco, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A });

        using var service = new WindowManipulationService(mockInterop);

        // Corrupt & missing files fail safely
        service.SetCustomIcon(hWnd, badIco).Should().BeFalse();
        service.SetCustomIcon(hWnd, Path.Combine(_env.RootPath, "missing.ico")).Should().BeFalse();

        // First valid icon applied
        service.SetCustomIcon(hWnd, validIco).Should().BeTrue();
        mockInterop.WmSetIconMessages.Count.Should().Be(2); // ICON_SMALL + ICON_BIG

        // Replace icon -> old handles destroyed
        service.SetCustomIcon(hWnd, validIco).Should().BeTrue();
        mockInterop.DestroyedIcons.Count.Should().Be(2);

        // Dispose -> remaining owned handles destroyed
        service.Dispose();
        mockInterop.DestroyedIcons.Count.Should().Be(4);
    }

    [Fact]
    public void SetBorderlessFullscreen_MultiMonitorNegativeCoords_TogglesAndRestoresOriginalState()
    {
        var mockInterop = new MockWin32WindowInterop();
        nint hWnd = 0x3334;
        var originalRect = new User32.RECT { Left = 100, Top = 100, Right = 1380, Bottom = 820 };
        var monitorRect = new User32.RECT { Left = -1920, Top = 0, Right = 0, Bottom = 1080 };

        mockInterop.RegisterWindow(
            hWnd,
            "WINDOWSCLIENT",
            "Roblox",
            User32.WS_OVERLAPPEDWINDOW | User32.WS_VISIBLE,
            User32.WS_EX_WINDOWEDGE,
            originalRect,
            monitorRect);

        using var service = new WindowManipulationService(mockInterop);

        // Enable borderless fullscreen
        service.SetBorderlessFullscreen(hWnd, true).Should().BeTrue();

        uint activeStyle = unchecked((uint)mockInterop.GetWindowLongPtr(hWnd, User32.GWL_STYLE).ToInt64());
        (activeStyle & User32.WS_POPUP).Should().Be(User32.WS_POPUP);
        (activeStyle & User32.WS_CAPTION).Should().Be(0u);
        (activeStyle & User32.WS_THICKFRAME).Should().Be(0u);

        mockInterop.LastSetWindowPos.Should().Be((-1920, 0, 1920, 1080));

        // Disable borderless fullscreen -> restores exact original style and bounds
        service.SetBorderlessFullscreen(hWnd, false).Should().BeTrue();

        uint restoredStyle = unchecked((uint)mockInterop.GetWindowLongPtr(hWnd, User32.GWL_STYLE).ToInt64());
        restoredStyle.Should().Be(User32.WS_OVERLAPPEDWINDOW | User32.WS_VISIBLE);
        mockInterop.LastSetWindowPos.Should().Be((100, 100, 1280, 720));
    }

    [Fact]
    public void HookLifecycle_MessagePumpThreadAndNativeTestWindow_AreThreadSafeAndIdempotent()
    {
        using var nativeWin = new NativeTestWindow("HookTestWindow", visible: true);
        using var realService = new WindowManipulationService(new Win32WindowInterop(), new WindowManipulationOptions
        {
            CustomTitleTemplate = "Custom - {PlaceName}",
            TitlePlaceholders = new Dictionary<string, string> { ["PlaceName"] = "Arena" }
        });

        // Start/stop/double-stop/double-dispose must be thread-safe and never throw
        realService.StartHook();
        realService.IsHookRunning.Should().BeTrue();
        realService.StartHook(); // idempotent
        realService.StopHook();
        realService.IsHookRunning.Should().BeFalse();
        realService.StopHook(); // idempotent

        // Verify simulated WinEvent dispatch via WindowPredicate
        var mockInterop = new MockWin32WindowInterop();
        nint hWnd = 0x4444;
        mockInterop.RegisterWindow(hWnd, "WINDOWSCLIENT", "Roblox", User32.WS_OVERLAPPEDWINDOW, 0, new User32.RECT { Left = 0, Top = 0, Right = 800, Bottom = 600 });

        using var hookSimService = new WindowManipulationService(mockInterop, new WindowManipulationOptions
        {
            CustomTitleTemplate = "BaBaBooeyStrap - {OriginalTitle}",
            BorderlessFullscreen = true,
            WindowPredicate = (_, _, cls, _) => cls == "WINDOWSCLIENT"
        });

        hookSimService.HandleWinEvent(nint.Zero, User32.EVENT_OBJECT_CREATE, hWnd, User32.OBJID_WINDOW, User32.CHILDID_SELF, 1, 1);
        mockInterop.GetWindowText(hWnd).Should().Be("BaBaBooeyStrap - Roblox");
        uint style = unchecked((uint)mockInterop.GetWindowLongPtr(hWnd, User32.GWL_STYLE).ToInt64());
        (style & User32.WS_POPUP).Should().Be(User32.WS_POPUP);
    }

    [Fact]
    public void SetCustomTitle_WithOriginalTitleTemplate_CalledTwiceOrOnNameChange_DoesNotRecursivelyWrap()
    {
        var mockInterop = new MockWin32WindowInterop();
        nint hWnd = 0x5555;
        mockInterop.RegisterWindow(hWnd, "WINDOWSCLIENT", "Original", User32.WS_OVERLAPPEDWINDOW, 0, new User32.RECT { Left = 0, Top = 0, Right = 800, Bottom = 600 });

        using var directService = new WindowManipulationService(mockInterop);
        var emptyPlaceholders = new Dictionary<string, string>();

        directService.SetCustomTitle(hWnd, "{OriginalTitle} - Mod", emptyPlaceholders).Should().BeTrue();
        mockInterop.GetWindowText(hWnd).Should().Be("Original - Mod");

        // Second direct call must keep "Original - Mod", never "Original - Mod - Mod"
        directService.SetCustomTitle(hWnd, "{OriginalTitle} - Mod", emptyPlaceholders).Should().BeTrue();
        mockInterop.GetWindowText(hWnd).Should().Be("Original - Mod");
        mockInterop.SetWindowTextCalls.Should().Be(1);

        // Changing template after customization still uses the true original title
        directService.SetCustomTitle(hWnd, "{OriginalTitle} - Alt", emptyPlaceholders).Should().BeTrue();
        mockInterop.GetWindowText(hWnd).Should().Be("Original - Alt");

        // Simulate WinEventHook EVENT_OBJECT_CREATE followed by multiple EVENT_OBJECT_NAMECHANGE events
        nint hookHwnd = 0x6666;
        mockInterop.RegisterWindow(hookHwnd, "WINDOWSCLIENT", "Original", User32.WS_OVERLAPPEDWINDOW, 0, new User32.RECT { Left = 0, Top = 0, Right = 800, Bottom = 600 });

        using var hookService = new WindowManipulationService(mockInterop, new WindowManipulationOptions
        {
            CustomTitleTemplate = "{OriginalTitle} - Mod",
            WindowPredicate = (_, _, cls, _) => cls == "WINDOWSCLIENT"
        });

        hookService.HandleWinEvent(nint.Zero, User32.EVENT_OBJECT_CREATE, hookHwnd, User32.OBJID_WINDOW, User32.CHILDID_SELF, 1, 1);
        mockInterop.GetWindowText(hookHwnd).Should().Be("Original - Mod");

        int callsAfterCreate = mockInterop.SetWindowTextCalls;
        hookService.HandleWinEvent(nint.Zero, User32.EVENT_OBJECT_NAMECHANGE, hookHwnd, User32.OBJID_WINDOW, User32.CHILDID_SELF, 1, 2);
        hookService.HandleWinEvent(nint.Zero, User32.EVENT_OBJECT_NAMECHANGE, hookHwnd, User32.OBJID_WINDOW, User32.CHILDID_SELF, 1, 3);

        mockInterop.GetWindowText(hookHwnd).Should().Be("Original - Mod");
        mockInterop.SetWindowTextCalls.Should().Be(callsAfterCreate);

        // Destroy cleans up stored title state; subsequent window with same handle starts fresh
        hookService.HandleWinEvent(nint.Zero, User32.EVENT_OBJECT_DESTROY, hookHwnd, User32.OBJID_WINDOW, User32.CHILDID_SELF, 1, 4);
        mockInterop.SetWindowText(hookHwnd, "FreshTitle");
        hookService.HandleWinEvent(nint.Zero, User32.EVENT_OBJECT_CREATE, hookHwnd, User32.OBJID_WINDOW, User32.CHILDID_SELF, 1, 5);
        mockInterop.GetWindowText(hookHwnd).Should().Be("FreshTitle - Mod");
    }

    [Fact]
    public void StartHookAndStopHook_TwentyRapidCycles_CompleteWithoutLockInversionStalls()
    {
        using var service = new WindowManipulationService(new Win32WindowInterop(), new WindowManipulationOptions
        {
            WindowPredicate = (_, _, _, _) => false
        });

        var totalSw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 20; i++)
        {
            var cycleSw = System.Diagnostics.Stopwatch.StartNew();
            service.StartHook();
            service.IsHookRunning.Should().BeTrue();
            service.StopHook();
            service.IsHookRunning.Should().BeFalse();
            cycleSw.Stop();

            cycleSw.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(500), $"cycle {i} stalled on StartHook/StopHook");
        }
        totalSw.Stop();

        totalSw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task SetCustomTitle_InFlightSyncAndConcurrentNameChange_DoesNotRecursivelyExpand()
    {
        // Case 1: Synchronous re-entrant EVENT_OBJECT_NAMECHANGE fired during SetWindowText
        var syncInterop = new MockWin32WindowInterop();
        nint syncHwnd = 0x7777;
        syncInterop.RegisterWindow(syncHwnd, "WINDOWSCLIENT", "Original", User32.WS_OVERLAPPEDWINDOW, 0, new User32.RECT { Left = 0, Top = 0, Right = 800, Bottom = 600 });

        using var syncService = new WindowManipulationService(syncInterop, new WindowManipulationOptions
        {
            CustomTitleTemplate = "{OriginalTitle} - Mod",
            WindowPredicate = (_, _, cls, _) => cls == "WINDOWSCLIENT"
        });

        int reentrantTriggerCount = 0;
        syncInterop.OnAfterTitleMutated = hwnd =>
        {
            if (Interlocked.Increment(ref reentrantTriggerCount) <= 5)
            {
                syncService.HandleWinEvent(nint.Zero, User32.EVENT_OBJECT_NAMECHANGE, hwnd, User32.OBJID_WINDOW, User32.CHILDID_SELF, 1, 1);
            }
        };

        syncService.SetCustomTitle(syncHwnd, "{OriginalTitle} - Mod", new Dictionary<string, string>()).Should().BeTrue();
        syncInterop.GetWindowText(syncHwnd).Should().Be("Original - Mod");
        syncInterop.SetWindowTextCalls.Should().Be(1);

        // Case 2: Cross-thread concurrent EVENT_OBJECT_NAMECHANGE fired while SetWindowText is in flight
        var concurrentInterop = new MockWin32WindowInterop();
        nint concurrentHwnd = 0x8888;
        concurrentInterop.RegisterWindow(concurrentHwnd, "WINDOWSCLIENT", "Original", User32.WS_OVERLAPPEDWINDOW, 0, new User32.RECT { Left = 0, Top = 0, Right = 800, Bottom = 600 });

        using var concurrentService = new WindowManipulationService(concurrentInterop, new WindowManipulationOptions
        {
            CustomTitleTemplate = "{OriginalTitle} - Mod",
            WindowPredicate = (_, _, cls, _) => cls == "WINDOWSCLIENT"
        });

        using var enteredSetWindowText = new ManualResetEventSlim(false);
        using var allowSetWindowTextReturn = new ManualResetEventSlim(false);

        concurrentInterop.OnAfterTitleMutated = _ =>
        {
            enteredSetWindowText.Set();
            allowSetWindowTextReturn.Wait(TimeSpan.FromSeconds(2)).Should().BeTrue();
        };

        var outerTask = Task.Run(() =>
            concurrentService.SetCustomTitle(concurrentHwnd, "{OriginalTitle} - Mod", new Dictionary<string, string>()));

        enteredSetWindowText.Wait(TimeSpan.FromSeconds(2)).Should().BeTrue();

        // Simulate concurrent hook thread firing EVENT_OBJECT_NAMECHANGE before outer SetWindowText returns
        concurrentService.HandleWinEvent(nint.Zero, User32.EVENT_OBJECT_NAMECHANGE, concurrentHwnd, User32.OBJID_WINDOW, User32.CHILDID_SELF, 1, 2);

        allowSetWindowTextReturn.Set();
        bool outerResult = await outerTask.WaitAsync(TimeSpan.FromSeconds(2));
        outerResult.Should().BeTrue();

        concurrentInterop.GetWindowText(concurrentHwnd).Should().Be("Original - Mod");
        concurrentInterop.SetWindowTextCalls.Should().Be(1);
    }

    [Fact]
    public void SetCustomTitle_WhenSetWindowTextFails_RevertsPendingAppliedTitleAndPreservesOriginalTitle()
    {
        var mockInterop = new MockWin32WindowInterop();
        nint hWnd = 0x9999;
        mockInterop.RegisterWindow(hWnd, "WINDOWSCLIENT", "Original", User32.WS_OVERLAPPEDWINDOW, 0, new User32.RECT { Left = 0, Top = 0, Right = 800, Bottom = 600 });

        using var service = new WindowManipulationService(mockInterop);
        var empty = new Dictionary<string, string>();

        mockInterop.FailSetWindowText = true;
        service.SetCustomTitle(hWnd, "{OriginalTitle} - Mod", empty).Should().BeFalse();
        mockInterop.GetWindowText(hWnd).Should().Be("Original");

        mockInterop.FailSetWindowText = false;
        service.SetCustomTitle(hWnd, "{OriginalTitle} - Mod", empty).Should().BeTrue();
        mockInterop.GetWindowText(hWnd).Should().Be("Original - Mod");

        // Subsequent update fails -> reverts back to "Original - Mod" so a retry with the original template still sees "Original - Mod" as applied
        mockInterop.FailSetWindowText = true;
        service.SetCustomTitle(hWnd, "{OriginalTitle} - Alt", empty).Should().BeFalse();
        mockInterop.GetWindowText(hWnd).Should().Be("Original - Mod");

        mockInterop.FailSetWindowText = false;
        service.SetCustomTitle(hWnd, "{OriginalTitle} - Alt", empty).Should().BeTrue();
        mockInterop.GetWindowText(hWnd).Should().Be("Original - Alt");
    }

    public void Dispose()
    {
        _env.Dispose();
    }

    private sealed class MockWin32WindowInterop : IWin32WindowInterop
    {
        private sealed class WindowRecord
        {
            public string ClassName { get; set; } = string.Empty;
            public string Title { get; set; } = string.Empty;
            public uint Style { get; set; }
            public uint ExStyle { get; set; }
            public User32.RECT Bounds { get; set; }
            public User32.RECT MonitorBounds { get; set; }
        }

        private readonly ConcurrentDictionary<nint, WindowRecord> _windows = new();
        private long _nextIconHandle = 0x10000;

        public int SetWindowTextCalls { get; private set; }
        public bool FailSetWindowText { get; set; }
        public Action<nint>? OnAfterTitleMutated { get; set; }
        public List<(nint HWnd, nint Type, nint HIcon)> WmSetIconMessages { get; } = new();
        public List<nint> DestroyedIcons { get; } = new();
        public (int X, int Y, int Cx, int Cy) LastSetWindowPos { get; private set; }

        public void RegisterWindow(
            nint hWnd,
            string className,
            string title,
            uint style,
            uint exStyle,
            User32.RECT bounds,
            User32.RECT? monitorBounds = null)
        {
            _windows[hWnd] = new WindowRecord
            {
                ClassName = className,
                Title = title,
                Style = style,
                ExStyle = exStyle,
                Bounds = bounds,
                MonitorBounds = monitorBounds ?? new User32.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 }
            };
        }

        public bool IsWindow(nint hWnd) => _windows.ContainsKey(hWnd);
        public bool IsWindowVisible(nint hWnd) => _windows.ContainsKey(hWnd);
        public uint GetWindowThreadProcessId(nint hWnd, out uint processId) { processId = 9999; return 1; }
        public string GetClassName(nint hWnd) => _windows.TryGetValue(hWnd, out var w) ? w.ClassName : string.Empty;
        public string GetWindowText(nint hWnd) => _windows.TryGetValue(hWnd, out var w) ? w.Title : string.Empty;

        public bool SetWindowText(nint hWnd, string text)
        {
            SetWindowTextCalls++;
            if (FailSetWindowText) return false;
            if (!_windows.TryGetValue(hWnd, out var w)) return false;
            w.Title = text;
            OnAfterTitleMutated?.Invoke(hWnd);
            return true;
        }

        public nint GetWindowLongPtr(nint hWnd, int nIndex)
        {
            if (!_windows.TryGetValue(hWnd, out var w)) return nint.Zero;
            return nIndex == User32.GWL_EXSTYLE ? unchecked((nint)w.ExStyle) : unchecked((nint)w.Style);
        }

        public nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong)
        {
            if (!_windows.TryGetValue(hWnd, out var w)) return nint.Zero;
            if (nIndex == User32.GWL_EXSTYLE)
            {
                nint prev = unchecked((nint)w.ExStyle);
                w.ExStyle = unchecked((uint)dwNewLong.ToInt64());
                return prev;
            }
            else
            {
                nint prev = unchecked((nint)w.Style);
                w.Style = unchecked((uint)dwNewLong.ToInt64());
                return prev;
            }
        }

        public bool GetWindowRect(nint hWnd, out User32.RECT rect)
        {
            if (_windows.TryGetValue(hWnd, out var w))
            {
                rect = w.Bounds;
                return true;
            }
            rect = default;
            return false;
        }

        public User32.RECT GetMonitorRectFromWindow(nint hWnd) =>
            _windows.TryGetValue(hWnd, out var w) ? w.MonitorBounds : new User32.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };

        public int GetSystemMetrics(int nIndex) => nIndex switch
        {
            User32.SM_CXSMICON or User32.SM_CYSMICON => 16,
            User32.SM_CXICON or User32.SM_CYICON => 32,
            _ => 1920
        };

        public bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags)
        {
            if (!_windows.TryGetValue(hWnd, out var w)) return false;
            if ((flags & User32.SWP_NOMOVE) == 0 && (flags & User32.SWP_NOSIZE) == 0)
            {
                w.Bounds = new User32.RECT { Left = x, Top = y, Right = x + cx, Bottom = y + cy };
                LastSetWindowPos = (x, y, cx, cy);
            }
            return true;
        }

        public nint SendMessage(nint hWnd, uint msg, nint wParam, nint lParam)
        {
            if (msg == User32.WM_SETICON) WmSetIconMessages.Add((hWnd, wParam, lParam));
            return nint.Zero;
        }

        public nint LoadImageIcon(string filePath, int cx, int cy) =>
            (nint)Interlocked.Increment(ref _nextIconHandle);

        public (nint SmallIcon, nint LargeIcon) ExtractFirstIconPair(string executablePath) =>
            ((nint)Interlocked.Increment(ref _nextIconHandle), (nint)Interlocked.Increment(ref _nextIconHandle));

        public bool DestroyIcon(nint hIcon)
        {
            DestroyedIcons.Add(hIcon);
            return true;
        }

        public nint SetWinEventHook(uint eventMin, uint eventMax, User32.WinEventDelegate callback, uint idProcess, uint idThread, uint flags) => 1;
        public bool UnhookWinEvent(nint hWinEventHook) => true;
        public uint GetCurrentThreadId() => 1;
        public bool PeekMessage(out User32.MSG msg, nint hWnd, uint filterMin, uint filterMax, uint removeMsg) { msg = default; return false; }
        public int GetMessage(out User32.MSG msg, nint hWnd, uint filterMin, uint filterMax) { msg = default; return 0; }
        public bool TranslateMessage(ref User32.MSG msg) => false;
        public nint DispatchMessage(ref User32.MSG msg) => nint.Zero;
        public bool PostThreadMessage(uint idThread, uint msg, nint wParam, nint lParam) => true;
    }
}
