using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Windows.Data;
using System.Xml.Linq;
using System.Xml.XPath;
using BaBaBooeyStrap.App.Converters;
using BaBaBooeyStrap.App.Services;
using BaBaBooeyStrap.App.Services.Pipeline;
using BaBaBooeyStrap.App.ViewModels;
using BaBaBooeyStrap.App.ViewModels.Sections;
using BaBaBooeyStrap.Bootstrapper.Deployment;
using BaBaBooeyStrap.Bootstrapper.Deployment.Abstractions;
using BaBaBooeyStrap.Bootstrapper.Deployment.Models;
using BaBaBooeyStrap.Bootstrapper.FastFlags;
using BaBaBooeyStrap.Bootstrapper.Mods;
using BaBaBooeyStrap.Bootstrapper.Settings;
using BaBaBooeyStrap.Core;
using BaBaBooeyStrap.Core.Interop;
using BaBaBooeyStrap.Core.Logging;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Models.Enums;
using BaBaBooeyStrap.Core.Roblox;
using BaBaBooeyStrap.Core.Storage;
using BaBaBooeyStrap.Integrations.Cleaner;
using BaBaBooeyStrap.Integrations.Discord;
using BaBaBooeyStrap.Integrations.Matchmaking;
using BaBaBooeyStrap.Integrations.Watcher;
using BaBaBooeyStrap.Integrations.Window;
using BaBaBooeyStrap.MultiInstance;
using BaBaBooeyStrap.Tests.Mocks;

namespace BaBaBooeyStrap.Tests.App;

[Collection("LocalizationSingleton")]
public sealed class AuditFeedbackFixTests : IDisposable
{
    private readonly string _tempDir;

