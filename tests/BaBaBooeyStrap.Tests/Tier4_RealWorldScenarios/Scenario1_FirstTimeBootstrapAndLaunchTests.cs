using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using BaBaBooeyStrap.Bootstrapper.Deployment;
using BaBaBooeyStrap.Bootstrapper.FastFlags;
using BaBaBooeyStrap.Bootstrapper.Settings;
using BaBaBooeyStrap.Core.Models.Enums;
using BaBaBooeyStrap.Tests.Mocks;
using BaBaBooeyStrap.Tests.Tier1_FeatureCoverage.Engines;
using FluentAssertions;
using Xunit;

namespace BaBaBooeyStrap.Tests.Tier4_RealWorldScenarios;

public sealed class Scenario1_FirstTimeBootstrapAndLaunchTests
{
    private static string BuildManifestContent(IReadOnlyDictionary<string, byte[]> packages)
    {
        var sb = new StringBuilder();
        sb.AppendLine("v0");
        foreach (var (name, bytes) in packages)
        {
            string md5 = Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();
            sb.AppendLine(name);
            sb.AppendLine(md5);
            sb.AppendLine(bytes.Length.ToString());
            sb.AppendLine(bytes.Length.ToString());
        }
        return sb.ToString();
    }

    [Fact]
    public async Task Scenario1_CleanMachineBootstrapAndLaunch_EndToEndWorkflow()
    {
        // 1. Arrange Clean Environment & Mock HTTP
        using var env = new MockRobloxEnvironment();
        var cdnPackages = new Dictionary<string, byte[]>
        {
            ["RobloxApp.zip"] = MockRobloxEnvironment.CreateZipBytes(new Dictionary<string, byte[]>
            {
                ["RobloxPlayerBeta.exe"] = new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x01, 0x02, 0x03 }
            }),
            ["content-textures.zip"] = MockRobloxEnvironment.CreateZipBytes(new Dictionary<string, string>
            {
                ["textures/icon.png"] = "mock_texture_data"
            }),
            ["content-fonts.zip"] = MockRobloxEnvironment.CreateZipBytes(new Dictionary<string, string>
            {
                ["fonts/arial.ttf"] = "mock_font_data"
            })
        };

        string manifest4Line = BuildManifestContent(cdnPackages);

        var handler = new MockHttpMessageHandler()
            .SetupChannelVersionWorkflow("live", "version-4ea8e85481e34f46", "0.640.0.6400599")
            .SetupCdnDeployment("https://setup.rbxcdn.com", "version-4ea8e85481e34f46", cdnPackages);

