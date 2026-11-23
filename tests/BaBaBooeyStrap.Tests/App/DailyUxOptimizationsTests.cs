using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using BaBaBooeyStrap.App.Services;
using BaBaBooeyStrap.App.ViewModels;
using BaBaBooeyStrap.App.ViewModels.Sections;
using BaBaBooeyStrap.Bootstrapper.FastFlags;
using BaBaBooeyStrap.Core;
using BaBaBooeyStrap.Core.Logging;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Models.Enums;
using BaBaBooeyStrap.Core.Roblox;
using BaBaBooeyStrap.Core.Storage;
using BaBaBooeyStrap.Integrations.Custom;
using BaBaBooeyStrap.Integrations.Window;
using FluentAssertions;
using Xunit;

namespace BaBaBooeyStrap.Tests.App;

[Collection("LocalizationSingleton")]
public sealed class DailyUxOptimizationsTests : IDisposable
{
    private readonly string _tempDir;

    public DailyUxOptimizationsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"BBBS_DailyUX_Tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch { }
    }

    // -------------------------------------------------------------------------
    // 1. ProfileTransferService
    // -------------------------------------------------------------------------

    [Fact]
    public void ProfileTransferService_ExportImportRoundTrip_PreservesDataAndNeverExportsCookies()
    {
        var paths = new PathProvider(_tempDir);
        paths.EnsureDirectoriesExist();

        // Plant a DPAPI account store with a secret cookie in the same base directory
        var accountStore = new DpapiAccountStore(paths.AccountsFile);
        accountStore.Upsert(new RobloxAccount
        {
            UserId = 424242,
            Username = "SecretUser",
            DisplayName = "SecretUser",
            Cookie = "_|WARNING:-DO-NOT-SHARE-THIS|--SUPER-SECRET-COOKIE-VALUE"
        });

        var settings = new Settings
        {
            Theme = Theme.Dark,
            Language = AppLanguage.TraditionalChinese,
            CheckAppUpdates = false,
            RunOnStartup = true,
            BootstrapperTitle = "CustomStrap"
        };
        settings.CustomIntegrations.Add(new CustomIntegration
        {
            Name = "OBS",
            Location = @"C:\Tools\obs64.exe",
            LaunchArgs = "--startreplaybuffer",
            Delay = 250,
            PreLaunch = false,
            AutoClose = true
        });

        var flagMgr = new FastFlagManager(paths.GetClientSettingsPath());
        flagMgr.SetFlag("FFlagTestBool", true);
        flagMgr.SetFlag("DFIntDebugFRMQualityLevelOverride", 10);
        flagMgr.Save();

        var targetStore = new JoinTargetStore(paths.TargetsFile);
        targetStore.SaveAll([
            new SavedJoinTarget
            {
                Name = "Blox Fruits",
                TargetInput = "2753915549",
                PlaceId = 2753915549,
                GameName = "Blox Fruits",
                ThumbnailUrl = "https://tr.rbxcdn.com/thumb.png"
            }
        ]);

        string exportPath = Path.Combine(_tempDir, "profile_backup.json");
        ProfileTransferService.ExportToFile(exportPath, settings, flagMgr, targetStore, "1.2.3");

        string rawJson = File.ReadAllText(exportPath, Encoding.UTF8);
        rawJson.Should().NotContain("SUPER-SECRET-COOKIE-VALUE");
        rawJson.Should().NotContain(".ROBLOSECURITY");
        rawJson.Should().NotContain("SecretUser");

        var imported = ProfileTransferService.ImportFromFile(exportPath);
        imported.SchemaVersion.Should().Be(ProfileBundle.CurrentSchemaVersion);
        imported.AppVersion.Should().Be("1.2.3");
        imported.Settings.Theme.Should().Be(Theme.Dark);
        imported.Settings.Language.Should().Be(AppLanguage.TraditionalChinese);
        imported.Settings.BootstrapperTitle.Should().Be("CustomStrap");
        imported.Settings.RunOnStartup.Should().BeTrue();
        imported.Settings.CustomIntegrations.Should().ContainSingle(i => i.Name == "OBS" && i.AutoClose);
        imported.FastFlags.Should().ContainKey("FFlagTestBool");
        imported.FastFlags["DFIntDebugFRMQualityLevelOverride"].ToString().Should().Be("10");
        imported.SavedTargets.Should().ContainSingle(t => t.Name == "Blox Fruits" && t.PlaceId == 2753915549);

        // Apply onto fresh stores (merge + replace verification)
        string targetDir = Path.Combine(_tempDir, "apply_target");
        var targetPaths = new PathProvider(targetDir);
        targetPaths.EnsureDirectoriesExist();
        var dstSettings = new Settings();
        var dstFlagMgr = new FastFlagManager(targetPaths.GetClientSettingsPath());
        dstFlagMgr.SetFlag("OldFlagToRemove", "True");
        dstFlagMgr.Save();

        var dstTargetStore = new JoinTargetStore(targetPaths.TargetsFile);
        dstTargetStore.SaveAll([new SavedJoinTarget { Name = "ExistingTarget", TargetInput = "1818" }]);

        ProfileTransferService.ApplyImportedProfile(imported, dstSettings, dstFlagMgr, dstTargetStore, replaceSavedTargets: false);

        dstSettings.Theme.Should().Be(Theme.Dark);
        dstSettings.CustomIntegrations.Should().BeEmpty();
        dstFlagMgr.GetFlag("OldFlagToRemove").Should().BeNull();
        dstFlagMgr.GetFlag("FFlagTestBool")?.ToString().Should().Be("True");
        dstTargetStore.LoadAll().Should().HaveCount(2);

        ProfileTransferService.ApplyImportedProfile(imported, dstSettings, dstFlagMgr, dstTargetStore, replaceSavedTargets: true, allowCustomIntegrations: true);
        dstSettings.CustomIntegrations.Should().ContainSingle(i => i.Name == "OBS");
        dstTargetStore.LoadAll().Should().ContainSingle(t => t.Name == "Blox Fruits");
    }

    [Fact]
    public void ProfileTransferService_InvalidOrUnsupportedBundle_ThrowsInvalidDataException()
    {
        string badJsonPath = Path.Combine(_tempDir, "bad.json");
        File.WriteAllText(badJsonPath, "{ not-valid-json");
        Action actBadJson = () => ProfileTransferService.ImportFromFile(badJsonPath);
        actBadJson.Should().Throw<InvalidDataException>();

        string futureSchemaPath = Path.Combine(_tempDir, "future.json");
        File.WriteAllText(futureSchemaPath, "{\"SchemaVersion\": 999, \"Settings\": {}}");
        Action actFuture = () => ProfileTransferService.ImportFromFile(futureSchemaPath);
        actFuture.Should().Throw<InvalidDataException>();
    }

    // -------------------------------------------------------------------------
    // 2. AppUpdateService
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("v1.2.3", 1, 2, 3, 0)]
    [InlineData("1.2.3", 1, 2, 3, 0)]
    [InlineData("v2.0.0-beta.1", 2, 0, 0, 0)]
    [InlineData("V3.4.5+abc123", 3, 4, 5, 0)]
    public void AppUpdateService_ParseVersion_ParsesValidTags(string raw, int major, int minor, int build, int rev)
    {
        var parsed = AppUpdateService.ParseVersion(raw);
        parsed.Should().Be(new Version(major, minor, build, rev));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-version")]
    public void AppUpdateService_ParseVersion_ReturnsNullForInvalidTags(string raw)
    {
        AppUpdateService.ParseVersion(raw).Should().BeNull();
    }

    [Fact]
    public async Task AppUpdateService_CheckForUpdateAsync_DetectsNewerAndSameVersions()
    {
        var paths = new PathProvider(_tempDir);
        const string sampleSha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        string newerReleaseJson = """
            {
              "tag_name": "v1.4.0",
              "html_url": "https://github.com/MeowHappyHappyHappy/BaBaBooeyStrap/releases/tag/v1.4.0",
              "body": "Added WebView2 login & quick launch.",
              "assets": [
                {
                  "name": "BaBaBooeyStrap.exe",
                  "browser_download_url": "https://github.com/MeowHappyHappyHappy/BaBaBooeyStrap/releases/download/v1.4.0/BaBaBooeyStrap.exe",
                  "digest": "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                  "size": 1048576
                }
              ]
            }
            """;

        using var newerClient = new HttpClient(new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(newerReleaseJson, Encoding.UTF8, "application/json")
            }));

        var updaterNewer = new AppUpdateService(paths, newerClient, currentVersionOverride: new Version(1, 0, 0, 0));
        var infoNewer = await updaterNewer.CheckForUpdateAsync();
        infoNewer.IsUpdateAvailable.Should().BeTrue();
        infoNewer.LatestVersion.Should().Be(new Version(1, 4, 0, 0));
        infoNewer.AssetDownloadUrl.Should().Be("https://github.com/MeowHappyHappyHappy/BaBaBooeyStrap/releases/download/v1.4.0/BaBaBooeyStrap.exe");
        infoNewer.AssetSha256.Should().Be(sampleSha256);
        infoNewer.ReleaseNotes.Should().Contain("WebView2");

        var updaterSame = new AppUpdateService(paths, newerClient, currentVersionOverride: new Version(1, 4, 0, 0));
        var infoSame = await updaterSame.CheckForUpdateAsync();
        infoSame.IsUpdateAvailable.Should().BeFalse();
    }

    [Fact]
    public void AppUpdateService_BuildSelfUpdateScript_ContainsPidWaitCopyAndRelaunch()
    {
        string script = AppUpdateService.BuildSelfUpdateScript(
            currentPid: 9876,
            downloadedExe: @"C:\Temp\new.exe",
            currentExe: @"C:\App\BaBaBooeyStrap.exe",
            installedExe: @"C:\Local\BaBaBooeyStrap.exe");

        script.Should().Contain("PID eq 9876");
        script.Should().Contain(@"copy /Y ""C:\Temp\new.exe"" ""C:\App\BaBaBooeyStrap.exe""");
        script.Should().Contain(@"copy /Y ""C:\Temp\new.exe"" ""C:\Local\BaBaBooeyStrap.exe""");
        script.Should().Contain(@"start """" ""C:\App\BaBaBooeyStrap.exe"" -menu");
    }

    // -------------------------------------------------------------------------
    // 3. RobloxAuthService.GetGameInfoByPlaceIdAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RobloxAuthService_GetGameInfoByPlaceIdAsync_ResolvesNameAndThumbnail_AndHandlesErrors()
    {
        using var client = new HttpClient(new StubHttpMessageHandler(req =>
        {
            string uri = req.RequestUri!.ToString();
            if (uri.Contains("/universes/v1/places/2753915549/universe"))
            {
                return JsonOk("{\"universeId\":994732206}");
            }
            if (uri.Contains("/v1/games?universeIds=994732206"))
            {
                return JsonOk("{\"data\":[{\"id\":994732206,\"name\":\"Blox Fruits\"}]}");
            }
            if (uri.Contains("/v1/games/icons?universeIds=994732206"))
            {
                return JsonOk("{\"data\":[{\"targetId\":994732206,\"imageUrl\":\"https://tr.rbxcdn.com/bloxfruits.png\"}]}");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        using var auth = new RobloxAuthService(client);
        var info = await auth.GetGameInfoByPlaceIdAsync(2753915549);

        info.Should().NotBeNull();
        info!.PlaceId.Should().Be(2753915549);
        info.UniverseId.Should().Be(994732206);
        info.Name.Should().Be("Blox Fruits");
        info.ThumbnailUrl.Should().Be("https://tr.rbxcdn.com/bloxfruits.png");

        var missing = await auth.GetGameInfoByPlaceIdAsync(999999999);
        missing.Should().BeNull();

        var invalid = await auth.GetGameInfoByPlaceIdAsync(0);
        invalid.Should().BeNull();
    }

    // -------------------------------------------------------------------------
    // 4. CustomIntegrationRunner
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CustomIntegrationRunner_PrePostFilteringMissingSkipAndAutoClose_WorksDeterministically()
    {
        var launched = new List<string>();
        var terminated = new List<int>();
        int nextPid = 5000;

        string missingExe = Path.Combine(_tempDir, "missing_tool.exe");
        string preToolExe = Path.Combine(_tempDir, "pre_tool.exe");
        string postAutoExe = Path.Combine(_tempDir, "post_auto.exe");
        string postKeepExe = Path.Combine(_tempDir, "post_keep.exe");
        File.WriteAllText(preToolExe, "stub");
        File.WriteAllText(postAutoExe, "stub");
        File.WriteAllText(postKeepExe, "stub");

        using var runner = new CustomIntegrationRunner(
            processStarter: psi =>
            {
                launched.Add($"{psi.FileName} {psi.Arguments}".Trim());
                return ++nextPid;
            },
            processTerminator: pid => terminated.Add(pid),
            delayAsync: (_, _) => Task.CompletedTask);

        var integrations = new List<CustomIntegration>
        {
            new() { Name = "PreTool", Location = preToolExe, LaunchArgs = "/c echo pre", Delay = 10, PreLaunch = true, AutoClose = true },
            new() { Name = "MissingTool", Location = missingExe, PreLaunch = true, AutoClose = true },
            new() { Name = "UnrootedTool", Location = "cmd.exe", PreLaunch = true, AutoClose = true },
            new() { Name = "UncTool", Location = @"\\evil-server\share\tool.exe", PreLaunch = true, AutoClose = true },
            new() { Name = "UriTool", Location = "https://example.com/tool.exe", PreLaunch = false, AutoClose = true },
            new() { Name = "PostToolAuto", Location = postAutoExe, LaunchArgs = "", PreLaunch = false, AutoClose = true },
            new() { Name = "PostToolKeep", Location = postKeepExe, LaunchArgs = "", PreLaunch = false, AutoClose = false }
        };

        var prePids = await runner.LaunchIntegrationsAsync(integrations, preLaunchOnly: true);
        prePids.Should().ContainSingle().Which.Should().Be(5001);
        launched.Should().ContainSingle(s => s.StartsWith(preToolExe, StringComparison.OrdinalIgnoreCase));

        runner.BindPreLaunchedToPid(prePids, robloxPid: 777);

        var postPids = await runner.LaunchIntegrationsAsync(integrations, preLaunchOnly: false, trackedRobloxPid: 777);
        postPids.Should().ContainSingle().Which.Should().Be(5002);
        launched.Should().HaveCount(3);

        // Bind an integration to a different Roblox PID to confirm isolation
        runner.BindPreLaunchedToPid([6001], robloxPid: 888);

        int closed = runner.CloseIntegrationsForPid(777);
        closed.Should().Be(2);
        terminated.Should().BeEquivalentTo([5001, 5002]);
        terminated.Should().NotContain(6001);
    }

    // -------------------------------------------------------------------------
    // 5. DiagnosticsService
    // -------------------------------------------------------------------------

    [Fact]
    public void DiagnosticsService_ResetClearAndExportZip_WorksSafelyWithoutExposingAccounts()
    {
        var paths = new PathProvider(_tempDir);
        paths.EnsureDirectoriesExist();
        Directory.CreateDirectory(paths.RobloxLogs);
        Directory.CreateDirectory(paths.RobloxCache);
        Directory.CreateDirectory(Path.Combine(paths.RobloxCache, "http"));

        // Plant sensitive account file + logs + caches + fastflags
        File.WriteAllText(paths.AccountsFile, "ENCRYPTED_OR_RAW_SECRET");

        var flagMgr = new FastFlagManager(paths.GetClientSettingsPath());
        flagMgr.SetFlag("FFlagTest", "True");
        flagMgr.Save();

        string activeLog = Path.Combine(paths.Logs, "active_launcher.log");
        string oldLog = Path.Combine(paths.Logs, "old_launcher.log");
        string robloxLog = Path.Combine(paths.RobloxLogs, "Roblox_01.log");
        string cacheFile = Path.Combine(paths.RobloxCache, "http", "cached.bin");

        File.WriteAllText(activeLog, "active log content");
        File.WriteAllText(oldLog, "old log content");
        File.WriteAllText(robloxLog, "roblox log content");
        File.WriteAllText(cacheFile, "cached bytes");

        string zipPath = Path.Combine(_tempDir, "out", "diagnostics.zip");
        DiagnosticsService.ExportDiagnosticsZip(zipPath, paths, new Settings(), new State(), new RobloxState(), "1.0.0");

        File.Exists(zipPath).Should().BeTrue();
        using (var archive = ZipFile.OpenRead(zipPath))
        {
            var entryNames = archive.Entries.Select(e => e.FullName).ToList();
            entryNames.Should().Contain("system_info.txt");
            entryNames.Should().Contain("settings.json");
            entryNames.Should().Contain("state.json");
            entryNames.Should().Contain("roblox_state.json");
            entryNames.Should().Contain("ClientAppSettings.json");
            entryNames.Should().Contain("logs/app/active_launcher.log");
            entryNames.Should().Contain("logs/roblox/Roblox_01.log");
            entryNames.Should().NotContain(n => n.Contains("accounts.dat", StringComparison.OrdinalIgnoreCase));
        }

        DiagnosticsService.ResetFastFlags(flagMgr);
        flagMgr.Flags.Should().BeEmpty();
        File.ReadAllText(flagMgr.FilePath).Trim().Should().Be("{}");

        var logger = new StubLoggerWithActiveFile(activeLog);
        var report = DiagnosticsService.ClearAllCachesImmediate(paths, logger);
        report.FilesDeleted.Should().BeGreaterThanOrEqualTo(3);
        File.Exists(activeLog).Should().BeTrue();
        File.Exists(oldLog).Should().BeFalse();
        File.Exists(robloxLog).Should().BeFalse();
        File.Exists(cacheFile).Should().BeFalse();
    }

    // -------------------------------------------------------------------------
    // 6. NotifyIconWrapper Quick Launch
    // -------------------------------------------------------------------------

    [Fact]
    public void NotifyIconWrapper_QuickLaunch_ExposesAccountsAndMenuAction()
    {
        var accounts = new List<RobloxAccount>
        {
            new() { Username = "Alice", DisplayName = "Alice", UserId = 1 },
            new() { Username = "Bob", DisplayName = "Bob", UserId = 2 }
        };
        var targets = new List<SavedJoinTarget>
        {
            new() { Name = "Adopt Me", TargetInput = "920587237", GameName = "Adopt Me!" }
        };

        using var tray = new NotifyIconWrapper(
            headlessMode: true,
            getAccounts: () => accounts,
            getSavedTargets: () => targets);

        tray.Initialize();

        tray.ContextMenuActions.Should().Contain(["Settings", "Launch Roblox", "Quick Launch", "Multi-Instance Status", "Server Location", "Exit"]);
        tray.LastQuickLaunchAccountNames.Should().Equal("Alice", "Bob");
    }

    // -------------------------------------------------------------------------
    // 7. ViewModels: Channel Reset, CustomIntegrations, Dirty Tracking & Game Info
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ViewModels_ChannelResetIntegrationsDirtyTrackingAndGameInfo_WorkAsExpected()
    {
        LocalizationService.Instance.ApplyLanguage(AppLanguage.English);
        try
        {
        var paths = new PathProvider(_tempDir);
        var settings = new Settings { Language = AppLanguage.English };
        var vm = new MainWindowViewModel(settings, paths: paths);

        vm.HasUnsavedChanges.Should().BeFalse();

        // Non-persisted update status props MUST NOT trigger dirty state
        vm.LaunchSettings.IsCheckingAppUpdate = true;
        vm.LaunchSettings.SetAppUpdateStatus("Str_About_CheckingUpdates");
        vm.LaunchSettings.IsCheckingAppUpdate = false;
        vm.HasUnsavedChanges.Should().BeFalse();

        // Persisted settings props MUST trigger dirty state
        vm.LaunchSettings.CheckAppUpdates = false;
        vm.HasUnsavedChanges.Should().BeTrue();

        // Reset Channel defaults
        vm.LaunchSettings.Channel = "zflag";
        vm.LaunchSettings.RobloxDomain = "custom.example.com";
        vm.LaunchSettings.Locale = "ja-jp";
        vm.LaunchSettings.SelectedWebEnvironment = WebEnvironment.Staging;
        vm.LaunchSettings.SelectedDistributorType = DistributorType.VNGGames;
        vm.LaunchSettings.ForceRobloxLanguage = true;

        vm.LaunchSettings.ResetChannelDefaults();

        vm.LaunchSettings.Channel.Should().Be("production");
        vm.LaunchSettings.RobloxDomain.Should().Be("roblox.com");
        vm.LaunchSettings.Locale.Should().Be("nil");
        vm.LaunchSettings.SelectedWebEnvironment.Should().Be(WebEnvironment.Production);
        vm.LaunchSettings.SelectedDistributorType.Should().Be(DistributorType.Global);
        vm.LaunchSettings.ForceRobloxLanguage.Should().BeFalse();

        // CustomIntegrations add/update/remove
        var obs = new CustomIntegration { Name = "OBS", Location = "obs64.exe" };
        vm.WindowIntegrations.AddCustomIntegration(obs);
        vm.WindowIntegrations.CustomIntegrations.Should().ContainSingle();

        var obsUpdated = new CustomIntegration { Name = "OBS Updated", Location = "obs64.exe", Delay = 500 };
        vm.WindowIntegrations.UpdateCustomIntegration(0, obsUpdated);
        vm.WindowIntegrations.CustomIntegrations[0].Name.Should().Be("OBS Updated");

        vm.WindowIntegrations.RemoveCustomIntegration(obsUpdated).Should().BeTrue();
        vm.WindowIntegrations.CustomIntegrations.Should().BeEmpty();

        // AccountsViewModel debounced game info resolution + auto TargetName on save
        var targetStore = new JoinTargetStore(paths.TargetsFile);
        var stubAuth = new StubGameInfoAuthService();
        var accountsVm = new AccountsViewModel(
            settings,
            paths,
            new DpapiAccountStore(paths.AccountsFile),
            targetStore,
            multiInstanceController: null,
            processSupervisor: null,
            launcher: null,
            new BaBaBooeyStrap.Bootstrapper.Settings.GlobalSettingsManager(paths.RobloxGlobalSettings),
            stateManager: null,
            setLocalizedStatus: (_, _) => { },
            authService: stubAuth);

        accountsVm.LaunchTargetInput = "2753915549";
        await Task.Delay(550);

        accountsVm.HasResolvedGameInfo.Should().BeTrue();
        accountsVm.ResolvedGameName.Should().Be("Blox Fruits");
        accountsVm.ResolvedGamePlaceIdText.Should().Be("Place ID: 2753915549");
        accountsVm.ResolvedGameThumbnailUrl.Should().Be("https://tr.rbxcdn.com/thumb.png");

        // Saving with empty TargetName auto-fills from ResolvedGameName
        accountsVm.TargetName = string.Empty;
        accountsVm.SaveCurrentJoinTarget().Should().BeTrue();
        accountsVm.TargetName.Should().Be("Blox Fruits");
        targetStore.LoadAll().Should().ContainSingle(t => t.Name == "Blox Fruits" && t.PlaceId == 2753915549);
        }
        finally
        {
            LocalizationService.Instance.ApplyLanguage(AppLanguage.English);
        }
    }

    // -------------------------------------------------------------------------
    // 8. AccountsPage UX: Running State, Merged Target Selector & Segmented Mode
    // -------------------------------------------------------------------------

    [Fact]
    public void AccountItem_RunningStateTransitions_UpdateStatusColorsAndTooltips()
    {
        LocalizationService.Instance.ApplyLanguage(AppLanguage.English);
        try
        {
            var account = new RobloxAccount { UserId = 1001, Username = "PlayerOne", DisplayName = "PlayerOne" };
            var item = new AccountItem(account);

            item.RunState.Should().Be(AccountRunState.NotRunning);
            item.IsRunning.Should().BeFalse();
            item.RunStatusText.Should().Be("Idle");
            item.RunStatusColorHex.Should().Be("#FF9CA3AF");
            item.RunStatusToolTip.Should().Contain("No running Roblox window");

            item.SetLaunching(true);
            item.RunState.Should().Be(AccountRunState.Launching);
            item.IsRunning.Should().BeFalse();
            item.RunStatusText.Should().Be("Launching");
            item.RunStatusColorHex.Should().Be("#FF3B82F6");
            item.RunStatusToolTip.Should().Contain("Launching Roblox");

            var focusedInstance = new RunningRobloxInstance(
                ProcessId: 4321,
                WindowHandle: 100,
                SlotIndex: 2,
                AccountId: account.Id,
                UserId: 1001,
                Username: "PlayerOne",
                DisplayName: "PlayerOne",
                Notes: null,
                IsExternal: false,
                IsMinimized: false,
                IsFocused: true,
                MonitorDeviceName: "\\\\.\\DISPLAY1",
                MonitorIndex: 1,
                StartedAtUtc: DateTimeOffset.UtcNow);

            item.UpdateRunningState(focusedInstance);
            item.RunState.Should().Be(AccountRunState.Running);
            item.IsRunning.Should().BeTrue();
            item.IsRunningFocused.Should().BeTrue();
            item.RunningProcessId.Should().Be(4321);
            item.RunningSlotIndex.Should().Be(2);
            item.RunningMonitorIndex.Should().Be(1);
            item.RunStatusText.Should().Be("Running");
            item.RunStatusColorHex.Should().Be("#FF10B981");
            item.RunStatusToolTip.Should().Contain("Focused").And.Contain("#2").And.Contain("PID 4321").And.Contain("M1");

            // Clearing launching flag while process is running must preserve Running state
            item.SetLaunching(false);
            item.RunState.Should().Be(AccountRunState.Running);

            var minimizedInstance = focusedInstance with { IsMinimized = true, IsFocused = false, MonitorIndex = 2 };
            item.UpdateRunningState(minimizedInstance);
            item.RunState.Should().Be(AccountRunState.Minimized);
            item.IsRunning.Should().BeTrue();
            item.IsRunningFocused.Should().BeFalse();
            item.RunStatusText.Should().Be("Minimized");
            item.RunStatusColorHex.Should().Be("#FFF59E0B");
            item.RunStatusToolTip.Should().Contain("Minimized").And.Contain("M2");

            item.UpdateRunningState(null);
            item.RunState.Should().Be(AccountRunState.NotRunning);
            item.IsRunning.Should().BeFalse();
            item.RunningProcessId.Should().BeNull();
        }
        finally
        {
            LocalizationService.Instance.ApplyLanguage(AppLanguage.English);
        }
    }

    [Fact]
    public void AccountsViewModel_SyncAccountRunningStates_MatchesAndPrioritizesFocusedInstance()
    {
        var paths = new PathProvider(_tempDir);
        var settings = new Settings { Language = AppLanguage.English };
        var accountStore = new DpapiAccountStore(paths.AccountsFile);
        var accById = new RobloxAccount { UserId = 101, Username = "MatchById", DisplayName = "MatchById" };
        var accByUserId = new RobloxAccount { UserId = 202, Username = "OldName", DisplayName = "OldName" };
        var accByUsername = new RobloxAccount { UserId = 0, Username = "MatchByName", DisplayName = "MatchByName" };
        accountStore.Upsert(accById);
        accountStore.Upsert(accByUserId);
        accountStore.Upsert(accByUsername);

        using var vm = new AccountsViewModel(
            settings,
            paths,
            accountStore,
            new JoinTargetStore(paths.TargetsFile),
            multiInstanceController: null,
            processSupervisor: null,
            launcher: null,
            new BaBaBooeyStrap.Bootstrapper.Settings.GlobalSettingsManager(paths.RobloxGlobalSettings),
            stateManager: null,
            setLocalizedStatus: (_, _) => { });

        var instances = new List<RunningRobloxInstance>
        {
            new(1111, 1, 1, accById.Id, 101, "MatchById", "MatchById", null, false, false, false, null, 1, DateTimeOffset.UtcNow),
            new(2222, 2, 2, accById.Id, 101, "MatchById", "MatchById", null, false, false, true, null, 2, DateTimeOffset.UtcNow),
            new(3333, 3, 3, Guid.NewGuid(), 202, "RenamedUser", "RenamedUser", null, false, true, false, null, 1, DateTimeOffset.UtcNow),
            new(4444, 4, 4, null, null, "matchbyname", "matchbyname", null, true, false, false, null, 1, DateTimeOffset.UtcNow)
        };

        vm.SyncAccountRunningStates(instances);

        var item1 = vm.AccountItems.Single(a => a.Id == accById.Id);
        item1.RunState.Should().Be(AccountRunState.Running);
        item1.RunningProcessId.Should().Be(2222);
        item1.IsRunningFocused.Should().BeTrue();

        var item2 = vm.AccountItems.Single(a => a.Id == accByUserId.Id);
        item2.RunState.Should().Be(AccountRunState.Minimized);
        item2.RunningProcessId.Should().Be(3333);

        var item3 = vm.AccountItems.Single(a => a.Id == accByUsername.Id);
        item3.RunState.Should().Be(AccountRunState.Running);
        item3.RunningProcessId.Should().Be(4444);

        vm.SyncAccountRunningStates(Array.Empty<RunningRobloxInstance>());
        vm.AccountItems.Should().OnlyContain(a => a.RunState == AccountRunState.NotRunning && !a.IsRunning);
    }

    [Fact]
    public void AccountsViewModel_MergedSavedTargetAndSegmentedMode_WorkCorrectly()
    {
        var paths = new PathProvider(_tempDir);
        var settings = new Settings { Language = AppLanguage.English };
        var targetStore = new JoinTargetStore(paths.TargetsFile);

        using var vm = new AccountsViewModel(
            settings,
            paths,
            new DpapiAccountStore(paths.AccountsFile),
            targetStore,
            multiInstanceController: null,
            processSupervisor: null,
            launcher: null,
            new BaBaBooeyStrap.Bootstrapper.Settings.GlobalSettingsManager(paths.RobloxGlobalSettings),
            stateManager: null,
            setLocalizedStatus: (_, _) => { });

        // Segmented control two-way bindings & JobId visibility
        vm.IsGameServerTargetMode.Should().BeTrue();
        vm.IsFollowUserModeSelected.Should().BeFalse();
        vm.IsJobIdInputVisible.Should().BeTrue();

        vm.IsFollowUserModeSelected = true;
        vm.IsFollowUserTargetMode.Should().BeTrue();
        vm.IsGameServerTargetMode.Should().BeFalse();
        vm.IsJobIdInputVisible.Should().BeFalse();

        vm.IsGameServerTargetMode = true;
        vm.IsFollowUserTargetMode.Should().BeFalse();
        vm.IsFollowUserModeSelected.Should().BeFalse();
        vm.IsJobIdInputVisible.Should().BeTrue();

        // Save new target via merged selector TargetName + TargetInput
        vm.CanDeleteTarget.Should().BeFalse();
        vm.TargetName = "Private Hub";
        vm.LaunchTargetInput = "1818";
        vm.SaveCurrentJoinTarget().Should().BeTrue();

        vm.SavedTargets.Should().ContainSingle(t => t.Name == "Private Hub");
        vm.SelectedSavedTarget.Should().NotBeNull();
        vm.CanDeleteTarget.Should().BeTrue();

        // When user clears SelectedSavedTarget (e.g. typing in editable ComboBox) matching name still resolves for delete
        vm.SelectedSavedTarget = null;
        vm.TargetName = "private hub";
        vm.ResolveTargetForDelete().Should().NotBeNull();
        vm.CanDeleteTarget.Should().BeTrue();

        vm.DeleteSelectedJoinTarget().Should().BeTrue();
        vm.SavedTargets.Should().BeEmpty();
        vm.TargetName.Should().BeEmpty();
        vm.CanDeleteTarget.Should().BeFalse();
    }

    private static HttpResponseMessage JsonOk(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_handler(request));
    }

    private sealed class StubLoggerWithActiveFile : ILogger
    {
        public StubLoggerWithActiveFile(string activeFile) => FileLocation = activeFile;
        public LogLevel MinLevel { get; set; } = LogLevel.Info;
        public string? FileLocation { get; }
        public bool Initialized => true;
        public bool NoWriteMode => true;
        public IReadOnlyList<string> History { get; } = Array.Empty<string>();
        public void Initialize(string? customLogDirectory = null, bool useTempDir = false) { }
        public void Log(LogLevel level, string identifier, string message) { }
        public void Debug(string identifier, string message) { }
        public void Info(string identifier, string message) { }
        public void Warn(string identifier, string message) { }
        public void Error(string identifier, string message) { }
        public void Exception(string identifier, Exception ex) { }
        public void Flush() { }
        public void Dispose() { }
    }

    private sealed class StubGameInfoAuthService : IRobloxAuthService
    {
        public Task<AuthenticatedUser> GetAuthenticatedUserAsync(string cookie, CancellationToken ct = default) =>
            Task.FromResult(new AuthenticatedUser(1, "Stub", "Stub"));

        public Task<string> GenerateAuthTicketAsync(string cookie, CancellationToken ct = default) =>
            Task.FromResult("TICKET");

        public Task<ResolvedShareLink> ResolveShareLinkAsync(string cookie, string shareCode, CancellationToken ct = default) =>
            Task.FromResult(new ResolvedShareLink(1, "CODE"));

        public Task<string?> GetAvatarThumbnailUrlAsync(long userId, string size = "150x150", bool isCircular = true, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task<long> ResolveUserIdByUsernameAsync(string username, CancellationToken ct = default) =>
            Task.FromResult(1L);

        public Task<RobloxUserPresence> GetUserPresenceAsync(string cookie, long userId, CancellationToken ct = default) =>
            Task.FromResult(new RobloxUserPresence(userId, 0, null, null, null, null, null));

        public Task<RobloxGameInfo?> GetGameInfoByPlaceIdAsync(long placeId, CancellationToken ct = default) =>
            Task.FromResult<RobloxGameInfo?>(new RobloxGameInfo(placeId, 994732206L, "Blox Fruits", "https://tr.rbxcdn.com/thumb.png"));

        public void Dispose() { }
    }
}
