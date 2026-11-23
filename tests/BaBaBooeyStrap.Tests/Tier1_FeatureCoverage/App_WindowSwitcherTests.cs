using System.Net;
using System.Net.Http;
using System.Text;
using BaBaBooeyStrap.App.Services;
using BaBaBooeyStrap.App.ViewModels.Sections;
using BaBaBooeyStrap.App.Views.Dialogs;
using BaBaBooeyStrap.Bootstrapper.Settings;
using BaBaBooeyStrap.Core;
using BaBaBooeyStrap.Core.Interop;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Models.Enums;
using BaBaBooeyStrap.Core.Roblox;
using BaBaBooeyStrap.Core.Storage;
using BaBaBooeyStrap.Integrations.Window;
using BaBaBooeyStrap.MultiInstance;
using BaBaBooeyStrap.Tests.Mocks;

namespace BaBaBooeyStrap.Tests.Tier1_FeatureCoverage;

public sealed class App_WindowSwitcherTests : IDisposable
{
    private readonly MockRobloxEnvironment _env = new();

    [Theory]
    [InlineData("Ctrl+Alt+Space", User32.MOD_CONTROL | User32.MOD_ALT | User32.MOD_NOREPEAT, 0x20u, "Ctrl+Alt+Space")]
    [InlineData("ctrl + alt + right", User32.MOD_CONTROL | User32.MOD_ALT | User32.MOD_NOREPEAT, 0x27u, "Ctrl+Alt+Right")]
    [InlineData("Ctrl+Alt+Left", User32.MOD_CONTROL | User32.MOD_ALT | User32.MOD_NOREPEAT, 0x25u, "Ctrl+Alt+Left")]
    [InlineData("Alt+~", User32.MOD_ALT | User32.MOD_NOREPEAT, 0xC0u, "Alt+~")]
    [InlineData("Ctrl+Shift+F5", User32.MOD_CONTROL | User32.MOD_SHIFT | User32.MOD_NOREPEAT, 0x74u, "Ctrl+Shift+F5")]
    public void HotkeyParser_ValidCombinations_ParseCorrectly(
        string input, uint expectedMods, uint expectedVk, string expectedNorm)
    {
        bool ok = HotkeyParser.TryParse(input, out var parsed);

        ok.Should().BeTrue();
        parsed.Modifiers.Should().Be(expectedMods);
        parsed.VirtualKey.Should().Be(expectedVk);
        parsed.NormalizedText.Should().Be(expectedNorm);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Space")]
    [InlineData("A")]
    [InlineData("Ctrl+Alt")]
    [InlineData("Ctrl+Alt+A+B")]
    [InlineData("NotAMod+Space")]
    public void HotkeyParser_InvalidOrUnmodifiedKeys_AreRejected(string input)
    {
        HotkeyParser.TryParse(input, out _).Should().BeFalse();
    }

    [Fact]
    public void HotkeyParser_ModifiersOnly_ParsesAndRejectsRegularKeys()
    {
        HotkeyParser.TryParseModifiersOnly("Ctrl+Alt", out uint mods, out string norm).Should().BeTrue();
        mods.Should().Be(User32.MOD_CONTROL | User32.MOD_ALT | User32.MOD_NOREPEAT);
        norm.Should().Be("Ctrl+Alt");

        HotkeyParser.TryParseModifiersOnly("Ctrl+Alt+1", out _, out _).Should().BeFalse();
        HotkeyParser.TryParseModifiersOnly("", out _, out _).Should().BeFalse();
    }

    [Fact]
    public void NotifyIconWrapper_HeadlessMode_TracksRunningWindowLabelsAndMenuActions()
    {
        var running = new List<RunningRobloxInstance>
        {
            new(1001, 0x101, 1, null, 11, "Alpha", "Alpha", null, false, false, true, @"\\.\DISPLAY1", 1, DateTimeOffset.UtcNow),
            new(1002, 0x102, 2, null, null, null, null, null, true, true, false, @"\\.\DISPLAY2", 2, DateTimeOffset.UtcNow)
        };

        using var tray = new NotifyIconWrapper(
            headlessMode: true,
            getRunningInstances: () => running);

        tray.Initialize();

        tray.ContextMenuActions.Should().Contain(["Running Windows", "Tile Windows", "Toggle Floating Bar"]);
        tray.LastRunningWindowLabels.Should().Equal("#1 Alpha [M1]", "#2 Roblox [M2]");
    }

    [Fact]
    public void WindowSwitcherCoordinator_CycleAndSlotAndTiling_OperateCleanlyHeadless()
    {
        var settingsManager = new JsonManager<Settings>(Path.Combine(_env.RootPath, "settings.json"));
        var stateManager = new JsonManager<State>(Path.Combine(_env.RootPath, "state.json"));
        var interop = new CoordinatorMockInterop();

        var mon1 = new MonitorInfo(1, @"\\.\DISPLAY1", true,
            new User32.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 },
            new User32.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1040 });
        var mon2 = new MonitorInfo(2, @"\\.\DISPLAY2", false,
            new User32.RECT { Left = 1920, Top = 0, Right = 3840, Bottom = 1080 },
            new User32.RECT { Left = 1920, Top = 0, Right = 3840, Bottom = 1080 });