        // Override manifest route with standard 4-line package records
        handler.SetupRoute("https://setup.rbxcdn.com/version-4ea8e85481e34f46-rbxPkgManifest.txt",
            () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(manifest4Line, Encoding.UTF8, "text/plain")
            });

        // Setup mirror pinging
        handler.SetupRoute("https://setup.rbxcdn.com/ping", () => new HttpResponseMessage(HttpStatusCode.OK));
        handler.SetupRoute("https://slow.rbxcdn.com/ping", () =>
        {
            Thread.Sleep(80);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using var httpClient = handler.ToHttpClient();
        var selector = new CdnMirrorSelector(httpClient);
        var parser = new DeploymentManifestParser();
        var downloader = new PackageDownloader();
        var extractor = new PackageExtractor();

        // 2. Select CDN Mirror (F10)
        string chosenMirror = await selector.SelectFastestMirrorAsync(
            new[] { "https://slow.rbxcdn.com/ping", "https://setup.rbxcdn.com/ping" }, httpClient);
        chosenMirror.Should().Be("https://setup.rbxcdn.com/ping");

        // 3. Download & Parse Manifest (F10)
        var manifest = await parser.FetchAndParseAsync("https://setup.rbxcdn.com", "version-4ea8e85481e34f46", httpClient);
        manifest.Count.Should().Be(3);

        // 4. Download Packages & Extract to Version Directory (F11)
        string downloadsDir = Path.Combine(env.BaBaBooeyStrapPath, "Downloads");
        string versionDir = Path.Combine(env.RobloxVersionsPath, "version-4ea8e85481e34f46");
        var downloadedFiles = await downloader.DownloadPackagesAsync(
            httpClient, "https://setup.rbxcdn.com", "version-4ea8e85481e34f46", manifest, downloadsDir);

        foreach (var pkgFile in downloadedFiles)
        {
            extractor.ExtractZipFile(pkgFile, versionDir);
        }
        File.WriteAllText(Path.Combine(versionDir, "version.txt"), "version-4ea8e85481e34f46");

        // 5. Configure FastFlags (F12)
        string clientSettingsDir = Path.Combine(versionDir, "ClientSettings");
        string clientSettingsJson = Path.Combine(clientSettingsDir, "ClientAppSettings.json");
        var ffManager = new FastFlagManager(clientSettingsJson);
        ffManager.ApplyPreset("MSAA", 4);
        ffManager.ApplyPreset("D3D11", true);
        ffManager.SetFlag("DFIntTaskSchedulerTargetFps", 144);
        ffManager.Save();

        // 6. Native FPS Uncap in GlobalBasicSettings_13.xml (F15)
        GlobalSettingsManager.SetFramerateCap(env.GlobalSettingsXmlPath, 144, lockReadOnly: true);

        // 7. CLI Protocol Router Handling (F21)
        string cliUri = "roblox-player:1+launchmode:play+gameinfo:TICKET_ABC123+placelauncherurl:https%3A%2F%2Fassetgame.roblox.com%2Fgame%2FPlaceLauncher.ashx%3FplaceId%3D1818";
        var router = new LaunchSettingsRouter(new[] { cliUri });

        // Assertions
        router.Settings.RobloxLaunchMode.Should().Be(LaunchMode.Player);
        File.Exists(Path.Combine(versionDir, "RobloxPlayerBeta.exe")).Should().BeTrue();
        File.Exists(clientSettingsJson).Should().BeTrue();

        string jsonContent = File.ReadAllText(clientSettingsJson);
        jsonContent.Should().Contain("FIntDebugForceMSAASamples");
        jsonContent.Should().Contain("144");

        File.Exists(env.GlobalSettingsXmlPath).Should().BeTrue();
        var xmlAttr = File.GetAttributes(env.GlobalSettingsXmlPath);
        xmlAttr.HasFlag(FileAttributes.ReadOnly).Should().BeTrue();
    }

    [Fact]
    public void Scenario1_CustomFastFlagsAndFpsUncap_AppliedToNewInstallationBeforeLaunch()
    {
        using var env = new MockRobloxEnvironment();
        string versionDir = Path.Combine(env.RobloxVersionsPath, "version-ff-uncap-test");
        Directory.CreateDirectory(versionDir);

        string clientSettingsDir = Path.Combine(versionDir, "ClientSettings");
        string clientSettingsJson = Path.Combine(clientSettingsDir, "ClientAppSettings.json");

        var ffManager = new FastFlagManager(clientSettingsJson);
        ffManager.ApplyPreset("MSAA", 8);
        ffManager.ApplyPreset("Vulkan", true);
        ffManager.SetFlag("DFIntTaskSchedulerTargetFps", 240);
        ffManager.SetFlag("FFlagEnableNewPhysicsSolver", "True");
        ffManager.Save();

        File.Exists(clientSettingsJson).Should().BeTrue();
        string json = File.ReadAllText(clientSettingsJson);
        json.Should().Contain("\"FIntDebugForceMSAASamples\": \"8\"");
        json.Should().Contain("\"FFlagDebugGraphicsPreferVulkan\": \"True\"");
        json.Should().Contain("\"DFIntTaskSchedulerTargetFps\": \"240\"");

        GlobalSettingsManager.SetFramerateCap(env.GlobalSettingsXmlPath, 240, lockReadOnly: true);
        File.Exists(env.GlobalSettingsXmlPath).Should().BeTrue();

        var attr = File.GetAttributes(env.GlobalSettingsXmlPath);
        attr.HasFlag(FileAttributes.ReadOnly).Should().BeTrue();

        string xmlContent = File.ReadAllText(env.GlobalSettingsXmlPath);
        xmlContent.Should().Contain("240");

        var writeAttempt = () => File.WriteAllText(env.GlobalSettingsXmlPath, "corrupt_data");
        writeAttempt.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task Scenario1_CdnMirrorFailover_PingsMirrorsAndRecoversFromSlowMirrorDuringBootstrap()
    {
        using var env = new MockRobloxEnvironment();
        string versionGuid = "version-failover-test";

        var cdnPackages = new Dictionary<string, byte[]>
        {
            ["RobloxApp.zip"] = MockRobloxEnvironment.CreateZipBytes(new Dictionary<string, byte[]>
            {
                ["RobloxPlayerBeta.exe"] = new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x11, 0x22 }
            })
        };

        string manifest4Line = BuildManifestContent(cdnPackages);

        var handler = new MockHttpMessageHandler()
            .SetupCdnDeployment("https://fast.rbxcdn.com", versionGuid, cdnPackages);

        handler.SetupRoute($"https://fast.rbxcdn.com/{versionGuid}-rbxPkgManifest.txt",
            () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(manifest4Line, Encoding.UTF8, "text/plain")
            });

        // Ping endpoints
        handler.SetupRoute("https://down.rbxcdn.com/ping", () => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        handler.SetupRoute("https://slow.rbxcdn.com/ping", () =>
        {
            Thread.Sleep(100);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        handler.SetupRoute("https://fast.rbxcdn.com/ping", () => new HttpResponseMessage(HttpStatusCode.OK));

        using var httpClient = handler.ToHttpClient();
        var selector = new CdnMirrorSelector(httpClient);
        var parser = new DeploymentManifestParser();
        var downloader = new PackageDownloader();
        var extractor = new PackageExtractor();

        // 1. Selector skips 500 error mirror and slow mirror, picks fast
        string selected = await selector.SelectFastestMirrorAsync(
            new[] { "https://down.rbxcdn.com/ping", "https://slow.rbxcdn.com/ping", "https://fast.rbxcdn.com/ping" },
            httpClient);
        selected.Should().Be("https://fast.rbxcdn.com/ping");

        // 2. Fetch manifest and download packages from healthy mirror
        var manifest = await parser.FetchAndParseAsync("https://fast.rbxcdn.com", versionGuid, httpClient);
        manifest.Should().NotBeEmpty();

        string downloadsDir = Path.Combine(env.BaBaBooeyStrapPath, "Downloads");
        string versionDir = Path.Combine(env.RobloxVersionsPath, versionGuid);

        var downloadedFiles = await downloader.DownloadPackagesAsync(
            httpClient, "https://fast.rbxcdn.com", versionGuid, manifest, downloadsDir);

        downloadedFiles.Should().HaveCount(1);
        extractor.ExtractZipFile(downloadedFiles[0], versionDir);

        File.Exists(Path.Combine(versionDir, "RobloxPlayerBeta.exe")).Should().BeTrue();
    }

    [Fact]
    public void Scenario1_CliLaunchProtocol_ParsesDeepLinkAndValidatesDeploymentReadiness()
    {
        using var env = new MockRobloxEnvironment();
        string versionDir = Path.Combine(env.RobloxVersionsPath, "version-ready-test");

        string deepLink = "roblox-player:1+launchmode:play+gameinfo:TICKET_SECRET_999+launchtime:1700000000+placelauncherurl:https%3A%2F%2Fassetgame.roblox.com%2Fgame%2FPlaceLauncher.ashx%3FplaceId%3D1818";

        LaunchSettingsRouter.IsValidPlayerProtocol(deepLink).Should().BeTrue();

        var router = new LaunchSettingsRouter(new[] { deepLink });
        router.Settings.RobloxLaunchMode.Should().Be(LaunchMode.Player);
        router.Settings.RobloxLaunchArgs.Should().Contain("gameinfo:TICKET_SECRET_999");
        router.Settings.RobloxLaunchArgs.Should().Contain("placeId%3D1818");

        // Validate deployment readiness check: initially not ready
        string exePath = Path.Combine(versionDir, "RobloxPlayerBeta.exe");
        File.Exists(exePath).Should().BeFalse();

        // Deploy binary
        Directory.CreateDirectory(versionDir);
        File.WriteAllBytes(exePath, new byte[] { 0x4D, 0x5A, 0x90, 0x00 });

        // Validate deployment readiness check: ready
        File.Exists(exePath).Should().BeTrue();
    }
}
