using System.Security.Cryptography;
using System.Text;
using BaBaBooeyStrap.Bootstrapper.Deployment;
using FluentAssertions;
using Xunit;

namespace BaBaBooeyStrap.Tests.Bootstrapper;

public sealed class PackageVerifierTests : IDisposable
{
    private readonly PackageVerifier _verifier = new();
    private readonly string _tempDirectory;

    public PackageVerifierTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "VerifierTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDirectory)) Directory.Delete(_tempDirectory, true); } catch { }
    }

    [Fact]
    public void VerifyMd5_ByteArray_MatchesValidHashAndRejectsMismatchedHash()
    {
        // Arrange
        byte[] payload = Encoding.UTF8.GetBytes("BaBaBooeyStrapPackageVerificationPayload");
        string expectedMd5 = Convert.ToHexString(MD5.HashData(payload)).ToLowerInvariant();

        // Act & Assert
        _verifier.VerifyMd5(payload, expectedMd5).Should().BeTrue();
        _verifier.VerifyMd5(payload, expectedMd5.ToUpperInvariant()).Should().BeTrue(); // Case insensitive
        _verifier.VerifyMd5(payload, "00000000000000000000000000000000").Should().BeFalse();
        _verifier.VerifyMd5((byte[])null!, expectedMd5).Should().BeFalse();
        _verifier.VerifyMd5(payload, "").Should().BeFalse();
    }

    [Fact]
    public void VerifyMd5_ReadOnlySpan_ValidatesSpanSlice()
    {
        // Arrange
        byte[] payload = Encoding.UTF8.GetBytes("Prefix_DataToVerify_Suffix");
        ReadOnlySpan<byte> slice = payload.AsSpan(7, 12); // "DataToVerify"
        string expectedMd5 = Convert.ToHexString(MD5.HashData(slice.ToArray())).ToLowerInvariant();

        // Act & Assert
        _verifier.VerifyMd5(slice, expectedMd5).Should().BeTrue();
        _verifier.VerifyMd5(slice, "wrong_hash").Should().BeFalse();
    }

    [Fact]
    public void VerifyFileMd5_ExistingFile_ValidatesFileChecksum()
    {
        // Arrange
        string testFile = Path.Combine(_tempDirectory, "test_verify.bin");
        byte[] content = Encoding.UTF8.GetBytes("FileContentForMd5Check_1234567890");
        File.WriteAllBytes(testFile, content);

        string expectedMd5 = Convert.ToHexString(MD5.HashData(content)).ToLowerInvariant();

        // Act & Assert
        _verifier.VerifyFileMd5(testFile, expectedMd5).Should().BeTrue();
        _verifier.VerifyFileMd5(testFile, "bad_md5").Should().BeFalse();
        _verifier.VerifyFileMd5(Path.Combine(_tempDirectory, "non_existent.bin"), expectedMd5).Should().BeFalse();
    }

    [Fact]
    public async Task VerifyFileMd5Async_StreamingLargeFile_ComputesAndMatchesCorrectHash()
    {
        // Arrange: Generate 1MB test file
        string testFile = Path.Combine(_tempDirectory, "large_stream_verify.bin");
        byte[] chunk = Encoding.UTF8.GetBytes("1234567890ABCDEF1234567890ABCDEF");
        using (var fs = File.Create(testFile))
        {
            for (int i = 0; i < 32768; i++) // 32KB * 32 = 1MB
            {
                await fs.WriteAsync(chunk);
            }
        }

        byte[] allBytes = await File.ReadAllBytesAsync(testFile);
        string expectedMd5 = Convert.ToHexString(MD5.HashData(allBytes)).ToLowerInvariant();

        // Act
        bool valid = await _verifier.VerifyFileMd5Async(testFile, expectedMd5);
        bool invalid = await _verifier.VerifyFileMd5Async(testFile, "bad_hash_value");

        // Assert
        valid.Should().BeTrue();
        invalid.Should().BeFalse();
    }

    [Fact]
    public void CreateStreamingHash_IncrementalHash_AccumulatesChunksAccurately()
    {
        // Arrange
        byte[] part1 = Encoding.UTF8.GetBytes("BaBaBooey_Part1_");
        byte[] part2 = Encoding.UTF8.GetBytes("BaBaBooey_Part2_");
        byte[] combined = Encoding.UTF8.GetBytes("BaBaBooey_Part1_BaBaBooey_Part2_");

        string expectedCombinedHash = Convert.ToHexString(MD5.HashData(combined)).ToLowerInvariant();

        // Act
        using var incremental = _verifier.CreateStreamingHash();
        incremental.AppendData(part1);
        incremental.AppendData(part2);
        byte[] actualHashBytes = incremental.GetHashAndReset();
        string actualHash = Convert.ToHexString(actualHashBytes).ToLowerInvariant();

        // Assert
        actualHash.Should().Be(expectedCombinedHash);
    }
}
