using System.Diagnostics;
using System.IO;
using BaBaBooeyStrap.App.Models;
using BaBaBooeyStrap.App.Services;
using BaBaBooeyStrap.App.ViewModels;
using BaBaBooeyStrap.App.Views.Dialogs.Bootstrapper;
using BaBaBooeyStrap.Bootstrapper.Deployment.Models;
using BaBaBooeyStrap.Bootstrapper.FastFlags;
using BaBaBooeyStrap.Core;
using BaBaBooeyStrap.Core.Launch;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Models.Enums;
using BaBaBooeyStrap.Core.Roblox;
using BaBaBooeyStrap.Core.Storage;
using FluentAssertions;
using Xunit;

namespace BaBaBooeyStrap.Tests.App;

[Collection("LocalizationSingleton")]
public sealed class Milestone5AppTests : IDisposable
{
    private readonly string _tempDir;

    public Milestone5AppTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"BBBS_M5_Tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch { }
    }

    [Fact]
    public void LaunchSettings_InstallFlag_AndNonGreedyBooleanFlags_ParseCorrectly()
    {
        var installArgs = new LaunchSettings(["-install", "-quiet"]);
        installArgs.InstallFlag.Active.Should().BeTrue();
        installArgs.QuietFlag.Active.Should().BeTrue();
        installArgs.InstallFlag.Data.Should().BeNull();
        installArgs.QuietFlag.Data.Should().BeNull();

        const string uri = "roblox-player:1+launchmode:play+gameinfo:ticket_xyz+launchtime:12345+placelauncherurl:https%3A%2F%2Fassetgame.roblox.com%2Fgame%2FPlaceLauncher.ashx%3Frequest%3DRequestGame%26placeId%3D1818";
        var quietThenUri = new LaunchSettings(["-quiet", uri]);
        quietThenUri.QuietFlag.Active.Should().BeTrue();
        quietThenUri.QuietFlag.Data.Should().BeNull();
        quietThenUri.RobloxLaunchMode.Should().Be(LaunchMode.Player);
        quietThenUri.RobloxLaunchArgs.Should().Be(uri);

        var menuThenVersion = new LaunchSettings(["-menu", "version-abc123"]);
        menuThenVersion.MenuFlag.Active.Should().BeTrue();
        menuThenVersion.MenuFlag.Data.Should().BeNull();
        menuThenVersion.VersionFlag.Active.Should().BeTrue();
        menuThenVersion.VersionFlag.Data.Should().Be("version-abc123");

        var valueFlags = new LaunchSettings(["-channel", "zflag", "-version", "version-999", "-player", uri]);
        valueFlags.ChannelFlag.Data.Should().Be("zflag");
        valueFlags.VersionFlag.Data.Should().Be("version-999");
        valueFlags.RobloxLaunchMode.Should().Be(LaunchMode.Player);
        valueFlags.RobloxLaunchArgs.Should().Be(uri);
    }

    [Fact]
    public async Task CoreRobloxLauncher_NoLaunchFlag_ReturnsSuccessWithoutSpawning()
    {
        var paths = new PathProvider(_tempDir);
        var launcher = new RobloxLauncher(paths);
        var settings = new LaunchSettings(["-nolaunch", "roblox-player:1+launchmode:play+gameinfo:abc"]);

        var result = await launcher.LaunchPlayerAsync(settings);

        result.Success.Should().BeTrue();
        result.ProcessId.Should().BeNull();
    }

    [Fact]
    public async Task CoreRobloxLauncher_MissingExecutable_ReturnsFailureWithMessage()
    {
        var paths = new PathProvider(_tempDir);
        var launcher = new RobloxLauncher(paths);
        var settings = new LaunchSettings(["roblox-player:1+launchmode:play+gameinfo:abc"]);

        var result = await launcher.LaunchPlayerAsync(settings);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("RobloxPlayerBeta.exe");
    }

    [Fact]
    public async Task CoreRobloxLauncher_NormalizesRobloxDeepLink_AndInvokesProcessStarter()
    {
        var paths = new PathProvider(_tempDir);
        string versionDir = paths.GetVersionDirectory("version-test123");
        Directory.CreateDirectory(versionDir);
        string fakeExe = Path.Combine(versionDir, "RobloxPlayerBeta.exe");
        File.WriteAllBytes(fakeExe, new byte[] { 0x4D, 0x5A });

        ProcessStartInfo? capturedPsi = null;
        var launcher = new RobloxLauncher(
            paths,
            processStarter: psi =>
            {
                capturedPsi = psi;
                return null;
            });

        var settings = new LaunchSettings(["roblox://experiences/start?placeId=1818"]);
        var result = await launcher.LaunchPlayerAsync(settings);

        result.Success.Should().BeTrue();
        result.Target.PlaceId.Should().Be(1818);
        result.LaunchUri.Should().StartWith("roblox-player:1+launchmode:play");
        capturedPsi.Should().NotBeNull();
        capturedPsi!.FileName.Should().Be(fakeExe);
        capturedPsi.Arguments.Should().Contain("roblox-player:1+launchmode:play");
    }

    [Fact]
    public async Task CoreRobloxLauncher_LaunchAccountAsync_ResolvesShareLinkAndAuthTicket()
    {
        var paths = new PathProvider(_tempDir);
        string versionDir = paths.GetVersionDirectory("version-share");
        Directory.CreateDirectory(versionDir);
        File.WriteAllBytes(Path.Combine(versionDir, "RobloxPlayerBeta.exe"), new byte[] { 0x4D, 0x5A });

        var mockAuth = new StubAuthService();
        ProcessStartInfo? capturedPsi = null;
        var launcher = new RobloxLauncher(
            paths,
            mockAuth,
            processStarter: psi =>
            {
                capturedPsi = psi;
                return null;
            });

        var account = new RobloxAccount { Username = "Tester", Cookie = "fake-cookie" };
        var target = new LaunchTarget(LaunchTargetKind.ShareLink, null, ShareCode: "SHARE123");

        var result = await launcher.LaunchAccountAsync(account, target);

        result.Success.Should().BeTrue();
        result.LaunchUri.Should().Contain("gameinfo:TICKET_OK");
        result.LaunchUri.Should().Contain("920587237");
        result.LaunchUri.Should().Contain("LINK_CODE_ABC");
        account.LastLaunchedAt.Should().NotBeNull();
        capturedPsi.Should().NotBeNull();
    }

    [Fact]
    public void MainWindowViewModel_MatchesProductionContractsAndBoundsClamping()
    {
        var settings = new Settings();
        var accountStore = new FailingAccountStore();
        var vm = new MainWindowViewModel(settings, accountStore, paths: new PathProvider(_tempDir));

        vm.NavigationTabs.Should().Equal(
            "Accounts", "FastFlags", "Mods", "Appearance", "Behaviour",
            "Channel", "Global Settings", "Shortcuts", "About");

        vm.AccountManagement.Accounts.Should().BeEmpty();

        Theme? changedTo = null;
        vm.WindowIntegrations.ThemeChanged += (_, t) => changedTo = t;
        vm.WindowIntegrations.CurrentTheme = Theme.Dark;
        changedTo.Should().Be(Theme.Dark);
        settings.Theme.Should().Be(Theme.Dark);

        vm.WindowIntegrations.UseAcrylicBackground = true;
        vm.WindowIntegrations.AcrylicBackgroundOpacity = 200;
        settings.UseAcrylicBackground.Should().BeTrue();
        settings.AcrylicBackgroundOpacity.Should().Be(200);

        var clamped = MainWindowViewModel.ClampWindowBounds(
            new WindowState { Width = 1000, Height = 700, Left = -30000, Top = -30000 },
            0, 0, 1920, 1080);
        clamped.Left.Should().BeGreaterThanOrEqualTo(0);
        clamped.Top.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void FastFlagEditorViewModel_CrudSearchAtomicImportAndPersistenceWork()
    {
        string flagsPath = Path.Combine(_tempDir, "ClientAppSettings.json");
        var manager = new FastFlagManager(flagsPath);
        var vm = new FastFlagEditorViewModel(manager);

        Action actEmpty = () => vm.AddFlag("  ", "True");
        actEmpty.Should().Throw<ArgumentException>();

        vm.AddFlag("FFlagDebugDisplayFPS", "True");
        vm.AddFlag("FInt[Special]Val", "42");
        vm.FilteredFlags.Should().HaveCount(2);

        vm.SearchText = "[Special]";
        vm.FilteredFlags.Should().ContainSingle(f => f.Name == "FInt[Special]Val");

        vm.SearchText = string.Empty;
        vm.EditFlag("  fflagdebugdisplayfps ", "False").Should().BeTrue();
        vm.DeleteFlag("FInt[Special]Val").Should().BeTrue();

        vm.ImportJson("{ invalid json").Should().BeFalse();
        vm.Flags.Should().ContainSingle(f => f.Name == "FFlagDebugDisplayFPS" && f.Value == "False");

        vm.SaveToManager();
        File.Exists(flagsPath).Should().BeTrue();

        var reloaded = new FastFlagEditorViewModel(new FastFlagManager(flagsPath));
        reloaded.Flags.Should().Contain(f => f.Name == "FFlagDebugDisplayFPS" && f.Value == "False");
        reloaded.ExportJson().Should().Contain("FFlagDebugDisplayFPS");
    }

    [Fact]
    public void XmlThemeParser_ParsesBothFormats_AndFallsBackSafely()
    {
        var theme1 = XmlThemeParser.Parse("<Theme Name=\"NeonCyber\"><Colors Background=\"#FF0F0F1A\" Foreground=\"#FF00FFCC\"/></Theme>");
        theme1.Background.Should().Be("#FF0F0F1A");
        theme1.Foreground.Should().Be("#FF00FFCC");
        theme1.Name.Should().Be("NeonCyber");

        var theme2 = XmlThemeParser.Parse("<BloxstrapCustomBootstrapper Name=\"Retro\" Background=\"#112233\" Foreground=\"#EEEEEE\" Width=\"640\" Height=\"400\" AllowTransparency=\"true\"/>");
        theme2.Background.Should().Be("#FF112233");
        theme2.Foreground.Should().Be("#FFEEEEEE");
        theme2.Width.Should().Be(640);
        theme2.Height.Should().Be(400);
        theme2.AllowTransparency.Should().BeTrue();
        theme2.IsCustomLayout.Should().BeTrue();

        var fallback = XmlThemeParser.Parse("<Broken><Xml");
        fallback.Background.Should().Be("#FF202020");
        fallback.Foreground.Should().Be("#FFFFFFFF");
    }

    [Fact]
    public void BootstrapperDialogViewModel_ThrottlesRapidUpdates_AndHandlesCancellation()
    {
        LocalizationService.Instance.ApplyLanguage(AppLanguage.English);
        using var cts = new CancellationTokenSource();
        var vm = new BootstrapperDialogViewModel(cts);
        bool cancelFired = false;
        vm.CancelRequested += (_, _) => cancelFired = true;

        for (int i = 1; i <= 500; i++)
        {
            vm.Report(new PackageDownloadProgress($"Pkg{i}.zip", i * 1000, 500_000, i / 5.0, i, 500));
        }

        vm.ProgressValue.Should().Be(100);
        vm.PackageProgressText.Should().Be("500/500 packages (100%)");
        vm.IsIndeterminate.Should().BeFalse();

        vm.CancelCommand.Execute(null);
        cts.IsCancellationRequested.Should().BeTrue();
        cancelFired.Should().BeTrue();
        vm.CancelEnabled.Should().BeFalse();
    }

    [Fact]
    public void BootstrapperDialogFactory_ResolvesAll11Styles_AndCreatesDialogsOnStaThread()
    {
        BootstrapperDialogFactory.ResolveStyle((BootstrapperStyle)999).Should().Be(BootstrapperStyle.FluentDialog);

        Exception? threadEx = null;
        var createdTypes = new List<Type>();

        var thread = new Thread(() =>
        {
            try
            {
                foreach (var style in Enum.GetValues<BootstrapperStyle>())
                {
                    var settings = new Settings { BootstrapperStyle = style, BootstrapperTitle = $"Test-{style}" };
                    using var cts = new CancellationTokenSource();
                    var dialog = BootstrapperDialogFactory.Create(settings, cts, new PathProvider(_tempDir));
                    createdTypes.Add(dialog.GetType());
                    dialog.CloseBootstrapper();
                }
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(15));

        threadEx.Should().BeNull();
        createdTypes.Should().HaveCount(11);
        createdTypes.Should().Contain(typeof(FluentDialog));
        createdTypes.Should().Contain(typeof(ClassicFluentDialog));
        createdTypes.Should().Contain(typeof(ByfronDialog));
        createdTypes.Should().Contain(typeof(TerminalDialog));
        createdTypes.Should().Contain(typeof(TwentyFiveDialog));
        createdTypes.Should().Contain(typeof(CustomDialog));
        createdTypes.Should().Contain(typeof(LegacyProgressDialog));
    }

    [Fact]
    public void NotifyIconWrapper_HeadlessLifecycle_WorksCorrectly()
    {
        using var tray = new NotifyIconWrapper(headlessMode: true);
        tray.Initialize();
        tray.IsTrayActive.Should().BeTrue();
        tray.ContextMenuActions.Should().Contain(["Settings", "Launch Roblox", "Multi-Instance Status", "Exit"]);

        tray.MinimizeToTray();
        tray.IsWindowVisible.Should().BeFalse();

        tray.ShowBalloonNotification("BaBaBooeyStrap", "Launching Roblox");
        tray.LastBalloonTitle.Should().Be("BaBaBooeyStrap");
        tray.LastBalloonMessage.Should().Be("Launching Roblox");

        tray.RestoreWindow();
        tray.IsWindowVisible.Should().BeTrue();

        tray.Dispose();
        tray.IsTrayActive.Should().BeFalse();
    }

    [Fact]
    public async Task NotifyIconWrapper_And_Orchestrator_ServerLocationTrayAndNotifications_WorkEndToEnd()
    {
        var paths = new PathProvider(_tempDir);
        var settingsMgr = new JsonManager<Settings>(paths.SettingsFile);
        settingsMgr.Prop.ShowServerDetails = true;

        var fakeRoValra = new StubRoValraService();
        using var orchestrator = new BootstrapperOrchestrator(
            paths,
            settingsMgr,
            roValraService: fakeRoValra);

        using var tray = new NotifyIconWrapper(
            headlessMode: true,
            getActiveServers: () => orchestrator.GetActiveServers());

        orchestrator.ActiveServersChanged += (_, _) => tray.RefreshServerLocationStatus();
        orchestrator.ServerLocationResolved += (_, info) =>
        {
            var (title, msg) = NotifyIconWrapper.FormatServerJoinNotification(info);
            tray.ShowBalloonNotification(title, msg);
        };

        tray.Initialize();
        tray.ContextMenuActions.Should().Contain("Server Location");
        tray.LastTooltipText.Should().Be("BaBaBooeyStrap");
        tray.LastServerLocationSubItems.Should().BeEmpty();

        // 1. Single instance joins Tokyo server
        await orchestrator.HandleGameJoinedAsync(
            1001,
            "AlphaPlayer",
            new BaBaBooeyStrap.Integrations.Watcher.GameJoinEventArgs(
                "1818",
                "999",
                "job-tokyo-1",
                "128.116.50.1",
                53640,
                true,
                DateTime.UtcNow),
            settingsMgr.Prop);

        orchestrator.GetActiveServers().Should().ContainSingle();
        tray.LastServerLocationText.Should().Contain("AlphaPlayer");
        tray.LastServerLocationText.Should().Contain("Tokyo");
        tray.LastServerLocationText.Should().Contain("AP-East");
        tray.LastServerLocationText.Should().Contain("128.116.50.1:53640");
        tray.LastTooltipText.Should().Contain("Tokyo");
        tray.LastTooltipText!.Length.Should().BeLessThanOrEqualTo(127);
        tray.LastBalloonTitle.Should().Contain("AlphaPlayer");
        tray.LastBalloonTitle.Should().Contain("1001");
        tray.LastBalloonMessage.Should().Contain("Tokyo");
        tray.LastBalloonMessage.Should().Contain("128.116.50.1:53640");

        // 2. Second instance joins Los Angeles server -> multi-instance submenu populated
        await orchestrator.HandleGameJoinedAsync(
            1002,
            "BetaPlayer",
            new BaBaBooeyStrap.Integrations.Watcher.GameJoinEventArgs(
                "2753915549",
                "888",
                "job-la-2",
                "128.116.63.2",
                53641,
                true,
                DateTime.UtcNow.AddSeconds(1)),
            settingsMgr.Prop);

        orchestrator.GetActiveServers().Should().HaveCount(2);
        tray.LastServerLocationSubItems.Should().HaveCount(2);
        tray.LastServerLocationSubItems[0].Should().Contain("AlphaPlayer").And.Contain("Tokyo");
        tray.LastServerLocationSubItems[1].Should().Contain("BetaPlayer").And.Contain("Los Angeles");
        tray.LastTooltipText!.Length.Should().BeLessThanOrEqualTo(127);

        // 3. First instance leaves -> back to single active server
        await orchestrator.HandleGameLeftAsync(1001);
        orchestrator.GetActiveServers().Should().ContainSingle(s => s.ProcessId == 1002);
        tray.LastServerLocationSubItems.Should().BeEmpty();
        tray.LastServerLocationText.Should().Contain("BetaPlayer").And.Contain("Los Angeles");

        // 4. Second instance leaves -> back to 0 active servers
        await orchestrator.HandleGameLeftAsync(1002);
        orchestrator.GetActiveServers().Should().BeEmpty();
        tray.LastTooltipText.Should().Be("BaBaBooeyStrap");

        // Verify tooltip truncation bound (<= 63 chars for WinForms NotifyIcon.Text)
        string longTooltip = NotifyIconWrapper.TruncateTrayTooltip(new string('A', 200));
        longTooltip.Length.Should().Be(63);
        longTooltip.Should().EndWith("...");
    }

    [Fact]
    public void Repro_MainWindowNavigation_ToAccountsPage()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
                var paths = new PathProvider(_tempDir);
                var settingsMgr = new JsonManager<Settings>(paths.SettingsFile);
                var stateMgr = new JsonManager<State>(paths.StateFile);
                var vm = new MainWindowViewModel(settingsMgr.Prop, paths: paths, stateManager: stateMgr);

                var page1 = new BaBaBooeyStrap.App.Views.Pages.AccountsPage { DataContext = vm };
                page1.Measure(new System.Windows.Size(1120, 740));
                page1.Arrange(new System.Windows.Rect(0, 0, 1120, 740));
                page1.UpdateLayout();

                var page2 = new BaBaBooeyStrap.App.Views.Pages.AccountsPage { DataContext = vm };
                page2.Measure(new System.Windows.Size(1120, 740));
                page2.Arrange(new System.Windows.Rect(0, 0, 1120, 740));
                page2.UpdateLayout();

                vm.AccountManagement.IsFollowUserTargetMode = true;
                page2.UpdateLayout();
                vm.AccountManagement.IsGameServerTargetMode = true;
                page2.UpdateLayout();
                vm.AccountManagement.TargetModeSelectedIndex = 1;
                vm.AccountManagement.IsFollowUserTargetMode.Should().BeTrue();
                vm.AccountManagement.TargetModeSelectedIndex = 0;
                vm.AccountManagement.IsGameServerTargetMode.Should().BeTrue();
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(15));
        threadEx.Should().BeNull();
    }

    [Fact]
    public async Task SingleInstanceIpc_MutexGuardAndNamedPipeForwarding_WorksEndToEnd()
    {
        string mutexName = $@"Local\BBBS_TestMutex_{Guid.NewGuid():N}";
        string pipeName = $"BBBS_TestPipe_{Guid.NewGuid():N}";

        using var primary = new SingleInstanceIpc(mutexName, pipeName);
        using var secondary = new SingleInstanceIpc(mutexName, pipeName);

        primary.TryAcquirePrimary().Should().BeTrue();
        bool secondaryAcquired = await Task.Run(() => secondary.TryAcquirePrimary());
        secondaryAcquired.Should().BeFalse();

        var receivedTcs = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.ArgumentsReceived += (_, args) => receivedTcs.TrySetResult(args);
        primary.StartPipeServer();

        bool sent = await secondary.SendArgumentsToPrimaryAsync(["-player", "roblox-player:1+test"]);
        sent.Should().BeTrue();

        var completed = await Task.WhenAny(receivedTcs.Task, Task.Delay(3000));
        completed.Should().Be(receivedTcs.Task);
        (await receivedTcs.Task).Should().Equal("-player", "roblox-player:1+test");
    }

    private sealed class StubAuthService : IRobloxAuthService
    {
        public Task<AuthenticatedUser> GetAuthenticatedUserAsync(string cookie, CancellationToken ct = default) =>
            Task.FromResult(new AuthenticatedUser(12345, "Tester", "Tester"));

        public Task<string> GenerateAuthTicketAsync(string cookie, CancellationToken ct = default) =>
            Task.FromResult("TICKET_OK");

        public Task<ResolvedShareLink> ResolveShareLinkAsync(string cookie, string shareCode, CancellationToken ct = default) =>
            Task.FromResult(new ResolvedShareLink(920587237, "LINK_CODE_ABC"));

        public Task<string?> GetAvatarThumbnailUrlAsync(long userId, string size = "150x150", bool isCircular = true, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task<long> ResolveUserIdByUsernameAsync(string username, CancellationToken ct = default) =>
            Task.FromResult(99999L);

        public Task<RobloxUserPresence> GetUserPresenceAsync(string cookie, long userId, CancellationToken ct = default) =>
            Task.FromResult(new RobloxUserPresence(userId, 2, 1818L, 1818L, "job-follow-1", 12345L, "Playing"));

        public Task<RobloxGameInfo?> GetGameInfoByPlaceIdAsync(long placeId, CancellationToken ct = default) =>
            Task.FromResult<RobloxGameInfo?>(new RobloxGameInfo(placeId, 12345L, $"Game {placeId}", null));

        public void Dispose() { }
    }

    private sealed class StubRoValraService : BaBaBooeyStrap.Integrations.Matchmaking.IRoValraService
    {
        public Task<BaBaBooeyStrap.Integrations.Matchmaking.ServerLocationInfo?> ResolveServerLocationAsync(string ip, CancellationToken ct = default)
        {
            if (ip == "128.116.50.1")
            {
                return Task.FromResult<BaBaBooeyStrap.Integrations.Matchmaking.ServerLocationInfo?>(
                    new BaBaBooeyStrap.Integrations.Matchmaking.ServerLocationInfo(
                        ip, "Tokyo", "Tokyo", "JP", "AP-East", 35.6895, 139.6917, 42));
            }

            return Task.FromResult<BaBaBooeyStrap.Integrations.Matchmaking.ServerLocationInfo?>(
                new BaBaBooeyStrap.Integrations.Matchmaking.ServerLocationInfo(
                    ip, "Los Angeles", "California", "US", "US-West", 34.0522, -118.2437, 135));
        }
    }

    private sealed class FailingAccountStore : IDpapiAccountStore
    {
        public string FilePath => string.Empty;
        public IReadOnlyList<RobloxAccount> LoadAll() => throw new InvalidDataException("Simulated DPAPI corruption");
        public void SaveAll(IEnumerable<RobloxAccount> accounts) { }
        public RobloxAccount Upsert(RobloxAccount account) => account;
        public RobloxAccount? GetById(Guid id) => null;
        public bool Delete(Guid id) => false;
    }
}
