using System.Net;
using System.Security.Cryptography;
using System.Text;
using BaBaBooeyStrap.Bootstrapper.Deployment;
using BaBaBooeyStrap.Bootstrapper.Deployment.Models;
using BaBaBooeyStrap.Tests.Mocks;
using FluentAssertions;
using Xunit;

namespace BaBaBooeyStrap.Tests.Bootstrapper;

public sealed class PackageDownloaderTests : IDisposable
{
    private readonly PackageDownloader _downloader = new();
    private readonly string _tempDirectory;

    public PackageDownloaderTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "DownloaderTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDirectory)) Directory.Delete(_tempDirectory, true); } catch { }
    }

    [Fact]
    public async Task DownloadPackagesAsync_HappyPath_DownloadsAllPackagesWithProgress()
    {
        // Arrange
        const string cdnHost = "https://setup.rbxcdn.com";
        const string versionGuid = "version-test-guid";

        byte[] payload1 = Encoding.UTF8.GetBytes("RobloxAppZipContent12345");
        byte[] payload2 = Encoding.UTF8.GetBytes("ContentFontsZipContent67890");

        string md5_1 = Convert.ToHexString(MD5.HashData(payload1)).ToLowerInvariant();
        string md5_2 = Convert.ToHexString(MD5.HashData(payload2)).ToLowerInvariant();

        var packages = new List<PackageRecord>
        {
            new("RobloxApp.zip", md5_1, payload1.Length, payload1.Length * 2),
            new("content-fonts.zip", md5_2, payload2.Length, payload2.Length * 2)
        };

        var handler = new MockHttpMessageHandler();
        handler.SetupRoute($"{cdnHost}/{versionGuid}-RobloxApp.zip", () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload1)
        });
        handler.SetupRoute($"{cdnHost}/{versionGuid}-content-fonts.zip", () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload2)
        });

        using var client = handler.ToHttpClient();
        var progressReports = new List<PackageDownloadProgress>();
        var progress = new Progress<PackageDownloadProgress>(p => progressReports.Add(p));

        // Act
        var downloadedPaths = await _downloader.DownloadPackagesAsync(
            client,
            cdnHost,
            versionGuid,
            packages,
            _tempDirectory,
            progress,
            maxDegreeOfParallelism: 2
        );

        // Assert
        downloadedPaths.Should().HaveCount(2);
        File.Exists(Path.Combine(_tempDirectory, "RobloxApp.zip")).Should().BeTrue();
        File.Exists(Path.Combine(_tempDirectory, "content-fonts.zip")).Should().BeTrue();

        File.ReadAllBytes(Path.Combine(_tempDirectory, "RobloxApp.zip")).Should().BeEquivalentTo(payload1);
        File.ReadAllBytes(Path.Combine(_tempDirectory, "content-fonts.zip")).Should().BeEquivalentTo(payload2);
    }

    [Fact]
    public async Task DownloadPackagesAsync_CorruptedChecksum_ThrowsInvalidDataExceptionAndCleansUp()
    {
        // Arrange
        const string cdnHost = "https://setup.rbxcdn.com";
        const string versionGuid = "version-bad-chk";

        byte[] badPayload = Encoding.UTF8.GetBytes("CorruptedDataPayloadFromNetwork");
        const string expectedMd5 = "ffffffffffffffffffffffffffffffff";

        var packages = new List<PackageRecord>
        {
            new("BadPackage.zip", expectedMd5, badPayload.Length, badPayload.Length)
        };

        var handler = new MockHttpMessageHandler();
        handler.SetupRoute($"{cdnHost}/{versionGuid}-BadPackage.zip", () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(badPayload)
        });

        using var client = handler.ToHttpClient();

        // Act & Assert
        Func<Task> act = async () => await _downloader.DownloadPackagesAsync(
            client,
            cdnHost,
            versionGuid,
            packages,
            _tempDirectory
        );

        await act.Should().ThrowAsync<InvalidDataException>().WithMessage("*checksum mismatch*");

        // Verify partial/corrupted file was deleted
        File.Exists(Path.Combine(_tempDirectory, "BadPackage.zip")).Should().BeFalse();
    }

    [Fact]
    public async Task DownloadPackagesAsync_CachedPackageWithMatchingMd5_SkipsNetworkDownload()
    {
        // Arrange
        const string cdnHost = "https://setup.rbxcdn.com";
        const string versionGuid = "version-cached";

        byte[] existingBytes = Encoding.UTF8.GetBytes("AlreadyExistingPackageContent");
        string md5 = Convert.ToHexString(MD5.HashData(existingBytes)).ToLowerInvariant();

        string cachedFile = Path.Combine(_tempDirectory, "CachedPackage.zip");
        await File.WriteAllBytesAsync(cachedFile, existingBytes);

        var packages = new List<PackageRecord>
        {
            new("CachedPackage.zip", md5, existingBytes.Length, existingBytes.Length)
        };

        // Network handler returns 404 to prove network is not queried when cache is valid
        var handler = new MockHttpMessageHandler();
        handler.SetupRoute($"{cdnHost}/{versionGuid}-CachedPackage.zip", () => new HttpResponseMessage(HttpStatusCode.NotFound));

        using var client = handler.ToHttpClient();

        // Act
        var downloaded = await _downloader.DownloadPackagesAsync(client, cdnHost, versionGuid, packages, _tempDirectory);

        // Assert: Succeeds without calling network because cache matches
        downloaded.Should().ContainSingle();
        downloaded[0].Should().Be(cachedFile);
    }

    [Fact]
    public async Task DownloadPackagesAsync_CancellationRequested_AbortsCleanly()
    {
        // Arrange
        const string cdnHost = "https://setup.rbxcdn.com";
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancelled token

        var packages = new List<PackageRecord>
        {
            new("Cancelled.zip", "md5", 100, 200)
        };

        var handler = new MockHttpMessageHandler();
        using var client = handler.ToHttpClient();

        // Act & Assert
        Func<Task> act = async () => await _downloader.DownloadPackagesAsync(
            client,
            cdnHost,
            "guid",
            packages,
            _tempDirectory,
            null,
            5,
            cts.Token
        );

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
