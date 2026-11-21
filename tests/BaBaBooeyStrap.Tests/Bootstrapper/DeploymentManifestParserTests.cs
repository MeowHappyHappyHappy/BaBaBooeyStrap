using System.Net;
using System.Text;
using BaBaBooeyStrap.Bootstrapper.Deployment;
using BaBaBooeyStrap.Bootstrapper.Deployment.Models;
using BaBaBooeyStrap.Tests.Mocks;
using FluentAssertions;
using Xunit;

namespace BaBaBooeyStrap.Tests.Bootstrapper;

public sealed class DeploymentManifestParserTests
{
    private readonly DeploymentManifestParser _parser = new();

    [Fact]
    public void Parse_HappyPath_ExtractsAllValidPackageRecords()
    {
        // Arrange
        string manifestContent =
            "v0\n" +
            "RobloxApp.zip\n" +
            "e4d909c290d0fb1ca068ffaddf22cbd0\n" +
            "52428800\n" +
            "125829120\n" +
            "content-fonts.zip\n" +
            "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6\n" +
            "10485760\n" +
            "20971520\n";

        // Act
        var packages = _parser.Parse(manifestContent);

        // Assert
        packages.Should().HaveCount(2);

        packages[0].Name.Should().Be("RobloxApp.zip");
        packages[0].Signature.Should().Be("e4d909c290d0fb1ca068ffaddf22cbd0");
        packages[0].Md5.Should().Be("e4d909c290d0fb1ca068ffaddf22cbd0");
        packages[0].CompressedSize.Should().Be(52428800);
        packages[0].PackedSize.Should().Be(52428800);
        packages[0].UncompressedSize.Should().Be(125829120);
        packages[0].Size.Should().Be(125829120);

        packages[1].Name.Should().Be("content-fonts.zip");
        packages[1].Signature.Should().Be("a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6");
        packages[1].CompressedSize.Should().Be(10485760);
        packages[1].UncompressedSize.Should().Be(20971520);
    }

    [Fact]
    public void Parse_EmptyOrWhitespace_ThrowsFormatException()
    {
        // Act & Assert
        Action act1 = () => _parser.Parse("");
        Action act2 = () => _parser.Parse("    \r\n\t  ");

        act1.Should().Throw<FormatException>().WithMessage("*empty*");
        act2.Should().Throw<FormatException>().WithMessage("*empty*");
    }

    [Theory]
    [InlineData("v1\nRobloxApp.zip\ne4d909c290d0fb1ca068ffaddf22cbd0\n100\n200\n")]
    [InlineData("<html>502 Bad Gateway</html>")]
    [InlineData("invalid header without v0")]
    public void Parse_InvalidHeader_ThrowsInvalidDataException(string invalidContent)
    {
        // Act & Assert
        Action act = () => _parser.Parse(invalidContent);
        act.Should().Throw<InvalidDataException>().WithMessage("*v0*");
    }

    [Fact]
    public void Parse_RobloxPlayerLauncherEncountered_SkipsLauncherAndContinuesParsing()
    {
        // Arrange: RobloxPlayerLauncher.exe is ignored while subsequent packages continue parsing
        string manifestContent =
            "v0\n" +
            "RobloxApp.zip\n" +
            "11111111111111111111111111111111\n" +
            "100\n" +
            "200\n" +
            "RobloxPlayerLauncher.exe\n" +
            "22222222222222222222222222222222\n" +
            "300\n" +
            "400\n" +
            "WebView2.zip\n" +
            "33333333333333333333333333333333\n" +
            "500\n" +
            "600\n";

        // Act
        var packages = _parser.Parse(manifestContent);

        // Assert
        packages.Should().HaveCount(2);
        packages.Select(p => p.Name).Should().Equal("RobloxApp.zip", "WebView2.zip");
    }

    [Fact]
    public void Parse_RobloxPlayerInstaller_IsExcludedFromResults()
    {
        // Arrange: RobloxPlayerInstaller.exe is filtered out
        string manifestContent =
            "v0\n" +
            "RobloxApp.zip\n" +
            "11111111111111111111111111111111\n" +
            "100\n" +
            "200\n" +
            "RobloxPlayerInstaller.exe\n" +
            "22222222222222222222222222222222\n" +
            "300\n" +
            "400\n" +
            "content-fonts.zip\n" +
            "33333333333333333333333333333333\n" +
            "500\n" +
            "600\n";

        // Act
        var packages = _parser.Parse(manifestContent);

        // Assert
        packages.Should().HaveCount(2);
        packages.Select(p => p.Name).Should().Contain(new[] { "RobloxApp.zip", "content-fonts.zip" });
        packages.Select(p => p.Name).Should().NotContain("RobloxPlayerInstaller.exe");
    }

    [Fact]
    public void Parse_TruncatedRecordLines_HandlesGracefullyWithoutThrowing()
    {
        // Arrange: Only 2 lines for second package
        string manifestContent =
            "v0\n" +
            "RobloxApp.zip\n" +
            "11111111111111111111111111111111\n" +
            "100\n" +
            "200\n" +
            "Incomplete.zip\n" +
            "22222222222222222222222222222222\n";

        // Act
        var packages = _parser.Parse(manifestContent);

        // Assert: First package parsed, partial second ignored
        packages.Should().HaveCount(1);
        packages[0].Name.Should().Be("RobloxApp.zip");
    }

    [Fact]
    public async Task FetchAndParseAsync_WithValidCdnResponse_FetchesAndParsesPackages()
    {
        // Arrange
        const string cdnHost = "https://setup.rbxcdn.com";
        const string versionGuid = "version-4ea8e85481e34f46";
        string manifestPayload =
            "v0\n" +
            "shaders.zip\n" +
            "aabbccddeeff00112233445566778899\n" +
            "1000\n" +
            "2000\n";

        var handler = new MockHttpMessageHandler();
        handler.SetupRoute($"{cdnHost}/{versionGuid}-rbxPkgManifest.txt", () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(manifestPayload, Encoding.UTF8)
        });

        using var client = handler.ToHttpClient();

        // Act
        var packages = await _parser.FetchAndParseAsync(cdnHost, versionGuid, client);

        // Assert
        packages.Should().HaveCount(1);
        packages[0].Name.Should().Be("shaders.zip");
        packages[0].Signature.Should().Be("aabbccddeeff00112233445566778899");
    }

    [Fact]
    public void Parse_ThreeLineTriplets_ExtractsAllPackagesIncludingNumericNextPackageName()
    {
        // Arrange: 3-line manifest where the second package has a purely numeric name
        string manifestContent =
            "v0\n" +
            "RobloxApp.zip\n" +
            "e4d909c290d0fb1ca068ffaddf22cbd0\n" +
            "1024\n" +
            "12345\n" +
            "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6\n" +
            "2048\n";

        // Act
        var packages = _parser.Parse(manifestContent);

        // Assert
        packages.Should().HaveCount(2);
        packages[0].Name.Should().Be("RobloxApp.zip");
        packages[0].CompressedSize.Should().Be(1024);
        packages[0].UncompressedSize.Should().Be(1024);
        packages[1].Name.Should().Be("12345");
        packages[1].Signature.Should().Be("a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6");
        packages[1].CompressedSize.Should().Be(2048);
    }
}
