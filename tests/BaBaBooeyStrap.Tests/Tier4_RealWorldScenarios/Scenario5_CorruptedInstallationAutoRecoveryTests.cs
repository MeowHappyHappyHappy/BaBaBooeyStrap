using System.IO;
using System.Security.Cryptography;
using BaBaBooeyStrap.Bootstrapper.Deployment;
using BaBaBooeyStrap.Tests.Mocks;
using BaBaBooeyStrap.Tests.Tier1_FeatureCoverage.Engines;
using FluentAssertions;
using Xunit;

namespace BaBaBooeyStrap.Tests.Tier4_RealWorldScenarios;

public sealed class Scenario5_CorruptedInstallationAutoRecoveryTests
{
    [Fact]
    public async Task Scenario5_CorruptBinaryDetection_TriggersManifestDownloadAndRepairsExecutable()
    {
        // 1. Arrange Sandbox with Corrupted Binary & Old Logs
        using var env = new MockRobloxEnvironment();
        string versionDir = Path.Combine(env.RobloxVersionsPath, "version-repair-test");
        Directory.CreateDirectory(versionDir);

        string exePath = Path.Combine(versionDir, "RobloxPlayerBeta.exe");
        File.WriteAllBytes(exePath, new byte[] { 0x00, 0x00 }); // Corrupted PE header

        // Create old logs (>30 days) and active live log
        string oldLog = Path.Combine(env.RobloxLogsPath, "old_log_30days.log");
        File.WriteAllText(oldLog, "old log content");
        File.SetLastWriteTimeUtc(oldLog, DateTime.UtcNow.AddDays(-30));

        using var activeLogStream = env.CreateActiveLogStream("active_session.log");
        activeLogStream.WriteLine("Active live session data");

        // 2. Detect Corruption (F10)
        byte[] currentExeBytes = File.ReadAllBytes(exePath);
        bool isValidHeader = currentExeBytes.Length >= 4 && currentExeBytes[0] == 0x4D && currentExeBytes[1] == 0x5A;
        isValidHeader.Should().BeFalse(); // Verified corrupt

        // 3. Setup CDN with Healthy Package
        byte[] validExe = new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0xAA, 0xBB, 0xCC };
        var healthyPackages = new Dictionary<string, byte[]>
        {
            ["RobloxApp.zip"] = MockRobloxEnvironment.CreateZipBytes(new Dictionary<string, byte[]>
            {
                ["RobloxPlayerBeta.exe"] = validExe
            })
        };

        var handler = new MockHttpMessageHandler()
            .SetupCdnDeployment("https://setup.rbxcdn.com", "version-repair-test", healthyPackages);
        using var httpClient = handler.ToHttpClient();

        // 4. Auto-Recovery Download & Extraction (F10, F11) — using production 3-line manifest parsing
        var parser = new DeploymentManifestParser();
        var downloader = new PackageDownloader();
        var extractor = new PackageExtractor();

        var manifest = await parser.FetchAndParseAsync("https://setup.rbxcdn.com", "version-repair-test", httpClient);

        string downloadsDir = Path.Combine(env.BaBaBooeyStrapPath, "Downloads");
        var downloaded = await downloader.DownloadPackagesAsync(
            httpClient, "https://setup.rbxcdn.com", "version-repair-test", manifest, downloadsDir);

        foreach (var file in downloaded)
        {
            extractor.ExtractZipFile(file, versionDir);
        }

        // Assert Binary Repaired
        byte[] restoredBytes = File.ReadAllBytes(exePath);
        restoredBytes.Should().BeEquivalentTo(validExe);

        // 5. Diagnostic Cleaner Execution with Active Session Protection (F19)
        var cleanResult = Cleaner.Clean(new[] { env.RobloxLogsPath }, TimeSpan.FromDays(7), allowedRootPrefix: env.RootPath);

        // Assertions
        cleanResult.FilesDeleted.Should().BeGreaterThanOrEqualTo(1);
        File.Exists(oldLog).Should().BeFalse(); // Purged
        File.Exists(activeLogStream.FilePath).Should().BeTrue(); // Preserved active session!

