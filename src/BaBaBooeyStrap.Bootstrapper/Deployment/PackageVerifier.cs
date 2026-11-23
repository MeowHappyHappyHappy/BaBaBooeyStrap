using System.Buffers;
using System.Security.Cryptography;
using BaBaBooeyStrap.Bootstrapper.Deployment.Abstractions;

namespace BaBaBooeyStrap.Bootstrapper.Deployment;

public class PackageVerifier : IPackageVerifier
{
    public bool VerifyMd5(ReadOnlySpan<byte> data, string expectedMd5)
    {
        if (!TryDecodeExpectedMd5(expectedMd5, out byte[] expectedBytes))
        {
            return false;
        }

        Span<byte> actualHash = stackalloc byte[16];
        MD5.HashData(data, actualHash);
        return CryptographicOperations.FixedTimeEquals(actualHash, expectedBytes);
    }

    public bool VerifyMd5(byte[] data, string expectedMd5)
    {
        if (data == null) return false;
        return VerifyMd5(data.AsSpan(), expectedMd5);
    }

    public bool VerifyFileMd5(string filePath, string expectedMd5)
    {
        return StaticVerifyFileMd5(filePath, expectedMd5);
    }

    public async Task<bool> VerifyFileMd5Async(string filePath, string expectedMd5, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) ||
            !File.Exists(filePath) ||
            !TryDecodeExpectedMd5(expectedMd5, out byte[] expectedBytes))
        {
            return false;
        }

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        if (stream.Length == 0)
        {
            return false;
        }

        using var incrementalHash = CreateStreamingHash();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            int bytesRead;
            while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
            {
                incrementalHash.AppendData(buffer, 0, bytesRead);
            }

            byte[] actualHash = incrementalHash.GetHashAndReset();
            return CryptographicOperations.FixedTimeEquals(actualHash, expectedBytes);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public IncrementalHash CreateStreamingHash()
    {
        return IncrementalHash.CreateHash(HashAlgorithmName.MD5);
    }

    public static bool StaticVerifyMd5(byte[] data, string expectedMd5)
    {
        if (data == null || !TryDecodeExpectedMd5(expectedMd5, out byte[] expectedBytes))
        {
            return false;
        }

        Span<byte> actualHash = stackalloc byte[16];
        MD5.HashData(data, actualHash);
        return CryptographicOperations.FixedTimeEquals(actualHash, expectedBytes);
    }

    public static bool StaticVerifyFileMd5(string filePath, string expectedMd5)
    {
        if (string.IsNullOrWhiteSpace(filePath) ||
            !File.Exists(filePath) ||
            !TryDecodeExpectedMd5(expectedMd5, out byte[] expectedBytes))
        {
            return false;
        }

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length == 0)
        {
            return false;
        }

        Span<byte> actualHash = stackalloc byte[16];
        MD5.HashData(stream, actualHash);
        return CryptographicOperations.FixedTimeEquals(actualHash, expectedBytes);
    }

    private static bool TryDecodeExpectedMd5(string? expectedMd5, out byte[] expectedBytes)
    {
        expectedBytes = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(expectedMd5))
        {
            return false;
        }

        string trimmed = expectedMd5.Trim();
        if (trimmed.Length != 32)
        {
            return false;
        }

        try
        {
            expectedBytes = Convert.FromHexString(trimmed);
            return expectedBytes.Length == 16;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