    public AuditFeedbackFixTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "AuditFeedbackFix_" + Guid.NewGuid().ToString("N"));
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
    // Phase 1: Critical Fixes Verification
    // -------------------------------------------------------------------------

    [Fact]
    public void Phase1_AppUpdateService_ScriptUsesUtf8CodePageBoundedWaitExactPidAndCopyRetries()
    {
        string script = AppUpdateService.BuildSelfUpdateScript(
            currentPid: 4321,
            downloadedExe: @"C:\Users\測試%User\new.exe",
            currentExe: @"C:\Apps\測試\BaBaBooeyStrap.exe",
            installedExe: @"C:\Apps\測試\BaBaBooeyStrap.exe");

        script.Should().Contain("chcp 65001 >NUL");
        script.Should().Contain(@"C:\Users\測試%%User\new.exe");
        script.Should().Contain("tasklist /FI \"PID eq 4321\" /FO CSV /NH");
        script.Should().Contain("findstr /C:\"\\\"4321\\\"\"");
        script.Should().NotContain("timeout /t 1");
        script.Should().Contain("ping -n 2 127.0.0.1 >NUL");
        script.Should().Contain("if !WAIT_COUNT! GEQ 60");
        script.Should().Contain("for /L %%I in (1,1,10)");
        script.Should().Contain("if !COPY_OK!==1 del /F /Q");
    }

    [Fact]
    public async Task Phase1_ModsApplicationStep_RemovingOverwrittenMod_RestoresOriginalFileFromPackageCache()
    {
        var paths = new PathProvider(_tempDir);
        string versionGuid = "version-mod-restore";
        string versionDir = paths.GetVersionDirectory(versionGuid);
        string soundPath = Path.Combine(versionDir, "content", "sounds", "ouch.ogg");
        Directory.CreateDirectory(Path.GetDirectoryName(soundPath)!);
        byte[] originalOgg = Encoding.ASCII.GetBytes("OggS-ORIGINAL-SOUND");
        await File.WriteAllBytesAsync(soundPath, originalOgg);

        // Cache the package zip under Downloads/{versionGuid}/content-sounds.zip
        string dlVersionDir = Path.Combine(paths.Downloads, versionGuid);
        Directory.CreateDirectory(dlVersionDir);
        string soundsZip = Path.Combine(dlVersionDir, "content-sounds.zip");
        using (var archive = ZipFile.Open(soundsZip, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("ouch.ogg");
            await using var stream = entry.Open();
            await stream.WriteAsync(originalOgg);
        }

        // Apply custom mod overwriting ouch.ogg + adding mod-only file
        string modSound = Path.Combine(paths.Modifications, "content", "sounds", "ouch.ogg");
        string modOnlySound = Path.Combine(paths.Modifications, "content", "sounds", "custom_extra.ogg");
        Directory.CreateDirectory(Path.GetDirectoryName(modSound)!);
        await File.WriteAllBytesAsync(modSound, Encoding.ASCII.GetBytes("OggS-MODDED-SOUND"));
        await File.WriteAllBytesAsync(modOnlySound, Encoding.ASCII.GetBytes("OggS-MOD-ONLY"));

        var settings = new Settings { UseFastFlagManager = false };
        var robloxStateMgr = new JsonManager<RobloxState>(paths.RobloxStateFile);
        var step = new ModsApplicationStep(paths, robloxStateMgr, Logger.Current);
        var context = new BootstrapPipelineContext(new LaunchSettings(), settings, isStudio: false)
        {
            TargetVersionDirectory = versionDir
        };

        await step.ExecuteAsync(context);
        (await File.ReadAllBytesAsync(soundPath)).Should().Equal(Encoding.ASCII.GetBytes("OggS-MODDED-SOUND"));
        File.Exists(Path.Combine(versionDir, "content", "sounds", "custom_extra.ogg")).Should().BeTrue();

        // Now user deletes both mod files (even remove .backups to exercise packageFileRestorer too)
        File.Delete(modSound);
        File.Delete(modOnlySound);
        string backupsDir = Path.Combine(paths.Modifications, ".backups");
        if (Directory.Exists(backupsDir))
        {
            Directory.Delete(backupsDir, recursive: true);
        }

        await step.ExecuteAsync(context);

        File.Exists(soundPath).Should().BeTrue();
        (await File.ReadAllBytesAsync(soundPath)).Should().Equal(originalOgg);
        File.Exists(Path.Combine(versionDir, "content", "sounds", "custom_extra.ogg")).Should().BeFalse();
    }

    [Fact]
    public async Task Phase1_PackageDeploymentStep_CorruptPackageExtraction_FailsWithoutMarkingVersionInstalled()
    {
        var paths = new PathProvider(_tempDir);
        string oldGuid = "version-old-ok";
        string newGuid = "version-new-corrupt";
        string manifestBody =
            "v0\r\n" +
            "RobloxApp.zip\r\n" +
            "deadbeef000000000000000000000000\r\n" +
            "4\r\n" +
            "4\r\n" +
            "shaders.zip\r\n";

        byte[] appBytes;
        using (var ms = new MemoryStream())
        {
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = archive.CreateEntry("RobloxPlayerBeta.exe");
                await using var s = entry.Open();
                await s.WriteAsync(new byte[] { 0x4D, 0x5A });
            }
            appBytes = ms.ToArray();
        }
        byte[] corruptShadersBytes = new byte[] { 0x00, 0x01, 0x02, 0x03 }; // invalid zip

        string appMd5 = Convert.ToHexString(MD5.HashData(appBytes)).ToLowerInvariant();
        string shadersMd5 = Convert.ToHexString(MD5.HashData(corruptShadersBytes)).ToLowerInvariant();
        string fullManifest =
            $"v0\r\nRobloxApp.zip\r\n{appMd5}\r\n{appBytes.Length}\r\n{appBytes.Length}\r\n" +
            $"shaders.zip\r\n{shadersMd5}\r\n{corruptShadersBytes.Length}\r\n{corruptShadersBytes.Length}\r\n";
        _ = manifestBody;

        var handler = new MockHttpMessageHandler
        {
            FallbackHandler = (req, _) =>
            {
                string u = req.RequestUri!.ToString();
                if (u.EndsWith("rbxPkgManifest.txt", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(fullManifest) });
                }
                if (u.EndsWith("RobloxApp.zip", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(appBytes) });
                }
                if (u.EndsWith("shaders.zip", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(corruptShadersBytes) });
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
        };
        using var client = handler.ToHttpClient();

        var settingsMgr = new JsonManager<Settings>(paths.SettingsFile);
        var stateMgr = new JsonManager<State>(paths.StateFile);
        var robloxStateMgr = new JsonManager<RobloxState>(paths.RobloxStateFile);
        robloxStateMgr.Prop.Player.VersionGuid = oldGuid;

        var step = new PackageDeploymentStep(paths, stateMgr, robloxStateMgr, client, Logger.Current);
        var context = new BootstrapPipelineContext(new LaunchSettings(), settingsMgr.Prop, isStudio: false)
        {
            ExistingVersionGuid = oldGuid,
            ResolvedVersionGuid = newGuid,
            LatestVersion = "0.700.0.1",
            CdnBaseUrl = "https://setup.rbxcdn.com",
            RequiresDownload = true
        };

        await step.ExecuteAsync(context);

        context.FailureReason.Should().NotBeNullOrWhiteSpace();
        context.ResolvedVersionGuid.Should().Be(oldGuid);
        robloxStateMgr.Prop.Player.VersionGuid.Should().Be(oldGuid);
    }

    [Fact]
    public async Task Phase1_VersionInstallationValidator_DoesNotExtractStudioPackagesIntoPlayerDir()
    {
        var paths = new PathProvider(_tempDir);
        Directory.CreateDirectory(paths.Downloads);
        string versionDir = paths.GetVersionDirectory("version-player-only");
        Directory.CreateDirectory(versionDir);

        string studioZip = Path.Combine(paths.Downloads, "RobloxStudio.zip");
        using (var archive = ZipFile.Open(studioZip, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("RobloxStudioBeta.exe");
            await using var s = entry.Open();
            await s.WriteAsync(new byte[] { 0x4D, 0x5A });
        }

        await VersionInstallationValidator.TryRepairFromCachedDownloadsAsync(
            paths,
            versionDir,
            isStudio: false,
            Logger.Current);

        File.Exists(Path.Combine(versionDir, "RobloxStudioBeta.exe")).Should().BeFalse();
    }

    [Fact]
    public void Phase1_JoinTargetStore_RecoversCorruptedJsonAndLeavesNoTempFiles()
    {
        string targetPath = Path.Combine(_tempDir, "targets.json");
        File.WriteAllText(targetPath, "{ broken json [");

        var store = new JoinTargetStore(targetPath);
        string? backup = null;
        store.OnCorruptionRecovered += path => backup = path;

        store.LoadAll().Should().BeEmpty();
        store.LastLoadError.Should().NotBeNullOrWhiteSpace();
        backup.Should().NotBeNullOrWhiteSpace();
        File.Exists(backup!).Should().BeTrue();

        store.Upsert(new SavedJoinTarget { Name = "Test", TargetInput = "1818" });
        store.LoadAll().Should().ContainSingle(t => t.Name == "Test");
        Directory.GetFiles(_tempDir, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public void Phase1_MainWindowViewModel_DiscardChanges_RevertsSettingsAndClearsDirtyFlag()
    {
        var paths = new PathProvider(_tempDir);
        var settingsMgr = new JsonManager<Settings>(paths.SettingsFile);
        settingsMgr.Prop.BootstrapperTitle = "SavedTitle";
        settingsMgr.Prop.MinimizeToTray = true;
        settingsMgr.Save();

        var stateMgr = new JsonManager<State>(paths.StateFile);
        stateMgr.Load();
        var store = new DpapiAccountStore(paths.AccountsFile);
        var targets = new JoinTargetStore(paths.TargetsFile);
        var vm = new MainWindowViewModel(
            settingsMgr.Prop,
            store,
            joinTargetStore: targets,
            paths: paths,
            stateManager: stateMgr,
            settingsManager: settingsMgr);

        vm.WindowIntegrations.BootstrapperTitle = "EditedDirtyTitle";
        vm.WindowIntegrations.MinimizeToTray = false;
        vm.HasUnsavedChanges.Should().BeTrue();

        vm.DiscardChanges(settingsMgr);

        vm.HasUnsavedChanges.Should().BeFalse();
        settingsMgr.Prop.BootstrapperTitle.Should().Be("SavedTitle");
        settingsMgr.Prop.MinimizeToTray.Should().BeTrue();
        vm.WindowIntegrations.BootstrapperTitle.Should().Be("SavedTitle");
        vm.WindowIntegrations.MinimizeToTray.Should().BeTrue();
    }

    // -------------------------------------------------------------------------
    // Phase 2: Startup Speed, Multi-Instance & Network UX Verification
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Phase2_VersionCheckStep_UpToDateInstall_SkipsCdnMirrorProbing()
    {
        var paths = new PathProvider(_tempDir);
        string guid = "version-uptodate-123";
        string versionDir = paths.GetVersionDirectory(guid);
        CreateCompleteInstallForTest(versionDir);

        var settingsMgr = new JsonManager<Settings>(paths.SettingsFile);
        settingsMgr.Prop.CheckForUpdates = true;
        var stateMgr = new JsonManager<State>(paths.StateFile);
        var robloxStateMgr = new JsonManager<RobloxState>(paths.RobloxStateFile);
        robloxStateMgr.Prop.Player.VersionGuid = guid;

        var handler = new MockHttpMessageHandler
        {
            FallbackHandler = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"version":"0.701.0.1","clientVersionUpload":"{{guid}}"}""")
            })
        };
        using var httpClient = handler.ToHttpClient();

        int cdnCalls = 0;
        var mirrorStub = new CountingCdnMirrorSelector(() => Interlocked.Increment(ref cdnCalls));
        var step = new VersionCheckStep(paths, settingsMgr, stateMgr, robloxStateMgr, httpClient, Logger.Current, mirrorStub);
        var context = new BootstrapPipelineContext(new LaunchSettings(), settingsMgr.Prop, isStudio: false);

        await step.ExecuteAsync(context);

        context.RequiresDownload.Should().BeFalse();
        context.ResolvedVersionGuid.Should().Be(guid);
        cdnCalls.Should().Be(0);
    }

    [Fact]
    public async Task Phase2_CdnMirrorSelector_ReturnsImmediatelyOnFastestMirror_WithoutWaitingSlowMirrors()
    {
        var fastMirror = CdnMirrorSelectorDefaults.GlobalMirrors[1];
        int requestCount = 0;
        var handler = new MockHttpMessageHandler
        {
            FallbackHandler = async (req, ct) =>
            {
                Interlocked.Increment(ref requestCount);
                if (req.RequestUri!.ToString().StartsWith(fastMirror, StringComparison.OrdinalIgnoreCase))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(CdnMirrorSelectorDefaults.VersionStudioHash)
                    };
                }

                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return new HttpResponseMessage(HttpStatusCode.RequestTimeout);
            }
        };

        using var client = handler.ToHttpClient();
        var selector = new CdnMirrorSelector(client);

        var sw = Stopwatch.StartNew();
        string winner = await selector.ResolveOptimalCdnUrlAsync();
        sw.Stop();

        winner.Should().Be(fastMirror);
        sw.ElapsedMilliseconds.Should().BeLessThan(2000);

        // Second call uses in-memory cache without re-probing
        int beforeCached = requestCount;
        string cached = await selector.ResolveOptimalCdnUrlAsync();
        cached.Should().Be(fastMirror);
        requestCount.Should().Be(beforeCached);
    }

    [Fact]
    public async Task Phase2_PackageDownloader_MonotonicProgress_ChannelUrl_AndMd5Retry()
    {
        byte[] payload = Encoding.UTF8.GetBytes("ValidZipPayloadForRetryTest");
        byte[] corruptPayload = Encoding.UTF8.GetBytes("CorruptZipPayloadForRetryTest!");
        string validMd5 = Convert.ToHexString(MD5.HashData(payload)).ToLowerInvariant();
        int attempts = 0;
        string? seenUrl = null;

        var handler = new MockHttpMessageHandler
        {
            FallbackHandler = (req, _) =>
            {
                seenUrl ??= req.RequestUri!.ToString();
                int current = Interlocked.Increment(ref attempts);
                byte[] body = current == 1 ? corruptPayload : payload;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(body)
                });
            }
        };

        using var client = handler.ToHttpClient();
        var downloader = new PackageDownloader();
        var reports = new List<PackageDownloadProgress>();
        var progress = new SyncProgress<PackageDownloadProgress>(p =>
        {
            lock (reports)
            {
                reports.Add(p);
            }
        });

        var result = await downloader.DownloadPackagesAsync(
            client,
            "https://setup.rbxcdn.com",
            "version-chan",
            [new PackageRecord("RobloxApp.zip", validMd5, payload.Length, payload.Length)],
            _tempDir,
            progress,
            maxDegreeOfParallelism: 1,
            channel: "zlive");

        result.Should().ContainSingle();
        attempts.Should().Be(2);
        seenUrl.Should().Be("https://setup.rbxcdn.com/channel/zlive/version-chan-RobloxApp.zip");

        long prevBytes = 0;
        double prevPct = 0;
        foreach (var r in reports)
        {
            r.DownloadedBytes.Should().BeGreaterThanOrEqualTo(prevBytes);
            r.ProgressPercentage.Should().BeGreaterThanOrEqualTo(prevPct);
            prevBytes = r.DownloadedBytes;
            prevPct = r.ProgressPercentage;
        }
        prevPct.Should().Be(100.0);
    }

    [Fact]
    public async Task Phase2_WindowsMultiInstanceController_ConcurrentEnable_WaitsForInitialAcquisition()
    {
        using var helper = new ParameterizedMutexTestHelper("ConcurrentEnablePhase2");
        using var controller = new WindowsMultiInstanceController(helper.MutexName, helper.EventName);

        var gate = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, 6).Select(_ => Task.Run(() =>
        {
            gate.Wait();
            return controller.Enable();
        })).ToArray();

        gate.Set();
        var statuses = await Task.WhenAll(tasks);

        foreach (var st in statuses)
        {
            st.Enabled.Should().BeTrue();
            st.OwnsMutex.Should().BeTrue();
        }
        controller.Disable();
    }

    [Fact]
    public async Task Phase2_BootstrapperOrchestrator_LaunchAccount_DefersAuthTicketAndReturnsApiFailure()
    {
        var paths = new PathProvider(_tempDir);
        var settingsMgr = new JsonManager<Settings>(paths.SettingsFile);
        settingsMgr.Prop.MultiInstanceEnabled = false;
        var stateMgr = new JsonManager<State>(paths.StateFile);
        var robloxStateMgr = new JsonManager<RobloxState>(paths.RobloxStateFile);

        var events = new List<string>();
        var step = new RecordingPipelineStep(() => events.Add("Pipeline"));
        var authStub = new DeferCheckAuthService(events, throwOnTicket: true);

        using var orchestrator = new BootstrapperOrchestrator(
            paths,
            settingsMgr,
            stateMgr,
            robloxStateMgr,
            authService: authStub,
            pipelineSteps: [step]);

        var account = new RobloxAccount { Username = "TestUser", UserId = 42, Cookie = "valid-cookie" };
        var result = await orchestrator.LaunchAccountAsync(account, new LaunchTarget(LaunchTargetKind.Standard, 1818));

        events.Should().Equal("Pipeline", "AuthTicket");
        result.Success.Should().BeFalse();
        result.AuthHttpStatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // -------------------------------------------------------------------------
    // Phase 3: UI, Multi-Monitor & Daily UX Verification
    // -------------------------------------------------------------------------

    [Fact]
    public void Phase3_ClampWindowBounds_PreservesNegativeCoordinatesOnSecondaryMonitors()
    {
        var saved = new BaBaBooeyStrap.Core.Models.WindowState
        {
            Width = 1100,
            Height = 720,
            Left = -1500,
            Top = -600
        };

        var clamped = MainWindowViewModel.ClampWindowBounds(
            saved,
            virtualLeft: -1920,
            virtualTop: -1080,
            virtualWidth: 3840,
            virtualHeight: 2160);

        clamped.Left.Should().Be(-1500);
        clamped.Top.Should().Be(-600);

        // Out-of-virtual-screen negative bounds clamp to virtualLeft/virtualTop rather than 0
        var offLeft = MainWindowViewModel.ClampWindowBounds(
            new BaBaBooeyStrap.Core.Models.WindowState { Width = 1100, Height = 720, Left = -3000, Top = -2000 },
            virtualLeft: -1920,
            virtualTop: -1080,
            virtualWidth: 3840,
            virtualHeight: 2160);

        offLeft.Left.Should().Be(-1920);
        offLeft.Top.Should().Be(-1080);
    }

    [Fact]
    public void Phase3_RobloxAuthService_NormalizeCookie_StripsPrefixQuotesAndSemicolon()
    {
        RobloxAuthService.NormalizeCookie("\".ROBLOSECURITY=_|WARNING:-DO-NOT-SHARE-THIS.|_abc123; Path=/; _ga=1\"")
            .Should().Be("_|WARNING:-DO-NOT-SHARE-THIS.|_abc123");
        RobloxAuthService.NormalizeCookie("Cookie: .ROBLOSECURITY=token_xyz")
            .Should().Be("token_xyz");
        RobloxAuthService.NormalizeCookie("  'raw_cookie_value'  ")
            .Should().Be("raw_cookie_value");
    }

    [Fact]
    public void Phase3_NullableDoubleConverters_ReturnDoNothingOnNullAndConvertNumbers()
    {
        NullableDoubleToIntConverter.Instance
            .ConvertBack(null, typeof(int), null, CultureInfo.InvariantCulture)
            .Should().Be(Binding.DoNothing);
        NullableDoubleToIntConverter.Instance
            .ConvertBack(144.0, typeof(int), null, CultureInfo.InvariantCulture)
            .Should().Be(144);

        NullableDoubleToFloatConverter.Instance
            .ConvertBack(null, typeof(float), null, CultureInfo.InvariantCulture)
            .Should().Be(Binding.DoNothing);
        NullableDoubleToFloatConverter.Instance
            .ConvertBack(1.5, typeof(float), null, CultureInfo.InvariantCulture)
            .Should().Be(1.5f);
    }

    [Fact]
    public void Phase3_FastFlagEditorViewModel_FiltersWithOrdinalIgnoreCase_AndKeepsPersistentView()
    {
        var vm = new FastFlagEditorViewModel();
        var viewInstance = vm.FlagsView;

        vm.AddFlag("FFlagTestRegexSpecial[1]", "True");
        vm.AddFlag("FIntOtherFlag", "10");

        vm.SearchText = "[1]";
        ReferenceEquals(vm.FlagsView, viewInstance).Should().BeTrue();
        vm.FilteredFlagCount.Should().Be(1);
        vm.FlagsView.Cast<FastFlag>().Should().ContainSingle(f => f.Name == "FFlagTestRegexSpecial[1]");
    }

    [Fact]
    public void Phase3_FastFlagsAndWindowIntegrationsViewModels_ExposeDerivedAndStatusContracts()
    {
        var settings = new Settings { UseFastFlagManager = false, EnableActivityTracking = false, UseDiscordRichPresence = true };
        var ffManager = new FastFlagManager(Path.Combine(_tempDir, "ClientAppSettings.json"));
        var ffVm = new FastFlagsViewModel(settings, ffManager);

        ffVm.ApplyFastFlagPresets().Should().BeFalse();
        settings.UseFastFlagManager = true;
        ffVm.ApplyFastFlagPresets().Should().BeTrue();

        var wiVm = new WindowAndIntegrationsViewModel(settings, new PathProvider(_tempDir), (_, _) => { });
        wiVm.IsCustomRobloxIconSelected.Should().BeFalse();
        wiVm.SelectedRobloxIcon = RobloxIcon.IconCustom;
        wiVm.IsCustomRobloxIconSelected.Should().BeTrue();

        wiVm.IsDiscordRpcSubOptionsEnabled.Should().BeFalse();
        wiVm.EnableActivityTracking = true;
        wiVm.IsDiscordRpcSubOptionsEnabled.Should().BeTrue();
    }

    [Fact]
    public void Phase4_VersionInstallationValidator_AcceptsCustomDomainAndRejectsMissingContentFolderOrInvalidBaseUrl()
    {
        string versionDir = Path.Combine(_tempDir, "version-appset");
        CreateCompleteInstallForTest(versionDir);

        string appSettingsPath = Path.Combine(versionDir, "AppSettings.xml");
        File.WriteAllText(appSettingsPath, "<Settings><ContentFolder>content</ContentFolder><BaseUrl>https://roblox.example.com</BaseUrl></Settings>");
        VersionInstallationValidator.IsVersionInstallationComplete(versionDir, isStudio: false).Should().BeTrue();

        File.WriteAllText(appSettingsPath, "<Settings><BaseUrl>https://roblox.example.com</BaseUrl></Settings>");
        VersionInstallationValidator.IsVersionInstallationComplete(versionDir, isStudio: false).Should().BeFalse();

        File.WriteAllText(appSettingsPath, "<Settings><ContentFolder>content</ContentFolder><BaseUrl>not-a-valid-url</BaseUrl></Settings>");
        VersionInstallationValidator.IsVersionInstallationComplete(versionDir, isStudio: false).Should().BeFalse();
    }

    [Fact]
    public void Phase4_FastFlagManager_PreservesLeadingZeroString_DoesNotOverwriteAltEnter_AndUsesExactPresets()
    {
        string ffPath = Path.Combine(_tempDir, "ClientAppSettings.json");
        var manager = new FastFlagManager(ffPath);
        manager.ImportJson("""{"FStringCode":"00123","FFlagHandleAltEnterFullscreenManually":"True"}""");
        manager.Save();

        var reloaded = new FastFlagManager(ffPath);
        reloaded.Load();
        reloaded.GetFlag("FStringCode").Should().BeOfType<string>().And.Be("00123");
        reloaded.GetFlag("FFlagHandleAltEnterFullscreenManually")?.ToString().Should().Be("True");
        reloaded.Changed.Should().BeFalse();

        reloaded.ApplyPreset("FFlagCustomVulkanFix", "False");
        reloaded.GetFlag("FFlagCustomVulkanFix")?.ToString().Should().Be("False");
        reloaded.GetFlag("FFlagDebugGraphicsPreferVulkan").Should().BeNull();
    }

    [Fact]
    public async Task Phase4_ModsApplicationStep_DeletesStaleClientAppSettings_WhenFastFlagManagerDisabled()
    {
        var paths = new PathProvider(_tempDir);
        string versionGuid = "version-ff-disabled";
        string versionDir = paths.GetVersionDirectory(versionGuid);
        CreateCompleteInstallForTest(versionDir);

        string staleClientSettings = paths.GetClientSettingsPath(versionGuid);
        Directory.CreateDirectory(Path.GetDirectoryName(staleClientSettings)!);
        await File.WriteAllTextAsync(staleClientSettings, """{"FFlagDebugDisplayFPS":"True"}""");

        var settings = new Settings { UseFastFlagManager = false };
        var robloxStateMgr = new JsonManager<RobloxState>(paths.RobloxStateFile);
        var step = new ModsApplicationStep(paths, robloxStateMgr, Logger.Current);
        var context = new BootstrapPipelineContext(new LaunchSettings(), settings, isStudio: false)
        {
            ResolvedVersionGuid = versionGuid,
            TargetVersionDirectory = versionDir
        };

        await step.ExecuteAsync(context);

        File.Exists(staleClientSettings).Should().BeFalse();
    }

    [Fact]
    public void Phase4_GlobalSettingsManager_UpgradesLegacyQualityTagWithoutDuplicate_AndDeletesNewlyCreatedFileOnRestore()
    {
        string legacyXmlPath = Path.Combine(_tempDir, "GlobalBasicSettings_Legacy.xml");
        File.WriteAllText(
            legacyXmlPath,
            """
            <roblox version="4">
              <Item class="GlobalBasicSettings">
                <Properties>
                  <int name="SavedQualityLevel">5</int>
                </Properties>
              </Item>
            </roblox>
            """);

        var legacyMgr = new GlobalSettingsManager(legacyXmlPath);
        legacyMgr.Load();
        legacyMgr.SavedQualityLevel = 8;
        legacyMgr.Save();

        var doc = XDocument.Load(legacyXmlPath);
        doc.XPathSelectElements("//int[@name='SavedQualityLevel']").Should().BeEmpty();
        var tokens = doc.XPathSelectElements("//token[@name='SavedQualityLevel']").ToList();
        tokens.Should().ContainSingle();
        tokens[0].Value.Should().Be("8");

        string transientXmlPath = Path.Combine(_tempDir, "GlobalBasicSettings_Transient.xml");
        var transientMgr = new GlobalSettingsManager(transientXmlPath);
        GlobalSettingsBackup backup = transientMgr.CaptureBackupState();
        backup.Existed.Should().BeFalse();

        transientMgr.ApplyProfile(new RobloxClientSettingsProfile { FramerateCap = 120 });
        File.Exists(transientXmlPath).Should().BeTrue();

        transientMgr.RestoreBackupState(backup);
        File.Exists(transientXmlPath).Should().BeFalse();
    }

    [Fact]
    public void Phase5_ActivityWatcher_InGameTeleportWithDisconnectPreservationAndDirectTeleport_RaisesGameLeftBeforeGameJoined()
    {
        using var watcher = new ActivityWatcher(Logger.Current);
        var timeline = new List<string>();
        watcher.GameJoined += (_, e) => timeline.Add($"Joined:{e.JobId}");
        watcher.GameLeft += (_, _) => timeline.Add("Left");

        // Initial session
        watcher.ProcessLine("2026-11-22T01:00:00.000Z,0.0,1,6 [FLog::Output] ! Joining game 'job-1' place 100 at 128.116.1.1");
        watcher.ProcessLine("2026-11-22T01:00:01.000Z,0.0,1,6 [FLog::Network] serverId: 128.116.1.1|50000");

        // Teleport with disconnect line interleaved after new join line
        watcher.ProcessLine("2026-11-22T01:01:00.000Z,0.0,1,6 [FLog::UgcExperienceController] doTeleport: joinScriptUrl=https://assetgame.roblox.com/game/PlaceLauncher.ashx");
        watcher.ProcessLine("2026-11-22T01:01:01.000Z,0.0,1,6 [FLog::Output] ! Joining game 'job-2' place 200 at 128.116.1.2");
        watcher.ProcessLine("2026-11-22T01:01:02.000Z,0.0,1,6 [FLog::Network] Time to disconnect replication data:");
        watcher.ProcessLine("2026-11-22T01:01:03.000Z,0.0,1,6 [FLog::Network] serverId: 128.116.1.2|50001");

        watcher.InGame.Should().BeTrue();
        watcher.CurrentActivity!.JobId.Should().Be("job-2");
        watcher.CurrentActivity.IsTeleport.Should().BeTrue();

        // Direct teleport completion without disconnect line
        watcher.ProcessLine("2026-11-22T01:02:00.000Z,0.0,1,6 [FLog::Output] ! Joining game 'job-3' place 300 at 128.116.1.3");
        watcher.ProcessLine("2026-11-22T01:02:01.000Z,0.0,1,6 [FLog::Network] serverId: 128.116.1.3|50002");

        timeline.Should().Equal("Joined:job-1", "Left", "Joined:job-2", "Left", "Joined:job-3");
        watcher.History.Select(h => h.JobId).Should().Equal("job-2", "job-1");
        watcher.History.All(h => h.TimeLeft.HasValue).Should().BeTrue();
    }

    [Fact]
    public async Task Phase5_ActivityWatcher_LogFileSwitch_DrainsOldTailAndResetsSessionState_WithTransitiveComparator()
    {
        string logDir = Path.Combine(_tempDir, "roblox-logs");
        Directory.CreateDirectory(logDir);

        string log1 = Path.Combine(logDir, "0.650.0_RobloxPlayerBeta_1.log");
        File.WriteAllText(log1,
            "2026-11-22T02:00:00.000Z,0.0,1,6 [FLog::Output] ! Joining game 'job-old' place 555 at 128.116.2.1\r\n" +
            "2026-11-22T02:00:01.000Z,0.0,1,6 [FLog::Network] serverId: 128.116.2.1|51000\r\n");

        DateTime baseUtc = DateTime.UtcNow;
        File.SetCreationTimeUtc(log1, baseUtc.AddMinutes(-5));
        File.SetLastWriteTimeUtc(log1, baseUtc.AddMinutes(-2));

        using var watcher = new ActivityWatcher(Logger.Current, new ActivityWatcherOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(20),
            DirectoryScanWindow = TimeSpan.FromHours(1)
        });

        var joinedTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var leftTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.GameJoined += (_, e) => joinedTcs.TrySetResult(e.JobId);
        watcher.GameLeft += (_, _) => leftTcs.TrySetResult(true);

        watcher.StartWatching(logDir);
        (await joinedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be("job-old");

        // Append trailing pending join in log1 AND create log2 with newer LastWriteTimeUtc despite older CreationTimeUtc
        File.AppendAllText(log1, "2026-11-22T02:00:10.000Z,0.0,1,6 [FLog::Network] Time to disconnect replication data:\r\n");
        string log2 = Path.Combine(logDir, "0.650.0_RobloxPlayerBeta_2.log");
        File.WriteAllText(log2, string.Empty);
        File.SetCreationTimeUtc(log2, baseUtc.AddMinutes(-10));
        File.SetLastWriteTimeUtc(log2, DateTime.UtcNow.AddSeconds(2));

        (await leftTcs.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!string.Equals(watcher.CurrentLogFilePath, log2, StringComparison.OrdinalIgnoreCase) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        watcher.CurrentLogFilePath.Should().Be(log2);
        watcher.InGame.Should().BeFalse();
        watcher.CurrentActivity.Should().BeNull();
    }

    [Fact]
    public async Task Phase5_DiscordRpcService_ThrottledTrailingFlush_DeliversLatestAndCancelsOnClear()
    {
        string pipeName = $"test-throttle-flush-{Guid.NewGuid():N}";
        using var server = new MockDiscordIpcServer(pipeName);
        server.Start();

        using var service = new DiscordRpcService(new DiscordRpcOptions
        {
            PipeNameOverride = pipeName,
            AutoReconnect = false,
            UpdateThrottle = TimeSpan.FromMilliseconds(160)
        });

        static DiscordActivity MakeActivity(string details) =>
            new(details, "Playing", null, null, null, null, null, null, null);

        (await service.ConnectAsync()).Should().BeTrue();
        await service.UpdatePresenceAsync(MakeActivity("InitialPresence"));
        (await server.WaitForFrameAsync(f => f.Opcode == 1 && f.Json.Contains("InitialPresence"), TimeSpan.FromSeconds(2))).Should().BeTrue();

        // Coalesced throttled updates: IntermediateSkipped must be replaced by FinalCoalesced
        await service.UpdatePresenceAsync(MakeActivity("IntermediateSkipped"));
        await service.UpdatePresenceAsync(MakeActivity("FinalCoalesced"));

        (await server.WaitForFrameAsync(f => f.Opcode == 1 && f.Json.Contains("FinalCoalesced"), TimeSpan.FromSeconds(3))).Should().BeTrue();
        server.ReceivedFrames.Should().NotContain(f => f.Json.Contains("IntermediateSkipped"));

        // Cancelled by ClearPresenceAsync before throttle expires
        await service.UpdatePresenceAsync(MakeActivity("MustNotSendAfterClear"));
        await service.ClearPresenceAsync();
        await Task.Delay(250);

        server.ReceivedFrames.Should().NotContain(f => f.Json.Contains("MustNotSendAfterClear"));
    }

    [Fact]
    public void Phase5_WindowManipulationService_MinimizedBoundsGuard_EventShowIdempotency_AndDestroyPrunesCache()
    {
        var interop = new Phase5InteropStub();
        nint hwnd = 0x2222;
        interop.RegisterWindow(
            hwnd,
            "WINDOWSCLIENT",
            "Roblox",
            style: User32.WS_OVERLAPPEDWINDOW,
            exStyle: 0,
            bounds: new User32.RECT { Left = -32000, Top = -32000, Right = -31840, Bottom = -31970 },
            normalBounds: new User32.RECT { Left = 100, Top = 120, Right = 900, Bottom = 720 },
            isIconic: true);

        using var service = new WindowManipulationService(
            interop,
            new WindowManipulationOptions
            {
                BorderlessFullscreen = true,
                WindowPredicate = (_, _, _, _) => true
            },
            Logger.Current);

        // Initial EnumWindows scan during StartHook applies borderless once and saves rcNormalPosition (not -32000)
        service.StartHook();
        interop.SetWindowPosCalls.Should().Be(1);

        // Firing EVENT_OBJECT_SHOW for the same already-applied window should be idempotent
        service.HandleWinEvent(1, User32.EVENT_OBJECT_SHOW, hwnd, User32.OBJID_WINDOW, User32.CHILDID_SELF, 1, 1);
        interop.SetWindowPosCalls.Should().Be(1);

        // Restoring should restore the valid rcNormalPosition (100, 120, 800, 600), not -32000
        service.SetBorderlessFullscreen(hwnd, false);
        interop.LastMoveArgs.Should().Be((100, 120, 800, 600, false));

        // Non-iconic window whose GetWindowRect unexpectedly returns -32000 must not restore to -32000 either
        interop.SetIconic(hwnd, false);
        interop.SetBounds(hwnd, new User32.RECT { Left = -32000, Top = -32000, Right = -31840, Bottom = -31970 });
        service.SetBorderlessFullscreen(hwnd, true);
        service.SetBorderlessFullscreen(hwnd, false);
        interop.LastMoveArgs.NoMoveOrSize.Should().BeTrue();

        service.HandleWinEvent(1, User32.EVENT_OBJECT_DESTROY, hwnd, User32.OBJID_WINDOW, User32.CHILDID_SELF, 1, 1);
    }

    [Fact]
    public void Phase5_CacheCleaner_CleansTempRobloxAndStudioCache_AndExecuteCleanupDoesNotThrowOnUnsafePath()
    {
        var paths = new Phase5SandboxPaths(_tempDir);
        string tempHttp = Path.Combine(paths.Temp, "Roblox", "sounds", "stale.ogg");
        string studioLocalCache = Path.Combine(paths.Roblox, "StudioCache", "stale.bin");
        string studioTemp = Path.Combine(paths.Temp, "RobloxStudio", "stale_tmp.bin");
        string studioDirCache = Path.Combine(paths.RobloxStudio, "Cache", "stale_rblx.bin");

        foreach (string f in new[] { tempHttp, studioLocalCache, studioTemp, studioDirCache })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            File.WriteAllText(f, "stale");
            File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddDays(-10));
        }

        var settings = new Settings
        {
            CleanerOptions = CleanerOptions.OneDay,
            CleanerDirectories = ["RobloxCache", "RobloxStudioCache"]
        };
        var policy = CleanerPolicyMapper.FromSettings(settings);
        policy.CleanHttpCache.Should().BeTrue();
        policy.CleanStudioCache.Should().BeTrue();

        var cleaner = new CacheCleaner(paths, Logger.Current);
        var report = cleaner.ExecuteCleanup(policy);
        report.FilesDeleted.Should().Be(4);
        foreach (string f in new[] { tempHttp, studioLocalCache, studioTemp, studioDirCache })
        {
            File.Exists(f).Should().BeFalse();
        }

        // Unsafe directory in ExecuteCleanup records error without throwing, whereas CleanDirectories throws
        var unsafePaths = new Phase5SandboxPaths(_tempDir, overrideRobloxLogs: Path.GetTempPath());
        var unsafeCleaner = new CacheCleaner(unsafePaths, Logger.Current);
        var safeReport = unsafeCleaner.ExecuteCleanup(new CleanerPolicy(TimeSpan.FromDays(1), 0, CleanLogs: true, false, false, false));
        safeReport.Errors.Should().NotBeEmpty();

        Action strictAct = () => unsafeCleaner.CleanDirectories([Path.GetTempPath()], _tempDir, TimeSpan.FromDays(1));
        strictAct.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task Phase5_RoValraService_NegativeCachesFailedIpLookup_AndCachesDataCentersAndCapsRegions()
    {
        int ipInfoCalls = 0;
        int selfIpCalls = 0;
        int dataCenterCalls = 0;
        int regionCalls = 0;

        var handler = new MockHttpMessageHandler();
        handler.SetupRoute("ipinfo.io/198.51.100.10/json", () =>
        {
            Interlocked.Increment(ref ipInfoCalls);
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });
        handler.SetupRoute("apis.rovalra.com/v1/geolocation", () =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError));
        handler.SetupRoute("ipinfo.io/json", () =>
        {
            Interlocked.Increment(ref selfIpCalls);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"country":"US","loc":"37.77,-122.41"}""", Encoding.UTF8, "application/json")
            };
        });
        handler.SetupRoute("apis.rovalra.com/v1/datacenters/list", () =>
        {
            Interlocked.Increment(ref dataCenterCalls);
            var list = Enumerable.Range(0, 10)
                .Select(i => new { location = new { country = $"R{i}", latLong = new[] { 10.0 + i, 20.0 + i } } });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(list), Encoding.UTF8, "application/json")
            };
        });
        handler.SetupRoute("apis.rovalra.com/v1/servers/region", () =>
        {
            Interlocked.Increment(ref regionCalls);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"servers":[]}""", Encoding.UTF8, "application/json")
            };
        });

        using var http = handler.ToHttpClient();
        var rovalra = new RoValraService(http, logger: Logger.Current, clientLatitude: 37.77, clientLongitude: -122.41);

        // Two failed lookups only trigger 1 HTTP call (negative cached)
        (await rovalra.ResolveServerLocationAsync("198.51.100.10")).Should().BeNull();
        (await rovalra.ResolveServerLocationAsync("198.51.100.10")).Should().BeNull();
        ipInfoCalls.Should().Be(1);

        // Two matchmaking runs reuse cached self-IP + datacenters/list, and cap regions to 6 per call
        (await rovalra.GetBetterMatchmakingServerIdAsync(1818)).Should().BeNull();
        regionCalls.Should().Be(6);
        (await rovalra.GetBetterMatchmakingServerIdAsync(1818)).Should().BeNull();
        selfIpCalls.Should().Be(1);
        dataCenterCalls.Should().Be(1);
        regionCalls.Should().Be(12);
    }

    private static void CreateCompleteInstallForTest(string versionDir)
    {
        Directory.CreateDirectory(versionDir);
        File.WriteAllBytes(Path.Combine(versionDir, "RobloxPlayerBeta.exe"), [0x4D, 0x5A]);
        File.WriteAllBytes(Path.Combine(versionDir, "WebView2Loader.dll"), [0x01]);
        File.WriteAllText(Path.Combine(versionDir, "AppSettings.xml"), "<Settings><ContentFolder>content</ContentFolder><BaseUrl>http://www.roblox.com</BaseUrl></Settings>");
        Directory.CreateDirectory(Path.Combine(versionDir, "ssl"));
        File.WriteAllText(Path.Combine(versionDir, "ssl", "cacert.pem"), "cert");
        foreach (string dir in new[]
        {
            Path.Combine("PlatformContent", "pc", "textures"),
            Path.Combine("PlatformContent", "pc", "terrain"),
            Path.Combine("PlatformContent", "pc", "fonts"),
            Path.Combine("content", "configs"),
            Path.Combine("content", "fonts"),
            Path.Combine("content", "sky"),
            Path.Combine("content", "sounds"),
            Path.Combine("content", "textures"),
            Path.Combine("ExtraContent", "LuaPackages"),
            Path.Combine("ExtraContent", "translations"),
            "shaders"
        })
        {
            Directory.CreateDirectory(Path.Combine(versionDir, dir));
        }
    }

    private sealed class CountingCdnMirrorSelector(Action onResolve) : ICdnMirrorSelector
    {
        public Task<string> SelectFastestMirrorAsync(IEnumerable<string> mirrorUrls, HttpClient? client = null, TimeSpan? timeout = null, CancellationToken ct = default)
            => Task.FromResult("https://setup.rbxcdn.com");

        public Task<string> ResolveOptimalCdnUrlAsync(CancellationToken ct = default)
        {
            onResolve();
            return Task.FromResult("https://setup.rbxcdn.com");
        }
    }

    private sealed class RecordingPipelineStep(Action onExecute) : IBootstrapPipelineStep
    {
        public string StepName => "Recording";
        public Task ExecuteAsync(BootstrapPipelineContext context, CancellationToken ct = default)
        {
            onExecute();
            return Task.CompletedTask;
        }
    }

    private sealed class DeferCheckAuthService(List<string> events, bool throwOnTicket) : IRobloxAuthService
    {
        public Task<AuthenticatedUser> GetAuthenticatedUserAsync(string cookie, CancellationToken ct = default)
            => Task.FromResult(new AuthenticatedUser(42, "TestUser", "Test User"));

        public Task<string> GenerateAuthTicketAsync(string cookie, CancellationToken ct = default)
        {
            events.Add("AuthTicket");
            if (throwOnTicket)
            {
                throw new RobloxApiException(HttpStatusCode.Unauthorized, "Token expired");
            }
            return Task.FromResult("ticket-123");
        }

        public Task<RobloxUserPresence> GetUserPresenceAsync(string cookie, long userId, CancellationToken ct = default)
            => Task.FromResult(new RobloxUserPresence(userId, 0, null, null, null, null, null));

        public Task<ResolvedShareLink> ResolveShareLinkAsync(string cookie, string shareCode, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<long> ResolveUserIdByUsernameAsync(string username, CancellationToken ct = default)
            => Task.FromResult(42L);

        public Task<RobloxGameInfo?> GetGameInfoByPlaceIdAsync(long placeId, CancellationToken ct = default)
            => Task.FromResult<RobloxGameInfo?>(null);

        public Task<string?> GetAvatarThumbnailUrlAsync(long userId, string size = "150x150", bool isCircular = true, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public void Dispose() { }
    }

    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private sealed class Phase5InteropStub : IWin32WindowInterop
    {
        private sealed class WinData
        {
            public string ClassName { get; set; } = string.Empty;
            public string Title { get; set; } = string.Empty;
            public uint Style { get; set; }
            public uint ExStyle { get; set; }
            public User32.RECT Bounds { get; set; }
            public User32.RECT NormalBounds { get; set; }
            public bool IsIconic { get; set; }
        }

        private readonly ConcurrentDictionary<nint, WinData> _windows = new();

        public int SetWindowPosCalls { get; private set; }
        public (int Left, int Top, int Width, int Height, bool NoMoveOrSize) LastMoveArgs { get; private set; }

        public void RegisterWindow(
            nint hWnd,
            string className,
            string title,
            uint style,
            uint exStyle,
            User32.RECT bounds,
            User32.RECT normalBounds,
            bool isIconic)
        {
            _windows[hWnd] = new WinData
            {
                ClassName = className,
                Title = title,
                Style = style,
                ExStyle = exStyle,
                Bounds = bounds,
                NormalBounds = normalBounds,
                IsIconic = isIconic
            };
        }

        public void SetIconic(nint hWnd, bool isIconic)
        {
            if (_windows.TryGetValue(hWnd, out var w)) w.IsIconic = isIconic;
        }

        public void SetBounds(nint hWnd, User32.RECT bounds)
        {
            if (_windows.TryGetValue(hWnd, out var w)) w.Bounds = bounds;
        }

        public bool IsWindow(nint hWnd) => _windows.ContainsKey(hWnd);
        public bool IsWindowVisible(nint hWnd) => _windows.ContainsKey(hWnd);
        public uint GetWindowThreadProcessId(nint hWnd, out uint processId) { processId = 9999; return 1; }
        public string GetClassName(nint hWnd) => _windows.TryGetValue(hWnd, out var w) ? w.ClassName : string.Empty;
        public string GetWindowText(nint hWnd) => _windows.TryGetValue(hWnd, out var w) ? w.Title : string.Empty;
        public bool SetWindowText(nint hWnd, string text) { if (!_windows.TryGetValue(hWnd, out var w)) return false; w.Title = text; return true; }
        public nint GetWindowLongPtr(nint hWnd, int nIndex) => _windows.TryGetValue(hWnd, out var w)
            ? (nIndex == User32.GWL_EXSTYLE ? unchecked((nint)w.ExStyle) : unchecked((nint)w.Style))
            : nint.Zero;
        public nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong)
        {
            if (!_windows.TryGetValue(hWnd, out var w)) return nint.Zero;
            if (nIndex == User32.GWL_EXSTYLE) w.ExStyle = unchecked((uint)dwNewLong.ToInt64());
            else w.Style = unchecked((uint)dwNewLong.ToInt64());
            return nint.Zero;
        }
        public bool GetWindowRect(nint hWnd, out User32.RECT rect)
        {
            if (_windows.TryGetValue(hWnd, out var w)) { rect = w.Bounds; return true; }
            rect = default;
            return false;
        }
        public bool IsIconic(nint hWnd) => _windows.TryGetValue(hWnd, out var w) && w.IsIconic;
        public bool TryGetNormalWindowRect(nint hWnd, out User32.RECT rect)
        {
            if (_windows.TryGetValue(hWnd, out var w)) { rect = w.NormalBounds; return true; }
            rect = default;
            return false;
        }
        public bool EnumWindows(User32.EnumWindowsProc callback)
        {
            foreach (nint hWnd in _windows.Keys)
            {
                if (!callback(hWnd, nint.Zero)) break;
            }
            return true;
        }
        public User32.RECT GetMonitorRectFromWindow(nint hWnd) => new() { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
        public int GetSystemMetrics(int nIndex) => 1920;
        public bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags)
        {
            SetWindowPosCalls++;
            bool noMoveOrSize = (flags & User32.SWP_NOMOVE) != 0 && (flags & User32.SWP_NOSIZE) != 0;
            LastMoveArgs = (x, y, cx, cy, noMoveOrSize);
            return true;
        }
        public nint SendMessage(nint hWnd, uint msg, nint wParam, nint lParam) => nint.Zero;
        public nint LoadImageIcon(string filePath, int cx, int cy) => nint.Zero;
        public (nint SmallIcon, nint LargeIcon) ExtractFirstIconPair(string executablePath) => (nint.Zero, nint.Zero);
        public bool DestroyIcon(nint hIcon) => true;
        public nint SetWinEventHook(uint eventMin, uint eventMax, User32.WinEventDelegate callback, uint idProcess, uint idThread, uint flags) => 1;
        public bool UnhookWinEvent(nint hWinEventHook) => true;
        public uint GetCurrentThreadId() => 1;
        public bool PeekMessage(out User32.MSG msg, nint hWnd, uint filterMin, uint filterMax, uint removeMsg) { msg = default; return false; }
        public int GetMessage(out User32.MSG msg, nint hWnd, uint filterMin, uint filterMax) { msg = default; return 0; }
        public bool TranslateMessage(ref User32.MSG msg) => false;
        public nint DispatchMessage(ref User32.MSG msg) => nint.Zero;
        public bool PostThreadMessage(uint idThread, uint msg, nint wParam, nint lParam) => true;
    }

    private sealed class Phase5SandboxPaths : IPaths
    {
        private readonly string _root;
        private readonly string? _overrideRobloxLogs;

        public Phase5SandboxPaths(string root, string? overrideRobloxLogs = null)
        {
            _root = Path.Combine(root, "sandbox_" + Guid.NewGuid().ToString("N"));
            _overrideRobloxLogs = overrideRobloxLogs;
            Directory.CreateDirectory(Base);
            Directory.CreateDirectory(Temp);
            Directory.CreateDirectory(Roblox);
            Directory.CreateDirectory(RobloxStudio);
            Directory.CreateDirectory(RobloxCache);
        }

        public string Base => Path.Combine(_root, "App");
        public string Downloads => Path.Combine(Base, "Downloads");
        public string Logs => Path.Combine(Base, "Logs");
        public string Versions => Path.Combine(Base, "Versions");
        public string Modifications => Path.Combine(Base, "Modifications");
        public string CustomThemes => Path.Combine(Base, "CustomThemes");
        public string SavedFlagProfiles => Path.Combine(Base, "SavedFlagProfiles");
        public string Integrations => Path.Combine(Base, "Integrations");
        public string Application => Path.Combine(Base, "BaBaBooeyStrap.exe");
        public string AccountsFile => Path.Combine(Base, "accounts.dat");
        public string TargetsFile => Path.Combine(Base, "targets.json");
        public string SettingsFile => Path.Combine(Base, "Settings.json");
        public string StateFile => Path.Combine(Base, "State.json");
        public string RobloxStateFile => Path.Combine(Base, "RobloxState.json");
        public string Temp => Path.Combine(_root, "Temp");
        public string TempUpdates => Path.Combine(Temp, "BaBaBooeyStrap", "Updates");
        public string TempLogs => Path.Combine(Temp, "BaBaBooeyStrap", "Logs");
        public string UserProfile => _root;
        public string LocalAppData => _root;
        public string Desktop => Path.Combine(_root, "Desktop");
        public string WindowsStartMenu => Path.Combine(_root, "StartMenu");
        public string System => Environment.GetFolderPath(Environment.SpecialFolder.System);
        public string Process => string.Empty;
        public string Roblox => Path.Combine(_root, "Roblox");
        public string RobloxStudio => Path.Combine(_root, "Roblox Studio");
        public string RobloxLogs => _overrideRobloxLogs ?? Path.Combine(Roblox, "logs");
        public string RobloxVersions => Path.Combine(Roblox, "Versions");
        public string RobloxGlobalSettings => Path.Combine(Roblox, "GlobalBasicSettings_13.xml");
        public string RobloxLocalStorage => Path.Combine(Roblox, "LocalStorage");
        public string RobloxCache => Path.Combine(Temp, "Roblox");
        public bool Initialized => true;
        public void Initialize(string? baseDirectory = null) { }
        public void EnsureDirectoriesExist() { }
        public string GetVersionDirectory(string versionGuid) => Path.Combine(Versions, versionGuid);
        public string GetClientSettingsPath(string? versionGuid = null) =>
            string.IsNullOrEmpty(versionGuid)
                ? Path.Combine(Modifications, "ClientSettings", "ClientAppSettings.json")
                : Path.Combine(Versions, versionGuid, "ClientSettings", "ClientAppSettings.json");
    }
}