        // Cleaner safety guard check
        var escapeAction = () => Cleaner.Clean(new[] { @"C:\Windows\System32" }, TimeSpan.FromDays(7), allowedRootPrefix: env.RootPath);
        escapeAction.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task Scenario5_CorruptZipPackage_FailsMd5CheckAndReDownloadsHealthyPackage()
    {
        using var env = new MockRobloxEnvironment();
        string versionGuid = "version-md5-repair-test";

        byte[] healthyZipBytes = MockRobloxEnvironment.CreateZipBytes(new Dictionary<string, string>
        {
            ["RobloxPlayerBeta.exe"] = "valid_decompressed_executable"
        });
        string expectedMd5 = Convert.ToHexString(MD5.HashData(healthyZipBytes)).ToLowerInvariant();

        // Place corrupted zip file in Downloads
        string downloadsDir = Path.Combine(env.BaBaBooeyStrapPath, "Downloads");
        Directory.CreateDirectory(downloadsDir);
        string corruptedZipPath = Path.Combine(downloadsDir, "RobloxApp.zip");
        File.WriteAllBytes(corruptedZipPath, new byte[] { 0xFF, 0xEE, 0xDD, 0xCC }); // Bad archive

        // Checksum verification fails
        var verifier = new PackageVerifier();
        bool corruptValid = verifier.VerifyFileMd5(corruptedZipPath, expectedMd5);
        corruptValid.Should().BeFalse();

        // Setup CDN to serve healthy package
        var healthyPackages = new Dictionary<string, byte[]>
        {
            ["RobloxApp.zip"] = healthyZipBytes
        };
        var handler = new MockHttpMessageHandler()
            .SetupCdnDeployment("https://setup.rbxcdn.com", versionGuid, healthyPackages);
        using var httpClient = handler.ToHttpClient();

        var parser = new DeploymentManifestParser();
        var downloader = new PackageDownloader(verifier);
        var extractor = new PackageExtractor();

        var manifest = await parser.FetchAndParseAsync("https://setup.rbxcdn.com", versionGuid, httpClient);

        // Auto-recovery re-downloads healthy package
        var downloaded = await downloader.DownloadPackagesAsync(
            httpClient, "https://setup.rbxcdn.com", versionGuid, manifest, downloadsDir);

        downloaded.Should().HaveCount(1);
        bool healthyValid = verifier.VerifyFileMd5(downloaded[0], expectedMd5);
        healthyValid.Should().BeTrue();

        // Extraction restores version files
        string versionDir = Path.Combine(env.RobloxVersionsPath, versionGuid);
        extractor.ExtractZipFile(downloaded[0], versionDir);

        File.Exists(Path.Combine(versionDir, "RobloxPlayerBeta.exe")).Should().BeTrue();
    }

    [Fact]
    public void Scenario5_DiagnosticCleaner_PurgesStaleLogsWhilePreservingActiveGameSessionLog()
    {
        using var env = new MockRobloxEnvironment();

        // 1. Create logs with different ages
        string log45d = Path.Combine(env.RobloxLogsPath, "log_45days.log");
        string log15d = Path.Combine(env.RobloxLogsPath, "log_15days.log");
        string log2d = Path.Combine(env.RobloxLogsPath, "log_2days.log");

        File.WriteAllText(log45d, "ancient log data");
        File.SetLastWriteTimeUtc(log45d, DateTime.UtcNow.AddDays(-45));

        File.WriteAllText(log15d, "stale log data");
        File.SetLastWriteTimeUtc(log15d, DateTime.UtcNow.AddDays(-15));

        File.WriteAllText(log2d, "recent log data");
        File.SetLastWriteTimeUtc(log2d, DateTime.UtcNow.AddDays(-2));

        // 2. Open active session log stream (locked with FileShare.ReadWrite)
        using var activeSession = env.CreateActiveLogStream("active_game_process.log");
        activeSession.WriteLine("Live streaming telemetry");

        // 3. Execute Cleaner with 7-day policy
        var result = Cleaner.Clean(new[] { env.RobloxLogsPath }, TimeSpan.FromDays(7), allowedRootPrefix: env.RootPath);

        // 4. Assertions
        result.FilesDeleted.Should().Be(2); // 45d and 15d removed

        File.Exists(log45d).Should().BeFalse();
        File.Exists(log15d).Should().BeFalse();
        File.Exists(log2d).Should().BeTrue(); // Recent log kept
        File.Exists(activeSession.FilePath).Should().BeTrue(); // Active session preserved
    }

    [Fact]
    public void Scenario5_CleanerSafetyGuard_RejectsPathsOutsideSandboxRoot()
    {
        using var env = new MockRobloxEnvironment();

        // Attempting to clean directory outside sandbox allowedRootPrefix throws
        var cleanOutside = () => Cleaner.Clean(
            new[] { @"C:\Windows\Temp" },
            TimeSpan.FromDays(7),
            allowedRootPrefix: env.RootPath);

        cleanOutside.Should().Throw<InvalidOperationException>()
            .WithMessage("*outside allowed prefix*");

        // Directory traversal path attempt
        string traversalPath = Path.Combine(env.RootPath, "..", "EscapeFolder");
        var cleanTraversal = () => Cleaner.Clean(
            new[] { traversalPath },
            TimeSpan.FromDays(7),
            allowedRootPrefix: env.RootPath);

        cleanTraversal.Should().Throw<InvalidOperationException>();
    }
}
