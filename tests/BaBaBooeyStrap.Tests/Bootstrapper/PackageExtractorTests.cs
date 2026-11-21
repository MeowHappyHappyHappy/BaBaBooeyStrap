using System.Security;
using System.Text;
using BaBaBooeyStrap.Bootstrapper.Deployment;
using ICSharpCode.SharpZipLib.Zip;
using FluentAssertions;
using Xunit;

namespace BaBaBooeyStrap.Tests.Bootstrapper;

public sealed class PackageExtractorTests : IDisposable
{
    private readonly PackageExtractor _extractor = new();
    private readonly string _tempDirectory;

    public PackageExtractorTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "ExtractorTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDirectory)) Directory.Delete(_tempDirectory, true); } catch { }
    }

    private static byte[] CreateZipArchive(Dictionary<string, byte[]> entries)
    {
        using var ms = new MemoryStream();
        using (var zipStream = new ZipOutputStream(ms))
        {
            zipStream.SetLevel(3);
            foreach (var (entryName, data) in entries)
            {
                var entry = new ZipEntry(entryName)
                {
                    DateTime = DateTime.UtcNow,
                    Size = data.Length
                };
                zipStream.PutNextEntry(entry);
                zipStream.Write(data, 0, data.Length);
                zipStream.CloseEntry();
            }
            zipStream.IsStreamOwner = false;
        }
        return ms.ToArray();
    }

    [Fact]
    public void ExtractZip_ValidArchiveBytes_ExtractsAllFilesPreservingStructure()
    {
        // Arrange
        var files = new Dictionary<string, byte[]>
        {
            ["RobloxPlayerBeta.exe"] = Encoding.UTF8.GetBytes("NativeBinaryContent"),
            ["shaders/default.vert"] = Encoding.UTF8.GetBytes("VertexShaderCode"),
            ["sub/folder/file.txt"] = Encoding.UTF8.GetBytes("NestedFileContent")
        };
        byte[] zipBytes = CreateZipArchive(files);
        string extractDir = Path.Combine(_tempDirectory, "extract_valid");

        // Act
        _extractor.ExtractZip(zipBytes, extractDir);

        // Assert
        File.Exists(Path.Combine(extractDir, "RobloxPlayerBeta.exe")).Should().BeTrue();
        File.Exists(Path.Combine(extractDir, "shaders", "default.vert")).Should().BeTrue();
        File.Exists(Path.Combine(extractDir, "sub", "folder", "file.txt")).Should().BeTrue();

        File.ReadAllText(Path.Combine(extractDir, "shaders", "default.vert")).Should().Be("VertexShaderCode");
    }

    [Fact]
    public void ExtractZipFile_ValidZipFile_ExtractsCorrectly()
    {
        // Arrange
        var files = new Dictionary<string, byte[]>
        {
            ["config.xml"] = Encoding.UTF8.GetBytes("<Config></Config>")
        };
        byte[] zipBytes = CreateZipArchive(files);
        string zipFilePath = Path.Combine(_tempDirectory, "test_package.zip");
        File.WriteAllBytes(zipFilePath, zipBytes);

        string extractDir = Path.Combine(_tempDirectory, "extract_file");

        // Act
        _extractor.ExtractZipFile(zipFilePath, extractDir);

        // Assert
        File.Exists(Path.Combine(extractDir, "config.xml")).Should().BeTrue();
    }

    [Fact]
    public void ExtractZip_ZipSlipVulnerability_ThrowsSecurityException()
    {
        // Arrange: Create entry with path traversal: ../../evil.txt
        var files = new Dictionary<string, byte[]>
        {
            ["../../evil_traversal.txt"] = Encoding.UTF8.GetBytes("MaliciousPayload")
        };
        byte[] maliciousZip = CreateZipArchive(files);
        string extractDir = Path.Combine(_tempDirectory, "zip_slip_target");

        // Act & Assert
        Action act = () => _extractor.ExtractZip(maliciousZip, extractDir);
        act.Should().Throw<SecurityException>().WithMessage("*ZipSlip*");
    }

    [Fact]
    public void ExtractZip_ZeroByteOrCorruptedArchive_ThrowsInvalidDataException()
    {
        // Arrange
        byte[] emptyBytes = Array.Empty<byte>();
        byte[] corruptBytes = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00, 0xFF, 0xFE, 0xFD };
        string extractDir = Path.Combine(_tempDirectory, "bad_zip");

        // Act & Assert
        Action act1 = () => _extractor.ExtractZip(emptyBytes, extractDir);
        Action act2 = () => _extractor.ExtractZip(corruptBytes, extractDir);

        act1.Should().Throw<InvalidDataException>();
        act2.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void ExtractZip_MissingZipFile_ThrowsFileNotFoundException()
    {
        // Arrange
        string nonExistentZip = Path.Combine(_tempDirectory, "missing.zip");
        string extractDir = Path.Combine(_tempDirectory, "extract_missing");

        // Act & Assert
        Action act = () => _extractor.ExtractZipFile(nonExistentZip, extractDir);
        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void ExtractZip_LockedTtfFontFile_ToleratesAndContinuesExtraction()
    {
        // Arrange: Locked .ttf font file should be skipped without throwing
        string extractDir = Path.Combine(_tempDirectory, "font_tolerance_target");
        Directory.CreateDirectory(extractDir);

        string lockedFont = Path.Combine(extractDir, "CustomFont.ttf");
        File.WriteAllBytes(lockedFont, new byte[] { 1, 2, 3 });

        // Lock font with FileShare.None
        using var fs = new FileStream(lockedFont, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var files = new Dictionary<string, byte[]>
        {
            ["CustomFont.ttf"] = new byte[] { 9, 9, 9 },
            ["normal.txt"] = Encoding.UTF8.GetBytes("NormalContent")
        };
        byte[] zipBytes = CreateZipArchive(files);

        // Act: Should NOT throw IOException because .ttf failure is tolerated
        Action act = () => _extractor.ExtractZip(zipBytes, extractDir);
        act.Should().NotThrow();

        // Verify normal file extracted successfully
        File.Exists(Path.Combine(extractDir, "normal.txt")).Should().BeTrue();
    }

    [Fact]
    public void ExtractZip_LockedOtfAndTtcFontFiles_ToleratesAndContinuesExtraction()
    {
        // Arrange: Locked .otf and .ttc font files should be skipped without throwing
        string extractDir = Path.Combine(_tempDirectory, "font_tolerance_extended_target");
        Directory.CreateDirectory(extractDir);

        string lockedOtf = Path.Combine(extractDir, "CustomFont.otf");
        string lockedTtc = Path.Combine(extractDir, "CustomFont.ttc");
        File.WriteAllBytes(lockedOtf, new byte[] { 1, 2, 3 });
        File.WriteAllBytes(lockedTtc, new byte[] { 4, 5, 6 });

        using var fsOtf = new FileStream(lockedOtf, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var fsTtc = new FileStream(lockedTtc, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var files = new Dictionary<string, byte[]>
        {
            ["CustomFont.otf"] = new byte[] { 9, 9, 9 },
            ["CustomFont.ttc"] = new byte[] { 8, 8, 8 },
            ["normal_after.txt"] = Encoding.UTF8.GetBytes("NormalContent")
        };
        byte[] zipBytes = CreateZipArchive(files);

        // Act: Should NOT throw IOException because .otf and .ttc failures are tolerated
        Action act = () => _extractor.ExtractZip(zipBytes, extractDir);
        act.Should().NotThrow();

        // Verify normal file extracted successfully
        File.Exists(Path.Combine(extractDir, "normal_after.txt")).Should().BeTrue();
    }

    [Fact]
    public void ExtractZip_LockedNonFontFile_ThrowsIOException()
    {
        // Arrange: Locked binary application file MUST throw IOException
        string extractDir = Path.Combine(_tempDirectory, "locked_app_target");
        Directory.CreateDirectory(extractDir);

        string lockedApp = Path.Combine(extractDir, "RobloxPlayerBeta.exe");
        File.WriteAllText(lockedApp, "LockedBinary");

        using var fs = new FileStream(lockedApp, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var files = new Dictionary<string, byte[]>
        {
            ["RobloxPlayerBeta.exe"] = Encoding.UTF8.GetBytes("NewBinary")
        };
        byte[] zipBytes = CreateZipArchive(files);

        // Act & Assert
        Action act = () => _extractor.ExtractZip(zipBytes, extractDir);
        act.Should().Throw<IOException>();
    }

    [Fact]
    public async Task ExtractPackageAsync_SpecificFilesFilter_ExtractsOnlyRequestedFiles()
    {
        // Arrange
        var files = new Dictionary<string, byte[]>
        {
            ["asset1.png"] = new byte[] { 1, 2, 3 },
            ["asset2.png"] = new byte[] { 4, 5, 6 },
            ["asset3.png"] = new byte[] { 7, 8, 9 }
        };
        byte[] zipBytes = CreateZipArchive(files);
        string zipPath = Path.Combine(_tempDirectory, "specific_pkg.zip");
        File.WriteAllBytes(zipPath, zipBytes);

        string versionDir = Path.Combine(_tempDirectory, "version_specific");

        // Act: Extract only asset2.png
        await _extractor.ExtractPackageAsync(
            zipPath,
            versionDir,
            "textures",
            specificFiles: new[] { "asset2.png" }
        );

        // Assert
        File.Exists(Path.Combine(versionDir, "textures", "asset2.png")).Should().BeTrue();
        File.Exists(Path.Combine(versionDir, "textures", "asset1.png")).Should().BeFalse();
        File.Exists(Path.Combine(versionDir, "textures", "asset3.png")).Should().BeFalse();
    }
}