        var monitorProvider = new MultiMonitorStub([mon1, mon2]);
        nint h1 = 0x8001;
        nint h2 = 0x8002;
        interop.Register(h1, "Roblox", 101);
        interop.Register(h2, "Roblox", 102);
        interop.SetForegroundWindow(h1);

        var observed = new List<ObservedRobloxWindow>
        {
            new(101, h1, "Roblox", false, true, mon1),
            new(102, h2, "Roblox", false, false, mon1)
        };

        using var tracker = new RunningInstanceTracker(
            () => settingsManager.Prop,
            interop,
            monitorProvider,
            windowProbe: () => observed.Select(w => w with
            {
                IsFocused = interop.GetForegroundWindow() == w.WindowHandle,
                WindowTitle = interop.GetWindowText(w.WindowHandle)
            }).ToList());

        tracker.RegisterLaunchedAccount(101, new RobloxAccount { UserId = 1, Username = "First" });
        tracker.RegisterLaunchedAccount(102, new RobloxAccount { UserId = 2, Username = "Second" });

        using var coordinator = new WindowSwitcherCoordinator(
            tracker,
            new WindowFocusHelper(interop),
            new WindowTilingService(interop, monitorProvider),
            monitorProvider,
            settingsManager,
            stateManager,
            hotkeyService: null,
            trayWrapper: null,
            enableFloatingWindows: false);

        coordinator.Initialize();

        // Cycle advances from focused h1 -> h2 -> h1, and backward h1 -> h2 -> h1
        coordinator.CycleNextInstance();
        interop.GetForegroundWindow().Should().Be(h2);

        coordinator.CycleNextInstance();
        interop.GetForegroundWindow().Should().Be(h1);

        coordinator.CyclePreviousInstance();
        interop.GetForegroundWindow().Should().Be(h2);

        coordinator.CyclePreviousInstance();
        interop.GetForegroundWindow().Should().Be(h1);

        // Direct slot focus
        coordinator.FocusSlot(2);
        interop.GetForegroundWindow().Should().Be(h2);

