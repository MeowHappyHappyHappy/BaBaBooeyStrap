using System.IO.Compression;
using BaBaBooeyStrap.App.DependencyInjection;
using BaBaBooeyStrap.App.Services;
using BaBaBooeyStrap.App.Services.Pipeline;
using BaBaBooeyStrap.Bootstrapper.Deployment.Abstractions;
using BaBaBooeyStrap.Core;
using BaBaBooeyStrap.Core.Logging;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Models.Enums;
using BaBaBooeyStrap.Core.Storage;
using BaBaBooeyStrap.MultiInstance.Abstractions;
using BaBaBooeyStrap.Tests.Mocks;

namespace BaBaBooeyStrap.Tests.App;

public sealed class BootstrapperPipelineAndDiTests : IDisposable
{
    private readonly string _tempDir;

    public BootstrapperPipelineAndDiTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"BBBS_PipelineDi_{Guid.NewGuid():N}");
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
    public void AppServiceConfiguration_CreateCoreServicesAndOrchestrator_WiresAllDependencies()
    {
        var paths = new PathProvider(_tempDir);
        using var services = AppServiceConfiguration.CreateCoreServices(paths, initializeGlobalEnvironment: false);

        services.Paths.Should().BeSameAs(paths);
        services.SettingsManager.Should().NotBeNull();
        services.StateManager.Should().NotBeNull();
        services.RobloxStateManager.Should().NotBeNull();
        services.AccountStore.Should().NotBeNull();
        services.JoinTargetStore.Should().NotBeNull();
        services.InstallerService.Should().NotBeNull();
        services.GlobalSettingsManager.Should().NotBeNull();
        services.HttpClient.Should().NotBeNull();

        var orchestrator = AppServiceConfiguration.CreateBootstrapperOrchestrator(services);
        orchestrator.Should().NotBeNull();
        services.Orchestrator.Should().BeSameAs(orchestrator);

        var vm = AppServiceConfiguration.CreateMainWindowViewModel(services);
        vm.Should().NotBeNull();
        vm.Settings.Should().BeSameAs(services.SettingsManager.Prop);
    }

    [Fact]
    public async Task BootstrapperOrchestrator_PrepareForBatchLaunchAsync_ExecutesPipelineStepsInOrder()
    {
        var paths = new PathProvider(_tempDir);
        var settingsMgr = new JsonManager<Settings>(paths.SettingsFile);
        var stateMgr = new JsonManager<State>(paths.StateFile);
        var robloxStateMgr = new JsonManager<RobloxState>(paths.RobloxStateFile);

        var executedOrder = new List<string>();
        IBootstrapPipelineStep[] customSteps =
        {
            new RecordingStep("Step1", executedOrder, ctx => ctx.ResolvedVersionGuid = "version-test-1"),
            new RecordingStep("Step2", executedOrder, ctx => ctx.TargetVersionDirectory = paths.GetVersionDirectory(ctx.ResolvedVersionGuid!)),
            new RecordingStep("Step3", executedOrder)
        };

        using var orchestrator = new BootstrapperOrchestrator(
            paths,
            settingsMgr,
            stateMgr,
            robloxStateMgr,
            multiInstanceController: new StubMultiInstanceController(),
            pipelineSteps: customSteps);

        await orchestrator.PrepareForBatchLaunchAsync();

        executedOrder.Should().Equal("Step1", "Step2", "Step3");
    }

    [Fact]
    public async Task VersionCheckStep_CheckForUpdatesDisabledAndCompleteInstall_SkipsDownloadWithoutHttpCalls()
    {
        var paths = new PathProvider(_tempDir);
        string versionGuid = "version-local-complete";
        string versionDir = paths.GetVersionDirectory(versionGuid);
        CreateCompletePlayerDirectory(versionDir);

        var settingsMgr = new JsonManager<Settings>(paths.SettingsFile);
        settingsMgr.Prop.CheckForUpdates = false;
        var stateMgr = new JsonManager<State>(paths.StateFile);
        var robloxStateMgr = new JsonManager<RobloxState>(paths.RobloxStateFile);
        robloxStateMgr.Prop.Player.VersionGuid = versionGuid;

        var handler = new MockHttpMessageHandler();
        using var httpClient = handler.ToHttpClient();
        var step = new VersionCheckStep(paths, settingsMgr, stateMgr, robloxStateMgr, httpClient, Logger.Current);

        var context = new BootstrapPipelineContext(new LaunchSettings(), settingsMgr.Prop, isStudio: false);
        await step.ExecuteAsync(context);

        context.ResolvedVersionGuid.Should().Be(versionGuid);
        context.RequiresDownload.Should().BeFalse();
        handler.RecordedRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task VersionCheckStep_WhenNewerVersionReturnedByApi_SetsRequiresDownloadAndMetadata()
    {
        var paths = new PathProvider(_tempDir);
        var settingsMgr = new JsonManager<Settings>(paths.SettingsFile);
        settingsMgr.Prop.CheckForUpdates = true;
        settingsMgr.Prop.Channel = "production";
        var stateMgr = new JsonManager<State>(paths.StateFile);
        var robloxStateMgr = new JsonManager<RobloxState>(paths.RobloxStateFile);
        robloxStateMgr.Prop.Player.VersionGuid = "version-old";

        var handler = new MockHttpMessageHandler();
        handler.SetupRoute("clientsettings.roblox.com/v2/client-version/WindowsPlayer", () =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"version":"0.650.0.1","clientVersionUpload":"version-new-999"}""",
                    Encoding.UTF8,
                    "application/json")
            });
        using var httpClient = handler.ToHttpClient();

        var mirrorSelector = new StubCdnMirrorSelector("https://setup.rbxcdn.com");
        var step = new VersionCheckStep(paths, settingsMgr, stateMgr, robloxStateMgr, httpClient, Logger.Current, mirrorSelector);

        var statusMessages = new List<string>();
        var context = new BootstrapPipelineContext(
            new LaunchSettings(),
            settingsMgr.Prop,
            isStudio: false,
            statusCallback: statusMessages.Add);

        await step.ExecuteAsync(context);

        context.RequiresDownload.Should().BeTrue();
        context.ExistingVersionGuid.Should().Be("version-old");
        context.ResolvedVersionGuid.Should().Be("version-new-999");
        context.LatestVersion.Should().Be("0.650.0.1");
        context.CdnBaseUrl.Should().Be("https://setup.rbxcdn.com");
        statusMessages.Should().NotBeEmpty();
    }

    [Fact]
    public async Task PackageDeploymentStep_WhenSkippedDownload_RepairsAppSettingsAndWebView2Loader()
    {
        var paths = new PathProvider(_tempDir);
        Directory.CreateDirectory(paths.Downloads);
        string versionGuid = "version-repair-test";
        string versionDir = paths.GetVersionDirectory(versionGuid);
        Directory.CreateDirectory(versionDir);
        File.WriteAllBytes(Path.Combine(versionDir, "RobloxPlayerBeta.exe"), [0x4D, 0x5A]);

        // Create cached Downloads/WebView2.zip containing WebView2Loader.dll
        string webViewZip = Path.Combine(paths.Downloads, "WebView2.zip");
        using (var archive = ZipFile.Open(webViewZip, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("WebView2Loader.dll");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("dummy-dll");
        }

        var settingsMgr = new JsonManager<Settings>(paths.SettingsFile);
        var stateMgr = new JsonManager<State>(paths.StateFile);
        var robloxStateMgr = new JsonManager<RobloxState>(paths.RobloxStateFile);
        using var httpClient = new HttpClient();

        var step = new PackageDeploymentStep(paths, stateMgr, robloxStateMgr, httpClient, Logger.Current);
        var context = new BootstrapPipelineContext(new LaunchSettings(), settingsMgr.Prop, isStudio: false)
        {
            ResolvedVersionGuid = versionGuid,
            RequiresDownload = false
        };

        await step.ExecuteAsync(context);

        context.TargetVersionDirectory.Should().Be(versionDir);
        File.Exists(Path.Combine(versionDir, "AppSettings.xml")).Should().BeTrue();
        File.Exists(Path.Combine(versionDir, "WebView2Loader.dll")).Should().BeTrue();
    }

    [Fact]
    public async Task ModsApplicationStep_CopiesClientAppSettingsWithoutBomAndAppliesModOverlay()
    {
        var paths = new PathProvider(_tempDir);
        string versionGuid = "version-mods-test";
        string versionDir = paths.GetVersionDirectory(versionGuid);
        Directory.CreateDirectory(versionDir);

        string sourceClientSettings = paths.GetClientSettingsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(sourceClientSettings)!);
        await File.WriteAllTextAsync(
            sourceClientSettings,
            """{"FFlagDebugDisplayFPS":"True"}""",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        string modFont = Path.Combine(paths.Modifications, "content", "fonts", "CustomFont.ttf");
        Directory.CreateDirectory(Path.GetDirectoryName(modFont)!);
        await File.WriteAllBytesAsync(modFont, [0x00, 0x01, 0x00, 0x00]);

        var settings = new Settings { UseFastFlagManager = true };
        var robloxStateMgr = new JsonManager<RobloxState>(paths.RobloxStateFile);
        var step = new ModsApplicationStep(paths, robloxStateMgr, Logger.Current);

        var context = new BootstrapPipelineContext(new LaunchSettings(), settings, isStudio: false)
        {
            TargetVersionDirectory = versionDir
        };

        await step.ExecuteAsync(context);

        string versionClientSettings = paths.GetClientSettingsPath(versionGuid);
        File.Exists(versionClientSettings).Should().BeTrue();
        byte[] bytes = await File.ReadAllBytesAsync(versionClientSettings);
        // Ensure no UTF-8 BOM
        bytes.Take(3).Should().NotEqual(new byte[] { 0xEF, 0xBB, 0xBF });

        File.Exists(Path.Combine(versionDir, "content", "fonts", "CustomFont.ttf")).Should().BeTrue();
        robloxStateMgr.Prop.ModManifest.Should().Contain(p => p.Contains("CustomFont.ttf", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PreLaunchCleanupStep_RespectsCleanerPolicy()
    {
        var paths = new PathProvider(_tempDir);
        Directory.CreateDirectory(paths.Logs);
        string staleLog = Path.Combine(paths.Logs, "stale_test.log");
        await File.WriteAllTextAsync(staleLog, "old log");
        File.SetLastWriteTimeUtc(staleLog, DateTime.UtcNow.AddDays(-30));

        var step = new PreLaunchCleanupStep(paths, Logger.Current);

        // CleanerOptions.Never leaves file intact
        var neverSettings = new Settings
        {
            CleanerOptions = CleanerOptions.Never,
            CleanerDirectories = ["BaBaBooeyStrapLogs"]
        };
        await step.ExecuteAsync(new BootstrapPipelineContext(new LaunchSettings(), neverSettings, isStudio: false));
        File.Exists(staleLog).Should().BeTrue();

        // CleanerOptions.OneDay deletes 30-day-old file
        var cleanSettings = new Settings
        {
            CleanerOptions = CleanerOptions.OneDay,
            CleanerDirectories = ["BaBaBooeyStrapLogs"]
        };
        await step.ExecuteAsync(new BootstrapPipelineContext(new LaunchSettings(), cleanSettings, isStudio: false));
        File.Exists(staleLog).Should().BeFalse();
    }

    private static void CreateCompletePlayerDirectory(string versionDir)
    {
        Directory.CreateDirectory(versionDir);
        File.WriteAllBytes(Path.Combine(versionDir, "RobloxPlayerBeta.exe"), [0x4D, 0x5A]);
        File.WriteAllBytes(Path.Combine(versionDir, "WebView2Loader.dll"), [0x4D, 0x5A]);
        File.WriteAllText(Path.Combine(versionDir, "AppSettings.xml"), "<Settings><BaseUrl>http://www.roblox.com</BaseUrl></Settings>");
        Directory.CreateDirectory(Path.Combine(versionDir, "ssl"));
        File.WriteAllText(Path.Combine(versionDir, "ssl", "cacert.pem"), "cert");
        Directory.CreateDirectory(Path.Combine(versionDir, "PlatformContent", "pc", "textures"));
        Directory.CreateDirectory(Path.Combine(versionDir, "PlatformContent", "pc", "terrain"));
        Directory.CreateDirectory(Path.Combine(versionDir, "PlatformContent", "pc", "fonts"));
        Directory.CreateDirectory(Path.Combine(versionDir, "content", "configs"));
        Directory.CreateDirectory(Path.Combine(versionDir, "content", "fonts"));
        Directory.CreateDirectory(Path.Combine(versionDir, "content", "sky"));
        Directory.CreateDirectory(Path.Combine(versionDir, "content", "sounds"));
        Directory.CreateDirectory(Path.Combine(versionDir, "content", "textures"));
        Directory.CreateDirectory(Path.Combine(versionDir, "ExtraContent", "LuaPackages"));
        Directory.CreateDirectory(Path.Combine(versionDir, "ExtraContent", "translations"));
        Directory.CreateDirectory(Path.Combine(versionDir, "shaders"));
    }

    private sealed class RecordingStep : IBootstrapPipelineStep
    {
        private readonly List<string> _log;
        private readonly Action<BootstrapPipelineContext>? _action;

        public string StepName { get; }

        public RecordingStep(string stepName, List<string> log, Action<BootstrapPipelineContext>? action = null)
        {
            StepName = stepName;
            _log = log;
            _action = action;
        }

        public Task ExecuteAsync(BootstrapPipelineContext context, CancellationToken ct = default)
        {
            _log.Add(StepName);
            _action?.Invoke(context);
            return Task.CompletedTask;
        }
    }

    private sealed class StubCdnMirrorSelector : ICdnMirrorSelector
    {
        private readonly string _cdn;

        public StubCdnMirrorSelector(string cdn) => _cdn = cdn;

        public Task<string> SelectFastestMirrorAsync(IEnumerable<string> mirrorUrls, HttpClient? client = null, TimeSpan? timeout = null, CancellationToken ct = default) =>
            Task.FromResult(_cdn);

        public Task<string> ResolveOptimalCdnUrlAsync(CancellationToken ct = default) =>
            Task.FromResult(_cdn);
    }

    private sealed class StubMultiInstanceController : IMultiInstanceController
    {
        public MultiInstanceStatus Enable() => new(true, true, false, [], null);
        public MultiInstanceStatus Disable() => new(false, false, false, [], null);
        public MultiInstanceStatus GetStatus() => new(false, false, false, [], null);
        public void Dispose() { }
    }
}
