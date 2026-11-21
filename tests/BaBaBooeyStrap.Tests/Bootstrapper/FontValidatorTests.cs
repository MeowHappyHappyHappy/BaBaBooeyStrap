using System.Text;
using BaBaBooeyStrap.Bootstrapper.Mods;
using FluentAssertions;
using Xunit;

namespace BaBaBooeyStrap.Tests.Bootstrapper;

public sealed class FontValidatorTests : IDisposable
{
    private readonly FontValidator _validator = new();
    private readonly string _tempDirectory;

    public FontValidatorTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "FontTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDirectory)) Directory.Delete(_tempDirectory, true); } catch { }
    }

    [Fact]
    public void IsValidFont_TrueTypeMagicBytes_ReturnsTrue()
    {
        // 00 01 00 00
        byte[] ttfData = new byte[] { 0x00, 0x01, 0x00, 0x00, 0x00, 0x0C, 0x00, 0x80 };
        FontValidator.IsValidFont(ttfData).Should().BeTrue();
    }

    [Fact]
    public void IsValidFont_OpenTypeMagicBytes_ReturnsTrue()
    {
        // 'OTTO'
        byte[] otfData = new byte[] { 0x4F, 0x54, 0x54, 0x4F, 0x00, 0x0A, 0x00, 0x40 };
        FontValidator.IsValidFont(otfData).Should().BeTrue();
    }

    [Fact]
    public void IsValidFont_TtcMagicBytes_ReturnsTrue()
    {
        // 'ttcf'
        byte[] ttcData = new byte[] { 0x74, 0x74, 0x63, 0x66, 0x00, 0x02, 0x00, 0x00 };
        FontValidator.IsValidFont(ttcData).Should().BeTrue();
    }

    [Fact]
    public void IsValidFont_InvalidMagicBytes_ReturnsFalse()
    {
        // Plain text
        byte[] textData = Encoding.UTF8.GetBytes("Not A Font Header At All");
        FontValidator.IsValidFont(textData).Should().BeFalse();

        // PE Executable MZ header
        byte[] peData = new byte[] { 0x4D, 0x5A, 0x90, 0x00 };
        FontValidator.IsValidFont(peData).Should().BeFalse();

        // Empty / Null / Truncated (< 4 bytes)
        FontValidator.IsValidFont(new byte[] { 0x00, 0x01 }).Should().BeFalse();
        FontValidator.IsValidFont(null!).Should().BeFalse();
    }

    [Fact]
    public void IsValidFont_OversizedFontAsset_ReturnsFalse()
    {
        // Arrange: Byte array exceeding 50MB
        byte[] oversized = new byte[51 * 1024 * 1024];
        oversized[0] = 0x00; oversized[1] = 0x01; oversized[2] = 0x00; oversized[3] = 0x00;

        // Act & Assert
        FontValidator.IsValidFont(oversized).Should().BeFalse();
    }

    [Fact]
    public void ValidateFont_FileOnDisk_IdentifiesFormatAndHandlesErrors()
    {
        // Arrange
        string ttfFile = Path.Combine(_tempDirectory, "test.ttf");
        File.WriteAllBytes(ttfFile, new byte[] { 0x00, 0x01, 0x00, 0x00, 0xAA, 0xBB });

        string txtFile = Path.Combine(_tempDirectory, "test.txt");
        File.WriteAllText(txtFile, "Some text");

        // Act & Assert
        bool validTtf = _validator.ValidateFont(ttfFile, out var format, out string? error);
        validTtf.Should().BeTrue();
        format.Should().Be(FontFormat.TrueType);
        error.Should().BeNull();

        bool invalidTxt = _validator.ValidateFont(txtFile, out var txtFormat, out string? txtError);
        invalidTxt.Should().BeFalse();
        txtFormat.Should().Be(FontFormat.Unknown);
        txtError.Should().Contain("Unsupported font extension");

        bool missing = _validator.ValidateFont(Path.Combine(_tempDirectory, "missing.ttf"), out _, out string? missError);
        missing.Should().BeFalse();
        missError.Should().Contain("does not exist");
    }
}
