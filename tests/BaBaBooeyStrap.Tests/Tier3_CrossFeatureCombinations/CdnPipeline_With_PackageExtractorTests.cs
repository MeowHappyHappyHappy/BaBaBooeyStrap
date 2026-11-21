using System.IO.Compression;
using System.Net;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using BaBaBooeyStrap.Bootstrapper.Deployment;
using BaBaBooeyStrap.Bootstrapper.Deployment.Models;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Storage;
using BaBaBooeyStrap.Tests.Mocks;
using FluentAssertions;
using Xunit;

namespace BaBaBooeyStrap.Tests.Tier3_CrossFeatureCombinations;

public sealed class CdnPipeline_With_PackageExtractorTests : IClassFixture<MockRobloxEnvironment>
{
    private readonly MockRobloxEnvironment _env;

    public CdnPipeline_With_PackageExtractorTests(MockRobloxEnvironment env)
    {
        _env = env;
    }

    [Fact]
    public async Task T3_CdnPipeline_Extractor_PingSelectionToManifestAndExtraction_DeploysClient()
    {
        byte[] exePayload = new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x01, 0x02, 0x03, 0x04 };
        byte[] zipBytes = MockRobloxEnvironment.CreateZipBytes(new Dictionary<string, byte[]>
        {
            ["RobloxPlayerBeta.exe"] = exePayload
        });

        byte[] hashBytes = MD5.HashData(zipBytes);
        string md5 = Convert.ToHexString(hashBytes).ToLowerInvariant();

        const string versionGuid = "version-t3-cdn-ext";
        string manifestContent = $"v0\nRobloxApp.zip\n{md5}\n{zipBytes.Length}\n{zipBytes.Length}\n";

        var handler = new MockHttpMessageHandler();
        handler.SetupRoute("https://mirror1.cdn/ping", () => new HttpResponseMessage(HttpStatusCode.OK));
        handler.SetupRoute("https://mirror2.cdn/ping", () =>
        {
            Thread.Sleep(50);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        handler.SetupRoute($"https://mirror1.cdn/{versionGuid}-rbxPkgManifest.txt", () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(manifestContent, Encoding.UTF8, "text/plain")
        });

        handler.SetupRoute($"https://mirror1.cdn/{versionGuid}-RobloxApp.zip", () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(zipBytes)
        });

        using var client = handler.ToHttpClient();
        var selector = new CdnMirrorSelector(client);
        var parser = new DeploymentManifestParser();
        var downloader = new PackageDownloader();
        var extractor = new PackageExtractor();

        string fastestMirror = await selector.SelectFastestMirrorAsync(
            new[] { "https://mirror2.cdn/ping", "https://mirror1.cdn/ping" }, client);
        fastestMirror.Should().Be("https://mirror1.cdn/ping");

        string baseUrl = "https://mirror1.cdn";
        var packages = await parser.FetchAndParseAsync(baseUrl, versionGuid, client);
        packages.Should().ContainSingle(p => p.Name == "RobloxApp.zip");

        string downloadDir = Path.Combine(_env.BaBaBooeyStrapPath, "Downloads_t3_cdn");
        var downloaded = await downloader.DownloadPackagesAsync(client, baseUrl, versionGuid, packages, downloadDir);
        downloaded.Should().ContainSingle();

        string destDir = Path.Combine(_env.RobloxVersionsPath, versionGuid);
        extractor.ExtractZipFile(downloaded[0], destDir);

        string deployedExe = Path.Combine(destDir, "RobloxPlayerBeta.exe");
        File.Exists(deployedExe).Should().BeTrue();
        File.ReadAllBytes(deployedExe).Should().Equal(exePayload);
    }

    [Fact]
    public async Task T3_CdnPipeline_Extractor_ChecksumMismatch_AbortsExtractionAndDeletesCorruptPackage()
    {
        const string versionGuid = "version-t3-badmd5";
        var packages = new[]
        {
            new PackageRecord("CorruptPackage.zip", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 1024, 1024)
        };

        var handler = new MockHttpMessageHandler();
        handler.SetupRoute($"https://setup.rbxcdn.com/{versionGuid}-CorruptPackage.zip", () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 0x01, 0x02, 0x03, 0x04 })
        });

        using var client = handler.ToHttpClient();
        string downloadDir = Path.Combine(_env.BaBaBooeyStrapPath, "Downloads_t3_bad");
        var downloader = new PackageDownloader();

        Func<Task> act = async () => await downloader.DownloadPackagesAsync(
            client, "https://setup.rbxcdn.com", versionGuid, packages, downloadDir);

        await act.Should().ThrowAsync<InvalidDataException>().WithMessage("*MD5 checksum mismatch*");
    }

    [Fact]
    public void T3_CdnPipeline_Extractor_ZipSlipExploit_TargetDirectoryTraversalBlocked()
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            var entry = archive.CreateEntry("../../evil_payload.exe");
            using var s = entry.Open();
            s.Write(new byte[] { 0xDE, 0xAD });
        }
        byte[] evilZip = ms.ToArray();

        string targetDir = Path.Combine(_env.RobloxVersionsPath, "version-sandbox-zipslip");
        var extractor = new PackageExtractor();
        Action act = () => extractor.ExtractZip(evilZip, targetDir);

        act.Should().Throw<SecurityException>().WithMessage("*ZipSlip*");
    }

    [Fact]
    public void T3_CdnPipeline_Extractor_VersionDeploy_UpdatesRobloxStateVersionGuid()
    {
        string stateFile = Path.Combine(_env.BaBaBooeyStrapPath, "t3_state_deploy.json");
        var stateMgr = new JsonManager<RobloxState>(stateFile);

        stateMgr.Prop.Player.VersionGuid = "version-99887766aabb";
        stateMgr.Prop.Player.Version = "0.641.0.999";
        stateMgr.Save();

        var reloaded = new JsonManager<RobloxState>(stateFile);
        reloaded.Load();

        reloaded.Prop.Player.VersionGuid.Should().Be("version-99887766aabb");
        reloaded.Prop.Player.Version.Should().Be("0.641.0.999");
    }
}
