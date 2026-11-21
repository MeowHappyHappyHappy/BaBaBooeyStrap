using System.Xml.Linq;
using System.Windows;
using BaBaBooeyStrap.App.Services;
using BaBaBooeyStrap.App.ViewModels;
using BaBaBooeyStrap.App.Views.Dialogs;
using BaBaBooeyStrap.App.Views.Pages;
using BaBaBooeyStrap.Bootstrapper.Settings;
using BaBaBooeyStrap.Core;
using BaBaBooeyStrap.Core.Interop;
using BaBaBooeyStrap.Core.Launch;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Models.Enums;
using BaBaBooeyStrap.Core.Roblox;
using BaBaBooeyStrap.Core.Storage;
using BaBaBooeyStrap.MultiInstance;
using BaBaBooeyStrap.MultiInstance.Abstractions;
using BaBaBooeyStrap.Tests.Mocks;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;

namespace BaBaBooeyStrap.Tests.App;

public sealed class MultiRobloxIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public MultiRobloxIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"BBBS_MultiRoblox_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                foreach (var file in Directory.GetFiles(_tempDir, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                }
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public void WindowsMultiInstanceController_WhenEventPreSignaled_ResetsAndKeepsEventUnsignaled()
    {
        using var helper = new ParameterizedMutexTestHelper("PreSignaledEvent");
        nint preSignaled = helper.CreateNamedEvent(manualReset: true, initialState: true);
        preSignaled.Should().NotBe(nint.Zero);
        Kernel32.WaitForSingleObject(preSignaled, 0).Should().Be(Kernel32.WAIT_OBJECT_0);

        using var controller = new WindowsMultiInstanceController(helper.MutexName, helper.EventName);
        var status = controller.Enable();

        status.Enabled.Should().BeTrue();
        status.OwnsMutex.Should().BeTrue();

        nint openedEvent = Kernel32.OpenEventW(Kernel32.SYNCHRONIZE, false, helper.EventName);
        openedEvent.Should().NotBe(nint.Zero);
        try
        {
            Kernel32.WaitForSingleObject(openedEvent, 50).Should().Be(Kernel32.WAIT_TIMEOUT);
        }
        finally
        {
            Kernel32.CloseHandle(openedEvent);
        }
    }

    [Fact]
    public void JoinTargetStore_UpsertUpdateDeleteAndReload_RoundTripsCleanly()
    {
        string targetsPath = Path.Combine(_tempDir, "targets.json");
        var store = new JoinTargetStore(targetsPath);

        store.LoadAll().Should().BeEmpty();

        var t1 = new SavedJoinTarget
        {
            Name = "VIP Server A",
            TargetInput = "https://www.roblox.com/games/1818/Classic?privateServerLinkCode=abc123"
        };
        var t2 = new SavedJoinTarget
        {
            Name = "Public Hub",
            TargetInput = "2753915549",
            JobId = "job-uuid-01"
        };

        store.Upsert(t1);
        store.Upsert(t2);

        t1.TargetInput = "https://www.roblox.com/games/1818/Classic?privateServerLinkCode=updated999";
        store.Upsert(t1);

        var reloaded = new JoinTargetStore(targetsPath).LoadAll();
        reloaded.Should().HaveCount(2);
        reloaded.Single(t => t.Id == t1.Id).TargetInput.Should().Contain("updated999");
        reloaded.Single(t => t.Id == t2.Id).JobId.Should().Be("job-uuid-01");

        store.Delete(t1.Id).Should().BeTrue();
        store.Delete(t1.Id).Should().BeFalse();
        new JoinTargetStore(targetsPath).LoadAll().Should().ContainSingle(t => t.Id == t2.Id);
    }

    [Fact]
    public void RobloxLauncher_ParseTargetInput_AndBuildEffectiveTargetInput_HandlesAllFormatsAndRejectsInvalid()
    {
        var raw = RobloxLauncher.ParseTargetInput("  1818  ");
        raw.Kind.Should().Be(LaunchTargetKind.Standard);
        raw.PlaceId.Should().Be(1818);
        raw.JobId.Should().BeNull();

        var gameUrl = RobloxLauncher.ParseTargetInput("https://www.roblox.com/games/2753915549/Blox-Fruits?gameInstanceId=job-123");
        gameUrl.Kind.Should().Be(LaunchTargetKind.Standard);
        gameUrl.PlaceId.Should().Be(2753915549);
        gameUrl.JobId.Should().Be("job-123");

        var vipUrl = RobloxLauncher.ParseTargetInput("https://www.roblox.com/games/1818/Classic?privateServerLinkCode=vip-code-77");
        vipUrl.Kind.Should().Be(LaunchTargetKind.PrivateServer);
        vipUrl.PlaceId.Should().Be(1818);
        vipUrl.LinkCode.Should().Be("vip-code-77");

        var shareUrl = RobloxLauncher.ParseTargetInput("https://www.roblox.com/share?code=shareXYZ&type=Server");
        shareUrl.Kind.Should().Be(LaunchTargetKind.ShareLink);
        shareUrl.PlaceId.Should().BeNull();
        shareUrl.ShareCode.Should().Be("shareXYZ");

        string effective = RobloxLauncher.BuildEffectiveTargetInput("1818", "job-999");
        var parsedEffective = RobloxLauncher.ParseTargetInput(effective);
        parsedEffective.Kind.Should().Be(LaunchTargetKind.Standard);
        parsedEffective.PlaceId.Should().Be(1818);
        parsedEffective.JobId.Should().Be("job-999");

        // FollowUser formats (profile URL, @username, user:id, followUserMode)
        var profileUrl = RobloxLauncher.ParseTargetInput("https://www.roblox.com/users/156/profile");
        profileUrl.Kind.Should().Be(LaunchTargetKind.FollowUser);
        profileUrl.UserId.Should().Be(156);

        var localizedProfileUrl = RobloxLauncher.ParseTargetInput("https://www.roblox.com/zh-tw/users/98765/profile");
        localizedProfileUrl.Kind.Should().Be(LaunchTargetKind.FollowUser);
        localizedProfileUrl.UserId.Should().Be(98765);

        var atUser = RobloxLauncher.ParseTargetInput("@Builderman");
        atUser.Kind.Should().Be(LaunchTargetKind.FollowUser);
        atUser.Username.Should().Be("Builderman");

        var prefixedUserId = RobloxLauncher.ParseTargetInput("user:123456");
        prefixedUserId.Kind.Should().Be(LaunchTargetKind.FollowUser);
        prefixedUserId.UserId.Should().Be(123456);

        var followModeNumeric = RobloxLauncher.ParseTargetInput("156", followUserMode: true);
        followModeNumeric.Kind.Should().Be(LaunchTargetKind.FollowUser);
        followModeNumeric.UserId.Should().Be(156);

        var followModeName = RobloxLauncher.ParseTargetInput("Friend_User01", followUserMode: true);
        followModeName.Kind.Should().Be(LaunchTargetKind.FollowUser);
        followModeName.Username.Should().Be("Friend_User01");

        RobloxLauncher.BuildEffectiveTargetInput("156", null, followUserMode: true).Should().Be("user:156");
        RobloxLauncher.BuildEffectiveTargetInput("Friend_User01", null, followUserMode: true).Should().Be("@Friend_User01");

        Action actEmpty = () => RobloxLauncher.ParseTargetInput("   ");
        Action actBadUrl = () => RobloxLauncher.ParseTargetInput("not-a-valid-url");
        Action actMissingShareCode = () => RobloxLauncher.ParseTargetInput("https://www.roblox.com/share?type=Server");
        Action actWrongShareType = () => RobloxLauncher.ParseTargetInput("https://www.roblox.com/share?code=abc&type=ExperienceDetails");
        Action actBadUserPrefix = () => RobloxLauncher.ParseTargetInput("user:invalid-name-with-dashes!");

        actEmpty.Should().Throw<ArgumentException>();
        actBadUrl.Should().Throw<ArgumentException>();
        actMissingShareCode.Should().Throw<ArgumentException>();
        actWrongShareType.Should().Throw<ArgumentException>();
        actBadUserPrefix.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task FollowUser_LaunchAccount_And_ViewModelModeSwitching_WorkEndToEnd()
    {
        var paths = new PathProvider(_tempDir);
        string versionDir = paths.GetVersionDirectory("version-follow");
        Directory.CreateDirectory(versionDir);
        File.WriteAllBytes(Path.Combine(versionDir, "RobloxPlayerBeta.exe"), new byte[] { 0x4D, 0x5A });

        var mockHandler = new MockHttpMessageHandler();
        mockHandler.WhenPost("users.roblox.com/v1/usernames/users")
            .RespondWithJson(new
            {
                data = new[]
                {
                    new { id = 555L, name = "FriendWithJob", displayName = "FriendWithJob" }
                }
            });
        mockHandler.SetupAuthTicketWorkflow("cookie-ok", "csrf-1", "TICKET_FOLLOW_OK");
        mockHandler.WhenPost("presence.roblox.com/v1/presence/users")
            .RespondWith(async (req, ct) =>
            {
                string body = await req.Content!.ReadAsStringAsync(ct);
                if (body.Contains("555"))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            "{\"userPresences\":[{\"userPresenceType\":2,\"placeId\":1818,\"rootPlaceId\":1818,\"gameId\":\"job-friend-555\",\"userId\":555}]}",
                            System.Text.Encoding.UTF8,
                            "application/json")
                    };
                }

                if (body.Contains("666"))
                {
                    // InGame but gameId hidden -> fallback to RequestFollowUser
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            "{\"userPresences\":[{\"userPresenceType\":2,\"placeId\":null,\"rootPlaceId\":null,\"gameId\":null,\"userId\":666}]}",
                            System.Text.Encoding.UTF8,
                            "application/json")
                    };
                }

                // Offline
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"userPresences\":[{\"userPresenceType\":0,\"placeId\":null,\"rootPlaceId\":null,\"gameId\":null,\"userId\":777}]}",
                        System.Text.Encoding.UTF8,
                        "application/json")
                };
            });

        using var authService = new RobloxAuthService(mockHandler.ToHttpClient());
        var launcher = new RobloxLauncher(paths, authService, processStarter: _ => null);
        var account = new RobloxAccount { Username = "MyAlt", Cookie = "cookie-ok" };

        // Case 1: Friend in game with exposed PlaceId + JobId -> launches directly into RequestGameJob
        var resDirect = await launcher.LaunchAccountAsync(account, RobloxLauncher.ParseTargetInput("@FriendWithJob"));
        resDirect.Success.Should().BeTrue();
        resDirect.Target.PlaceId.Should().Be(1818L);
        resDirect.Target.JobId.Should().Be("job-friend-555");
        resDirect.Target.UserId.Should().Be(555L);
        resDirect.LaunchUri.Should().Contain("RequestGameJob");
        resDirect.LaunchUri.Should().Contain("job-friend-555");

        // Case 2: Friend in game without exposed JobId -> falls back to RequestFollowUser
        var resFallback = await launcher.LaunchAccountAsync(account, RobloxLauncher.ParseTargetInput("user:666"));
        resFallback.Success.Should().BeTrue();
        resFallback.Target.UserId.Should().Be(666L);
        resFallback.LaunchUri.Should().Contain("RequestFollowUser");
        resFallback.LaunchUri.Should().Contain("userId%3D666");
        RobloxLauncher.ParseTargetFromUri(resFallback.LaunchUri).Kind.Should().Be(LaunchTargetKind.FollowUser);

        // Case 3: Friend not in game -> intercepted before launching Roblox
        var resOffline = await launcher.LaunchAccountAsync(account, RobloxLauncher.ParseTargetInput("https://www.roblox.com/users/777/profile"));
        resOffline.Success.Should().BeFalse();
        resOffline.ErrorMessage.Should().Contain("not currently in a game");

        // Case 4: ViewModel FollowUser mode toggle, target saving normalization, and batch launch
        var store = new DpapiAccountStore(paths.AccountsFile);
        store.Upsert(account);
        var targetStore = new JoinTargetStore(paths.TargetsFile);
        var controller = new StubMultiInstanceController(new MultiInstanceStatus(true, true, false, [], null));
        var supervisor = new StubProcessSupervisor();
        var vm = new MainWindowViewModel(
            new Settings { MultiInstanceEnabled = true },
            store,
            controller,
            supervisor,
            launcher,
            paths,
            joinTargetStore: targetStore);

        vm.AccountManagement.IsGameServerTargetMode.Should().BeTrue();
        vm.AccountManagement.IsJobIdInputVisible.Should().BeTrue();

        vm.AccountManagement.IsFollowUserTargetMode = true;
        vm.AccountManagement.IsGameServerTargetMode.Should().BeFalse();
        vm.AccountManagement.IsJobIdInputVisible.Should().BeFalse();

        vm.AccountManagement.TargetName = "Follow My Friend";
        vm.AccountManagement.LaunchTargetInput = "FriendWithJob";
        vm.AccountManagement.SaveCurrentJoinTarget().Should().BeTrue();
        vm.AccountManagement.SavedTargets.Should().ContainSingle(t => t.Name == "Follow My Friend" && t.TargetInput == "@FriendWithJob");

        // Switch back to game server mode, then select saved friend target -> auto-switches to FollowUser mode
        vm.AccountManagement.IsGameServerTargetMode = true;
        vm.AccountManagement.SelectedSavedTarget = null;
        vm.AccountManagement.SelectedSavedTarget = vm.AccountManagement.SavedTargets[0];
        vm.AccountManagement.IsFollowUserTargetMode.Should().BeTrue();

        vm.AccountManagement.AccountItems[0].IsChecked = true;
        int launchedCount = await vm.AccountManagement.LaunchCheckedAccountsAsync();
        launchedCount.Should().Be(1);
    }

    [Fact]
    public void GlobalSettingsManager_ApplyProfileAndRestoreBackup_PreservesReadOnlyAndLeavesNullsUntouched()
    {
        const string originalXml = """
            <roblox version="4">
              <Item class="UserGameSettings">
                <Properties>
                  <int name="FramerateCap">240</int>
                  <bool name="Fullscreen">true</bool>
                  <int name="GraphicsQualityLevel">21</int>
                  <float name="MasterVolume">0.8</float>
                  <token name="SavedQualityLevel">10</token>
                  <bool name="VSync">true</bool>
                  <string name="KeepMe">unchanged</string>
                </Properties>
              </Item>
            </roblox>
            """;

        string xmlPath = Path.Combine(_tempDir, "GlobalBasicSettings_13.xml");
        File.WriteAllText(xmlPath, originalXml);
        File.SetAttributes(xmlPath, FileAttributes.ReadOnly);

        var manager = new GlobalSettingsManager(xmlPath);
        string? backup = manager.CaptureBackup();
        backup.Should().Be(originalXml);

        var profile = new RobloxClientSettingsProfile
        {
            MasterVolumePercent = 0,
            QualityLevel = 1,
            FramerateCap = 30,
            Fullscreen = false,
            CustomOverrides =
            [
                new CustomSettingOverride { Key = "VSync", Value = "false" },
                new CustomSettingOverride { Key = "NewFloat", Value = "1.25" },
                new CustomSettingOverride { Key = "NewInt", Value = "42" },
                new CustomSettingOverride { Key = "NewText", Value = "hello" }
            ]
        };

        manager.ApplyProfile(profile);

        File.GetAttributes(xmlPath).HasFlag(FileAttributes.ReadOnly).Should().BeTrue();
        var doc = XDocument.Load(xmlPath);
        var props = doc.Descendants("Properties").Single();
        GetXmlProp(props, "MasterVolume").Should().Be("0");
        GetXmlProp(props, "SavedQualityLevel").Should().Be("1");
        GetXmlProp(props, "GraphicsQualityLevel").Should().Be("1");
        GetXmlProp(props, "FramerateCap").Should().Be("30");
        GetXmlProp(props, "Fullscreen").Should().Be("false");
        GetXmlProp(props, "VSync").Should().Be("false");
        GetXmlProp(props, "NewFloat").Should().Be("1.25");
        GetXmlProp(props, "NewInt").Should().Be("42");
        GetXmlProp(props, "NewText").Should().Be("hello");
        GetXmlProp(props, "KeepMe").Should().Be("unchanged");

        // Null-only fields leave existing values untouched
        manager.ApplyProfile(new RobloxClientSettingsProfile { QualityLevel = 0 });
        var autoDoc = XDocument.Load(xmlPath);
        var autoProps = autoDoc.Descendants("Properties").Single();
        GetXmlProp(autoProps, "SavedQualityLevel").Should().Be("0");
        GetXmlProp(autoProps, "GraphicsQualityLevel").Should().Be("0");
        GetXmlProp(autoProps, "FramerateCap").Should().Be("30");

        manager.RestoreBackup(backup);
        File.GetAttributes(xmlPath).HasFlag(FileAttributes.ReadOnly).Should().BeTrue();
        File.ReadAllText(xmlPath).Should().Be(originalXml);
    }

    [Fact]
    public void LaunchSettings_NewMultiRobloxCliFlags_ParseAccurately()
    {
        var holdArgs = new LaunchSettings(["-hold", "-quiet"]);
        holdArgs.HoldFlag.Active.Should().BeTrue();
        holdArgs.QuietFlag.Active.Should().BeTrue();

        var acctArgs = new LaunchSettings(["-account", "AltUser1", "-target", "https://www.roblox.com/games/1818/Classic", "-jobid", "job-77", "-nohold", "-detach"]);
        acctArgs.AccountFlag.Active.Should().BeTrue();
        acctArgs.AccountFlag.Data.Should().Be("AltUser1");
        acctArgs.TargetFlag.Active.Should().BeTrue();
        acctArgs.TargetFlag.Data.Should().Be("https://www.roblox.com/games/1818/Classic");
        acctArgs.JobIdFlag.Active.Should().BeTrue();
        acctArgs.JobIdFlag.Data.Should().Be("job-77");
        acctArgs.NoHoldFlag.Active.Should().BeTrue();
        acctArgs.DetachFlag.Active.Should().BeTrue();
    }

    [Fact]
    public async Task MainWindowViewModel_LaunchSelectedAccount_PreservesSelectionAndNeverThrowsNre()
    {
        var paths = new PathProvider(_tempDir);
        var store = new DpapiAccountStore(paths.AccountsFile);
        var acc1 = new RobloxAccount { UserId = 101, Username = "PlayerOne", Cookie = "cookie-1" };
        var acc2 = new RobloxAccount { UserId = 102, Username = "PlayerTwo", Cookie = "cookie-2" };
        store.Upsert(acc1);
        store.Upsert(acc2);

        var controller = new StubMultiInstanceController(new MultiInstanceStatus(true, true, false, [], null));
        var supervisor = new StubProcessSupervisor();
        var launcher = new RecordingRobloxLauncher();

        var vm = new MainWindowViewModel(
            new Settings(),
            store,
            controller,
            supervisor,
            launcher,
            paths);

        vm.AccountManagement.LaunchTargetInput = "1818";
        vm.AccountManagement.SelectedAccountItem = vm.AccountManagement.AccountItems[0];
        vm.AccountManagement.SelectedAccount.Should().BeSameAs(vm.AccountManagement.AccountItems[0].Account);

        var result = await vm.AccountManagement.LaunchSelectedAccountAsync();

        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();
        vm.AccountManagement.SelectedAccountItem.Should().NotBeNull();
        vm.AccountManagement.SelectedAccountItem!.Username.Should().Be("PlayerOne");
        vm.AccountManagement.SelectedAccount.Should().NotBeNull();
        vm.AccountManagement.SelectedAccount!.LastLaunchedAt.Should().NotBeNull();
        launcher.LaunchedUsernames.Should().Equal("PlayerOne");
    }

    [Fact]
    public async Task MainWindowViewModel_SequentialBatchLaunch_MixedSettingsWindowWaitFailureRecoveryAndCancel()
    {
        string xmlPath = Path.Combine(_tempDir, "Roblox", "GlobalBasicSettings_13.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(xmlPath)!);
        const string baselineXml = """
            <roblox version="4">
              <Item class="UserGameSettings">
                <Properties>
                  <int name="FramerateCap">240</int>
                </Properties>
              </Item>
            </roblox>
            """;
        File.WriteAllText(xmlPath, baselineXml);

        var paths = new PathProvider(_tempDir);
        var store = new DpapiAccountStore(paths.AccountsFile);
        var acc1 = new RobloxAccount
        {
            UserId = 1,
            Username = "Acc1_AFK",
            Cookie = "c1",
            RobloxSettings = RobloxClientSettingsProfile.CreateAfkLowPerfPreset()
        };
        var acc2 = new RobloxAccount
        {
            UserId = 2,
            Username = "Acc2_Default",
            Cookie = "c2",
            RobloxSettings = null
        };
        var acc3 = new RobloxAccount
        {
            UserId = 3,
            Username = "Acc3_Fps144",
            Cookie = "c3",
            RobloxSettings = new RobloxClientSettingsProfile { FramerateCap = 144 }
        };
        store.Upsert(acc1);
        store.Upsert(acc2);
        store.Upsert(acc3);

        var observedFpsDuringLaunch = new List<(string Username, string? FramerateCap)>();
        var controller = new StubMultiInstanceController(new MultiInstanceStatus(true, true, false, [], null));
        var supervisor = new StubProcessSupervisor();
        var settingsManager = new GlobalSettingsManager(xmlPath);
        var launcher = new RecordingRobloxLauncher(onLaunch: (acc, _) =>
        {
            var doc = XDocument.Load(xmlPath);
            var fps = GetXmlProp(doc.Descendants("Properties").Single(), "FramerateCap");
            observedFpsDuringLaunch.Add((acc.Username, fps));
        });

        var vm = new MainWindowViewModel(
            new Settings { MultiInstanceEnabled = true },
            store,
            controller,
            supervisor,
            launcher,
            paths,
            globalSettingsManager: settingsManager);
        vm.AccountManagement.LaunchTargetInput = "https://www.roblox.com/games/1818/Classic";
        vm.AccountManagement.WindowWaitTimeout = TimeSpan.FromSeconds(2);

        vm.AccountManagement.ToggleSelectAllAccounts();
        vm.AccountManagement.AccountItems.Should().OnlyContain(i => i.IsChecked);

        int count = await vm.AccountManagement.LaunchCheckedAccountsAsync();

        count.Should().Be(3);
        observedFpsDuringLaunch.Should().Equal(
            ("Acc1_AFK", "30"),
            ("Acc2_Default", "240"),
            ("Acc3_Fps144", "144"));
        File.ReadAllText(xmlPath).Should().Be(baselineXml);
        supervisor.WaitForNewWindowCallCount.Should().Be(3);

        // Single-account API exception does not crash batch and continues launching remaining accounts
        var failingLauncher = new RecordingRobloxLauncher(onLaunch: (acc, _) =>
        {
            if (acc.Username == "Acc2_Default")
            {
                throw new RobloxApiException(HttpStatusCode.Unauthorized, "Expired cookie");
            }
        });

        var vm2 = new MainWindowViewModel(
            new Settings { MultiInstanceEnabled = true },
            store,
            controller,
            supervisor,
            failingLauncher,
            paths,
            globalSettingsManager: settingsManager);
        vm2.AccountManagement.LaunchTargetInput = "1818";
        vm2.AccountManagement.WindowWaitTimeout = TimeSpan.FromSeconds(2);
        vm2.AccountManagement.ToggleSelectAllAccounts();
        int partialSuccess = await vm2.AccountManagement.LaunchCheckedAccountsAsync();

        partialSuccess.Should().Be(2);
        failingLauncher.LaunchedUsernames.Should().Equal("Acc1_AFK", "Acc3_Fps144");
        vm2.AccountManagement.ActivityLogText.Should().Contain("Acc2_Default");
        File.ReadAllText(xmlPath).Should().Be(baselineXml);

        // Blocked mutex prevents any launcher call
        var blockedController = new StubMultiInstanceController(new MultiInstanceStatus(true, false, true, [999], null));
        var blockedLauncher = new RecordingRobloxLauncher();
        var vmBlocked = new MainWindowViewModel(
            new Settings { MultiInstanceEnabled = true },
            store,
            blockedController,
            supervisor,
            blockedLauncher,
            paths,
            globalSettingsManager: settingsManager);
        vmBlocked.AccountManagement.LaunchTargetInput = "1818";
        vmBlocked.AccountManagement.ToggleSelectAllAccounts();
        int blockedCount = await vmBlocked.AccountManagement.LaunchCheckedAccountsAsync();

        blockedCount.Should().Be(0);
        blockedLauncher.LaunchedUsernames.Should().BeEmpty();
        vmBlocked.AccountManagement.ActivityLogText.Should().Contain("ROBLOX_singletonMutex");

        // Cancellation stops remaining launches and restores XML backup
        MainWindowViewModel? vmCancel = null;
        var cancelLauncher = new RecordingRobloxLauncher(onLaunch: (acc, _) =>
        {
            if (acc.Username == "Acc1_AFK")
            {
                vmCancel!.AccountManagement.CancelActiveLaunch();
            }
        });
        vmCancel = new MainWindowViewModel(
            new Settings { MultiInstanceEnabled = true },
            store,
            controller,
            supervisor,
            cancelLauncher,
            paths,
            globalSettingsManager: settingsManager);
        vmCancel.AccountManagement.LaunchTargetInput = "1818";
        vmCancel.AccountManagement.WindowWaitTimeout = TimeSpan.FromSeconds(2);
        vmCancel.AccountManagement.ToggleSelectAllAccounts();
        int cancelledCount = await vmCancel.AccountManagement.LaunchCheckedAccountsAsync();

        cancelledCount.Should().Be(1);
        cancelLauncher.LaunchedUsernames.Should().Equal("Acc1_AFK");
        File.ReadAllText(xmlPath).Should().Be(baselineXml);
    }

    [Fact]
    public void MainWindowViewModel_AccountManagementSearchTargetsAndMultiRobloxImport_WorkEndToEnd()
    {
        var paths = new PathProvider(_tempDir);
        var store = new DpapiAccountStore(paths.AccountsFile);
        var targetStore = new JoinTargetStore(paths.TargetsFile);

        var vm = new MainWindowViewModel(
            new Settings(),
            store,
            paths: paths,
            joinTargetStore: targetStore);

        var a1 = new RobloxAccount { UserId = 1001, Username = "AlphaUser", Notes = "Main" };
        var a2 = new RobloxAccount { UserId = 1002, Username = "BetaFarmer", Notes = "Alt" };
        vm.AccountManagement.AddOrUpdateAccount(a1);
        vm.AccountManagement.AddOrUpdateAccount(a2);

        vm.AccountManagement.AccountItems.Should().HaveCount(2);
        vm.AccountManagement.AccountCountText.Should().Be("(2)");

        // Filter search
        vm.AccountManagement.AccountSearchText = "Farmer";
        vm.AccountManagement.FilteredAccountItems.Cast<AccountItem>().Should().ContainSingle(i => i.Username == "BetaFarmer");
        vm.AccountManagement.AccountSearchText = string.Empty;
        vm.AccountManagement.FilteredAccountItems.Cast<AccountItem>().Should().HaveCount(2);

        // Move up/down + persist
        vm.AccountManagement.SelectedAccountItem = vm.AccountManagement.AccountItems[1];
        vm.AccountManagement.MoveSelectedAccount(-1).Should().BeTrue();
        vm.AccountManagement.AccountItems[0].Username.Should().Be("BetaFarmer");
        store.LoadAll()[0].Username.Should().Be("BetaFarmer");

        // Batch AFK preset
        vm.AccountManagement.AccountItems[0].IsChecked = true;
        vm.AccountManagement.ApplyBatchAfkPreset().Should().Be(1);
        vm.AccountManagement.AccountItems[0].Account.RobloxSettings.Should().NotBeNull();
        vm.AccountManagement.AccountItems[0].Account.RobloxSettings!.FramerateCap.Should().Be(30);

        // Saved Join Targets CRUD
        vm.AccountManagement.TargetName = "Boss Farm";
        vm.AccountManagement.LaunchTargetInput = "2753915549";
        vm.AccountManagement.LaunchJobId = "job-boss";
        vm.AccountManagement.SaveCurrentJoinTarget().Should().BeTrue();
        vm.AccountManagement.SavedTargets.Should().ContainSingle(t => t.Name == "Boss Farm" && t.JobId == "job-boss");

        vm.AccountManagement.DeleteSelectedJoinTarget().Should().BeTrue();
        vm.AccountManagement.SavedTargets.Should().BeEmpty();

        // MultiRoblox import without duplicating existing UserId
        string mrDir = Path.Combine(_tempDir, "MultiRobloxImport");
        Directory.CreateDirectory(mrDir);
        var mrAccountStore = new DpapiAccountStore(Path.Combine(mrDir, "accounts.dat"));
        mrAccountStore.Upsert(new RobloxAccount { UserId = 1001, Username = "AlphaDuplicate", Cookie = "c-dup" });
        mrAccountStore.Upsert(new RobloxAccount { UserId = 1003, Username = "GammaImported", Cookie = "c-gamma", Notes = "Imported" });

        var mrTargetStore = new JoinTargetStore(Path.Combine(mrDir, "targets.json"));
        mrTargetStore.Upsert(new SavedJoinTarget { Name = "Imported VIP", TargetInput = "https://www.roblox.com/share?code=mr123&type=Server" });

        var (importedAccs, importedTargets) = vm.AccountManagement.ImportFromMultiRoblox(mrDir);
        importedAccs.Should().Be(1);
        importedTargets.Should().Be(1);
        vm.AccountManagement.AccountItems.Should().HaveCount(3);
        vm.AccountManagement.AccountItems.Should().Contain(i => i.Username == "GammaImported");
        vm.AccountManagement.SavedTargets.Should().ContainSingle(t => t.Name == "Imported VIP");

        // Remove checked
        foreach (var item in vm.AccountManagement.AccountItems) item.IsChecked = item.UserId == 1003;
        vm.AccountManagement.RemoveCheckedOrSelectedAccounts().Should().Be(1);
        vm.AccountManagement.AccountItems.Should().HaveCount(2);
    }

    [Fact]
    public void AccountSettingsDialog_And_AccountsPage_InstantiateCleanlyOnStaThread()
    {
        Exception? threadEx = null;
        string? headerText = null;
        string? volumeText = null;
        string? fpsText = null;
        RobloxClientSettingsProfile? loadedProfile = null;

        var thread = new Thread(() =>
        {
            Window? host = null;
            try
            {
                _ = System.IO.Packaging.PackUriHelper.UriSchemePack;

                host = new Window();
                host.Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = ApplicationTheme.Dark });
                host.Resources.MergedDictionaries.Add(new ControlsDictionary());
                host.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/BaBaBooeyStrap;component/Resources/Styles/AppResources.xaml", UriKind.Absolute)
                });

                var vm = new MainWindowViewModel(new Settings(), paths: new PathProvider(_tempDir));
                var page = new AccountsPage { DataContext = vm };
                host.Content = page;

                var preset = RobloxClientSettingsProfile.CreateAfkLowPerfPreset();
                preset.CustomOverrides.Add(new CustomSettingOverride { Key = "VSync", Value = "false" });
                var dialog = new AccountSettingsDialog("TestPlayer", preset);
                headerText = dialog.AccountTitleText.Text;
                volumeText = dialog.VolumeValueText.Text;
                fpsText = dialog.FpsComboBox.Text;
                loadedProfile = dialog.ResultProfile;
                dialog.Close();
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
            finally
            {
                if (host != null) host.Content = null;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(15));

        threadEx.Should().BeNull();
        headerText.Should().Contain("TestPlayer");
        volumeText.Should().Be("0%");
        fpsText.Should().Be("30");
        loadedProfile.Should().NotBeNull();
    }

    private static string? GetXmlProp(XElement properties, string name) =>
        properties.Elements()
            .FirstOrDefault(e => string.Equals((string?)e.Attribute("name"), name, StringComparison.Ordinal))
            ?.Value;

    private sealed class StubMultiInstanceController : IMultiInstanceController
    {
        private readonly MultiInstanceStatus _status;

        public StubMultiInstanceController(MultiInstanceStatus status) => _status = status;

        public MultiInstanceStatus Enable() => _status;
        public MultiInstanceStatus Disable() => _status with { Enabled = false, OwnsMutex = false };
        public MultiInstanceStatus GetStatus() => _status;
        public void Dispose() { }
    }

    private sealed class StubProcessSupervisor : IProcessSupervisor
    {
        private int _nextPid = 50000;
        public int WaitForNewWindowCallCount { get; private set; }
        public IReadOnlyList<int> TrackedPids { get; } = Array.Empty<int>();
        public void RegisterProcess(int pid) { }
        public bool UnregisterProcess(int pid) => false;
        public void ClearTrackedProcesses() { }
        public IReadOnlyList<int> GetRunningRobloxPids() => Array.Empty<int>();
        public IReadOnlySet<int> CaptureRunningClientPids() => new HashSet<int>();
        public Task<TimeSpan> ThrottleLaunchAsync(TimeSpan? minDelay = null, CancellationToken ct = default) => Task.FromResult(TimeSpan.Zero);
        public Task<nint> WaitForNewClientMainWindowAsync(int pid, TimeSpan timeout, Func<nint, bool>? windowValidator = null, CancellationToken ct = default) => Task.FromResult(nint.Zero);
        public Task<ClientWindowWaitResult> WaitForNewClientMainWindowAsync(IReadOnlySet<int> previousPids, TimeSpan? timeout = null, TimeSpan? pollInterval = null, CancellationToken ct = default)
        {
            WaitForNewWindowCallCount++;
            return Task.FromResult(new ClientWindowWaitResult(true, ++_nextPid, new nint(1234), false));
        }
    }

    private sealed class RecordingRobloxLauncher : IRobloxLauncher
    {
        private readonly Action<RobloxAccount, LaunchTarget>? _onLaunch;
        public List<string> LaunchedUsernames { get; } = [];

        public RecordingRobloxLauncher(Action<RobloxAccount, LaunchTarget>? onLaunch = null)
        {
            _onLaunch = onLaunch;
        }

        public Task<LaunchResult> LaunchPlayerAsync(LaunchSettings settings, CancellationToken ct = default) =>
            Task.FromResult(new LaunchResult(true, settings.RobloxLaunchArgs, new LaunchTarget(LaunchTargetKind.Standard, null), DateTimeOffset.UtcNow, 1000, null));

        public Task<LaunchResult> LaunchAccountAsync(RobloxAccount account, LaunchTarget target, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _onLaunch?.Invoke(account, target);
            LaunchedUsernames.Add(account.Username);
            account.LastLaunchedAt = DateTimeOffset.UtcNow;
            return Task.FromResult(new LaunchResult(true, "roblox-player:1+launchmode:play", target, DateTimeOffset.UtcNow, 2000 + LaunchedUsernames.Count, null));
        }
    }
}
