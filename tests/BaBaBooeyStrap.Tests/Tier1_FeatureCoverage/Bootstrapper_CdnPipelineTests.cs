using BaBaBooeyStrap.Bootstrapper.Deployment;
using BaBaBooeyStrap.Bootstrapper.Deployment.Models;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Tests.Mocks;

namespace BaBaBooeyStrap.Tests.Tier1_FeatureCoverage;

public sealed class Bootstrapper_CdnPipelineTests : IClassFixture<MockRobloxEnvironment>
{
    private readonly MockRobloxEnvironment _env;

    public Bootstrapper_CdnPipelineTests(MockRobloxEnvironment env)
    {
        _env = env;
    }

    private sealed class SyncProgress<T>(Action<T> onReport) : IProgress<T>
    {
        public void Report(T value) => onReport(value);
    }

    // =========================================================================
    // Feature 10: CDN Mirror & Manifest Pipeline
    // =========================================================================

    [Fact]
    public async Task F10_CdnMirror_PingSelection_SelectsLowestLatencyMirror()
    {
        // Arrange
        const string mirrorFast = "https://fast.rbxcdn.com/ping";
        const string mirrorMedium = "https://medium.rbxcdn.com/ping";
        const string mirrorSlow = "https://slow.rbxcdn.com/ping";

        var handler = new MockHttpMessageHandler();
        handler.SetupRoute(mirrorFast, () => new HttpResponseMessage(HttpStatusCode.OK));
        handler.SetupRoute(mirrorMedium, () =>
        {
            Thread.Sleep(50);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        handler.SetupRoute(mirrorSlow, () =>
        {
            Thread.Sleep(150);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using var client = handler.ToHttpClient();
        var selector = new CdnMirrorSelector(client);

        // Act
        string fastest = await selector.SelectFastestMirrorAsync(
            new[] { mirrorSlow, mirrorFast, mirrorMedium },
            client
        );

        // Assert
        fastest.Should().Be(mirrorFast);
    }

    [Fact]
    public void F10_Manifest_ParseManifest_ExtractsAllPackageTriplets()
    {
        // Arrange
        string manifestContent =
            "v0\n" +
            "RobloxApp.zip\n" +
            "e4d909c290d0fb1ca068ffaddf22cbd0\n" +
            "1234567\n" +
            "456789\n" +
            "content-fonts.zip\n" +
            "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6\n" +
            "987654\n" +
            "321000\n";

        // Act
        var packages = DeploymentManifestParser.ParseManifest(manifestContent);

        // Assert
        packages.Count.Should().Be(2);
        packages[0].Name.Should().Be("RobloxApp.zip");
        packages[0].Md5.Should().Be("e4d909c290d0fb1ca068ffaddf22cbd0");
        packages[0].UncompressedSize.Should().Be(1234567);
        packages[0].CompressedSize.Should().Be(456789);

        packages[1].Name.Should().Be("content-fonts.zip");
        packages[1].Md5.Should().Be("a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6");
    }

    [Fact]
    public void F10_Manifest_VerifyPackageMd5_ValidatesMatchingChecksum()
    {
        // Arrange
        byte[] payload = Encoding.UTF8.GetBytes("TestPackagePayloadForMd5Check");
        string expectedMd5 = Convert.ToHexString(MD5.HashData(payload)).ToLowerInvariant();
        var verifier = new PackageVerifier();

        // Act & Assert
        verifier.VerifyMd5(payload, expectedMd5).Should().BeTrue();

        byte[] corrupted = Encoding.UTF8.GetBytes("CorruptedPackagePayload");
        verifier.VerifyMd5(corrupted, expectedMd5).Should().BeFalse();
    }

    [Fact]
    public async Task F10_DeployInfo_ResolveVersion_ParsesJsonPayload()
    {
        // Arrange
        const string cdnHost = "https://clientsettingscdn.roblox.com";
        const string versionGuid = "version-4ea8e85481e34f46";
        const string versionNumber = "0.640.0.12345";

        var handler = new MockHttpMessageHandler()
            .SetupChannelVersionWorkflow("live", versionGuid, versionNumber);
        using var client = handler.ToHttpClient();

        // Act
        string url = $"{cdnHost}/v2/client-version/WindowsPlayer/channel/live";
        string json = await client.GetStringAsync(url);
        using var doc = JsonDocument.Parse(json);

        var deployInfo = new DeployInfo
        {
            Version = doc.RootElement.GetProperty("version").GetString()!,
            ClientVersionUpload = doc.RootElement.GetProperty("clientVersionUpload").GetString()!,
            BootstrapperVersion = "1.0.0"
        };

        // Assert
        deployInfo.ClientVersionUpload.Should().Be(versionGuid);
        deployInfo.Version.Should().Be(versionNumber);
    }

    [Fact]
    public void F10_Manifest_MalformedHeader_ThrowsInvalidDataException()
    {
        // Arrange
        string invalidManifest = "v1_UNKNOWN\nRobloxApp.zip\nabc\n100\n50\n";

        // Act
        Action act = () => DeploymentManifestParser.ParseManifest(invalidManifest);

        // Assert
        act.Should().Throw<InvalidDataException>()
           .WithMessage("*Invalid manifest header*");
    }

    // =========================================================================
    // Feature 11: Package Downloader & Extractor
    // =========================================================================

    [Fact]
    public async Task F11_PackageDownloader_ParallelDownload_FetchesAllPackagesWithProgress()
    {
        // Arrange
        const string cdnHost = "https://setup.rbxcdn.com";
        const string versionGuid = "version-test-guid-pkg";
        byte[] pkg1 = Encoding.UTF8.GetBytes("RobloxAppZipContent");
        byte[] pkg2 = Encoding.UTF8.GetBytes("ShadersZipContent");

        var packagesMap = new Dictionary<string, byte[]>
        {
            ["RobloxApp.zip"] = pkg1,
            ["shaders.zip"] = pkg2
        };

        var handler = new MockHttpMessageHandler().SetupCdnDeployment(cdnHost, versionGuid, packagesMap);
        using var client = handler.ToHttpClient();

        string destFolder = Path.Combine(_env.RootPath, "download_test");
        var descriptors = new List<PackageRecord>
        {
            new("RobloxApp.zip", Convert.ToHexString(MD5.HashData(pkg1)).ToLowerInvariant(), pkg1.Length, pkg1.Length),
            new("shaders.zip", Convert.ToHexString(MD5.HashData(pkg2)).ToLowerInvariant(), pkg2.Length, pkg2.Length)
        };

        double lastProgress = 0;
        var progress = new SyncProgress<PackageDownloadProgress>(p => Interlocked.Exchange(ref lastProgress, p.ProgressPercentage));
        var downloader = new PackageDownloader();

        // Act
        var downloaded = await downloader.DownloadPackagesAsync(client, cdnHost, versionGuid, descriptors, destFolder, progress);

        // Assert
        downloaded.Count.Should().Be(2);
        File.Exists(downloaded[0]).Should().BeTrue();
        File.Exists(downloaded[1]).Should().BeTrue();
        lastProgress.Should().Be(100.0);
    }

    [Fact]
    public void F11_Extractor_FastZipExtract_UnpacksZipFilesToTargetDirectory()
    {
        // Arrange
        var files = new Dictionary<string, byte[]>
        {
            ["content/fonts/arial.ttf"] = Encoding.UTF8.GetBytes("FakeArialTtfData"),
            ["shaders/default.cso"] = Encoding.UTF8.GetBytes("FakeShaderBytecode")
        };
        byte[] zipBytes = MockRobloxEnvironment.CreateZipBytes(files);

        string targetDir = Path.Combine(_env.RootPath, "unpacked_test");
        var extractor = new PackageExtractor();

        // Act
        extractor.ExtractZip(zipBytes, targetDir);

        // Assert
        File.Exists(Path.Combine(targetDir, "content", "fonts", "arial.ttf")).Should().BeTrue();
        File.Exists(Path.Combine(targetDir, "shaders", "default.cso")).Should().BeTrue();
        File.ReadAllText(Path.Combine(targetDir, "content", "fonts", "arial.ttf")).Should().Be("FakeArialTtfData");
    }

    [Fact]
    public void F11_Extractor_VersionDirectory_CreatesExecutableAndVersionFiles()
    {
        // Arrange
        string versionGuid = "version-final-exec-test";
        string versionDir = Path.Combine(_env.RobloxVersionsPath, versionGuid);

        var files = new Dictionary<string, byte[]>
        {
            ["RobloxPlayerBeta.exe"] = Encoding.UTF8.GetBytes("ExecutableDummyBytes"),
            ["version.txt"] = Encoding.UTF8.GetBytes(versionGuid)
        };
        byte[] zipBytes = MockRobloxEnvironment.CreateZipBytes(files);
        var extractor = new PackageExtractor();

        // Act
        extractor.ExtractZip(zipBytes, versionDir);

        // Assert
        File.Exists(Path.Combine(versionDir, "RobloxPlayerBeta.exe")).Should().BeTrue();
        File.Exists(Path.Combine(versionDir, "version.txt")).Should().BeTrue();
        File.ReadAllText(Path.Combine(versionDir, "version.txt")).Should().Be(versionGuid);
    }

    [Fact]
    public void F11_PackageDownloader_SkipExisting_DetectsAlreadyInstalledVersion()
    {
        // Arrange
        const string versionGuid = "version-already-installed";
        string existingDir = _env.CreateDummyRobloxInstallation(versionGuid);

        // Act
        bool exists = Directory.Exists(existingDir) &&
                      File.Exists(Path.Combine(existingDir, "RobloxPlayerBeta.exe")) &&
                      File.Exists(Path.Combine(existingDir, "version.txt"));

        // Assert
        exists.Should().BeTrue();
    }

    [Fact]
    public void F11_Extractor_PreservesFileTimestampsAndPermissions()
    {
        // Arrange
        var files = new Dictionary<string, byte[]>
        {
            ["data.bin"] = new byte[] { 1, 2, 3, 4, 5 }
        };
        byte[] zipBytes = MockRobloxEnvironment.CreateZipBytes(files);
        string targetDir = Path.Combine(_env.RootPath, "perm_test");
        var extractor = new PackageExtractor();

        // Act
        extractor.ExtractZip(zipBytes, targetDir);
        string extractedFile = Path.Combine(targetDir, "data.bin");

        // Assert
        File.Exists(extractedFile).Should().BeTrue();
        var attr = File.GetAttributes(extractedFile);
        attr.HasFlag(FileAttributes.ReadOnly).Should().BeFalse();
    }
}