        // Tiling Main+Side places currently focused h2 on primary mon1, and h1 on mon2
        int tiled = coordinator.TileInstances(WindowTilingMode.MainAndSide);
        tiled.Should().Be(2);
        interop.GetBounds(h2).Should().Be(mon1.WorkArea);
        interop.GetBounds(h1).Should().Be(mon2.WorkArea);
    }

    [Fact]
    public async Task FollowUserPreviewAndLegacyBackfill_ResolvesCircularAvatarAndPersists()
    {
        var paths = new PathProvider(_env.RootPath);
        paths.EnsureDirectoriesExist();
        var targetStore = new JoinTargetStore(paths.TargetsFile);

        // Pre-seed a legacy FollowUser target without ThumbnailUrl or UserId
        targetStore.Upsert(new SavedJoinTarget
        {
            Name = "LegacyFriend",
            TargetInput = "@LegacyPal"
        });

        using var httpClient = new HttpClient(new StubHandler(req =>
        {
            string uri = req.RequestUri!.ToString();
            if (uri.Contains("usernames/users"))
            {
                return JsonResponse("{\"data\":[{\"id\":777888,\"name\":\"LegacyPal\"}]}");
            }
            if (uri.Contains("/v1/users/777888"))
            {
                return JsonResponse("{\"id\":777888,\"name\":\"LegacyPal\",\"displayName\":\"Legacy Bestie\"}");
            }
            if (uri.Contains("avatar-headshot") && uri.Contains("777888"))
            {
                return JsonResponse("{\"data\":[{\"targetId\":777888,\"imageUrl\":\"https://tr.rbxcdn.com/legacy777.png\"}]}");
            }
            if (uri.Contains("/v1/users/123456"))
            {
                return JsonResponse("{\"id\":123456,\"name\":\"CoolFriend\",\"displayName\":\"Cool Display\"}");
            }
            if (uri.Contains("avatar-headshot") && uri.Contains("123456"))
            {
                return JsonResponse("{\"data\":[{\"targetId\":123456,\"imageUrl\":\"https://tr.rbxcdn.com/cool123.png\"}]}");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        using var authService = new RobloxAuthService(httpClient);
        var vm = new AccountsViewModel(
            new Settings(),
            paths,
            accountStore: null,
            joinTargetStore: targetStore,
            multiInstanceController: null,
            processSupervisor: new ProcessSupervisor(),
            launcher: null,
            globalSettingsManager: new GlobalSettingsManager(paths.RobloxGlobalSettings),
            stateManager: new JsonManager<State>(paths.StateFile),
            setLocalizedStatus: (_, _) => { },
            authService: authService);

        // 1. Backfill resolves legacy saved FollowUser target
        await vm.BackfillMissingTargetThumbnailsAsync();
        vm.SavedTargets.Should().ContainSingle();
        vm.SavedTargets[0].UserId.Should().Be(777888);
        vm.SavedTargets[0].ThumbnailUrl.Should().Be("https://tr.rbxcdn.com/legacy777.png");
        vm.SavedTargets[0].IsUserTarget.Should().BeTrue();

        // 2. Typing a FollowUser ID in FollowUser mode resolves circular avatar + display name
        vm.TargetName = string.Empty;
        vm.IsFollowUserTargetMode = true;
        vm.LaunchTargetInput = "123456";

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!vm.HasResolvedGameInfo && sw.Elapsed < TimeSpan.FromSeconds(3))
        {
            await Task.Delay(30);
        }

        vm.HasResolvedGameInfo.Should().BeTrue();
        vm.IsResolvedPreviewCircular.Should().BeTrue();
        vm.ResolvedGameName.Should().Be("Cool Display (@CoolFriend)");
        vm.ResolvedGameThumbnailUrl.Should().Be("https://tr.rbxcdn.com/cool123.png");

        // 3. Saving target persists avatar & UserId & auto-fills name when blank
        vm.SaveCurrentJoinTarget().Should().BeTrue();
        var saved = vm.SavedTargets.FirstOrDefault(t => t.UserId == 123456);
        saved.Should().NotBeNull();
        saved!.Name.Should().Be("Cool Display (@CoolFriend)");
        saved.ThumbnailUrl.Should().Be("https://tr.rbxcdn.com/cool123.png");
        saved.IsUserTarget.Should().BeTrue();
    }

    [Fact]
    public void GlobalHotkeyService_TryMatchDirectSlotKey_SwallowsPressRepeatAndReleaseAcrossMainAndNumpad()
    {
        uint configured = User32.MOD_CONTROL | User32.MOD_ALT | User32.MOD_NOREPEAT;
        uint exactMods = User32.MOD_CONTROL | User32.MOD_ALT;
        var swallowed = new HashSet<uint>();

        // Initial press of '3' with exact modifiers -> matches slot 3 and swallows
        GlobalHotkeyService.TryMatchDirectSlotKey(
                configured, exactMods, 0x33u, isKeyDown: true, swallowed, out int slot, out bool swallow)
            .Should().BeTrue();
        slot.Should().Be(3);
        swallow.Should().BeTrue();
        swallowed.Should().Contain(0x33u);

        // Auto-repeat KeyDown -> still swallowed, does not re-trigger slot
        GlobalHotkeyService.TryMatchDirectSlotKey(
                configured, exactMods, 0x33u, isKeyDown: true, swallowed, out slot, out swallow)
            .Should().BeTrue();
        slot.Should().Be(0);
        swallow.Should().BeTrue();

        // KeyUp after modifiers were already released -> still swallowed and cleared from set
        GlobalHotkeyService.TryMatchDirectSlotKey(
                configured, currentModifiers: 0u, 0x33u, isKeyDown: false, swallowed, out slot, out swallow)
            .Should().BeTrue();
        slot.Should().Be(0);
        swallow.Should().BeTrue();
        swallowed.Should().NotContain(0x33u);

        // Numpad 7 -> matches slot 7 and swallows
        GlobalHotkeyService.TryMatchDirectSlotKey(
                configured, exactMods, 0x67u, isKeyDown: true, swallowed, out slot, out swallow)
            .Should().BeTrue();
        slot.Should().Be(7);
        swallow.Should().BeTrue();

        // Wrong modifiers or non-digit key -> ignored & not swallowed
        GlobalHotkeyService.TryMatchDirectSlotKey(
                configured, exactMods | User32.MOD_SHIFT, 0x31u, isKeyDown: true, swallowed, out slot, out swallow)
            .Should().BeFalse();
        slot.Should().Be(0);
        swallow.Should().BeFalse();

        GlobalHotkeyService.TryMatchDirectSlotKey(
                configured, exactMods, (uint)'A', isKeyDown: true, swallowed, out slot, out swallow)
            .Should().BeFalse();
        slot.Should().Be(0);
        swallow.Should().BeFalse();
    }

    [Fact]
    public void TrayMenuPositioner_ClampsWithinWorkAreaAndFlipsSubmenuAtEdges()
    {
        var primaryWork = new User32.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1040 };

        // Bottom-right taskbar click opens upward & leftward inside work area (6px margin)
        var (popX, popY) = TrayMenuPositioner.ComputePopupPosition(
            cursorX: 1880,
            cursorY: 1055,
            menuWidth: 300,
            menuHeight: 400,
            primaryWork);
        popX.Should().BeLessThanOrEqualTo(primaryWork.Right - 6 - 300);
        popY.Should().Be(primaryWork.Bottom - 6 - 400);

        // Negative secondary monitor coordinates clamp strictly inside negative bounds
        var leftMonWork = new User32.RECT { Left = -1920, Top = 0, Right = 0, Bottom = 1040 };
        var (negX, negY) = TrayMenuPositioner.ComputePopupPosition(
            cursorX: -20,
            cursorY: 1050,
            menuWidth: 300,
            menuHeight: 360,
            leftMonWork);
        negX.Should().BeInRange(leftMonWork.Left + 6, leftMonWork.Right - 6 - 300);
        negY.Should().Be(leftMonWork.Bottom - 6 - 360);

        // Submenu opens to the right when room exists (4px overlap)
        var (subRightX, subRightY) = TrayMenuPositioner.ComputeSubMenuPosition(
            parentLeft: 400,
            parentRight: 700,
            itemTop: 300,
            subWidth: 260,
            subHeight: 220,
            primaryWork);
        subRightX.Should().Be(700 - 4);
        subRightY.Should().Be(300);

        // Submenu near right edge flips cleanly to the left of parent menu
        var (subFlipX, subFlipY) = TrayMenuPositioner.ComputeSubMenuPosition(
            parentLeft: 1600,
            parentRight: 1900,
            itemTop: 950,
            subWidth: 260,
            subHeight: 220,
            primaryWork);
        subFlipX.Should().Be(1600 - 260 + 4);
        subFlipY.Should().Be(primaryWork.Bottom - 6 - 220);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }

    public void Dispose() => _env.Dispose();

    private sealed class MultiMonitorStub(IReadOnlyList<MonitorInfo> monitors) : IMonitorProvider
    {
        public IReadOnlyList<MonitorInfo> GetMonitors() => monitors;
        public MonitorInfo GetPrimaryMonitor() => monitors[0];
        public MonitorInfo GetMonitorFromCursor() => monitors[0];
        public MonitorInfo GetMonitorFromWindow(nint hWnd) => monitors[0];
        public MonitorInfo? FindMonitorByDeviceName(string? deviceName) =>
            string.IsNullOrWhiteSpace(deviceName)
                ? null
                : monitors.FirstOrDefault(m => string.Equals(m.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class CoordinatorMockInterop : IWin32WindowInterop
    {
        private readonly Dictionary<nint, string> _titles = new();
        private readonly Dictionary<nint, uint> _pids = new();
        private readonly Dictionary<nint, User32.RECT> _bounds = new();
        private nint _fg;

        public void Register(nint hWnd, string title, uint processId = 0)
        {
            _titles[hWnd] = title;
            _pids[hWnd] = processId;
            _bounds[hWnd] = new User32.RECT { Left = 0, Top = 0, Right = 800, Bottom = 600 };
        }

        public User32.RECT GetBounds(nint hWnd) => _bounds[hWnd];
        public bool IsWindow(nint hWnd) => _titles.ContainsKey(hWnd);
        public bool IsWindowVisible(nint hWnd) => _titles.ContainsKey(hWnd);
        public uint GetWindowThreadProcessId(nint hWnd, out uint processId)
        {
            processId = _pids.TryGetValue(hWnd, out uint pid) ? pid : 0;
            return (uint)(hWnd.ToInt64() & 0xFFFF);
        }
        public string GetClassName(nint hWnd) => "WINDOWSCLIENT";
        public string GetWindowText(nint hWnd) => _titles.TryGetValue(hWnd, out string? t) ? t : string.Empty;
        public bool SetWindowText(nint hWnd, string text) { _titles[hWnd] = text; return true; }
        public nint GetWindowLongPtr(nint hWnd, int nIndex) => nint.Zero;
        public nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong) => nint.Zero;
        public bool GetWindowRect(nint hWnd, out User32.RECT rect) => _bounds.TryGetValue(hWnd, out rect);
        public nint GetForegroundWindow() => _fg;
        public bool SetForegroundWindow(nint hWnd) { _fg = hWnd; return true; }
        public bool BringWindowToTop(nint hWnd) => true;
        public bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach) => true;
        public bool AllowSetForegroundWindow(int dwProcessId) => true;
        public bool ShowWindow(nint hWnd, int nCmdShow) => true;
        public User32.RECT GetMonitorRectFromWindow(nint hWnd) => new() { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
        public int GetSystemMetrics(int nIndex) => 1920;
        public bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags)
        {
            if ((flags & User32.SWP_NOMOVE) == 0 && (flags & User32.SWP_NOSIZE) == 0)
            {
                _bounds[hWnd] = new User32.RECT { Left = x, Top = y, Right = x + cx, Bottom = y + cy };
            }
            return true;
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
