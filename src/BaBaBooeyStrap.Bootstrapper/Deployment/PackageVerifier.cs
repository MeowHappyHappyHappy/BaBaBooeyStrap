using System.Security.Cryptography;
using BaBaBooeyStrap.Bootstrapper.Deployment.Abstractions;

namespace BaBaBooeyStrap.Bootstrapper.Deployment;

public class PackageVerifier : IPackageVerifier
{
    public bool VerifyMd5(ReadOnlySpan<byte> data, string expectedMd5)
    {
        if (string.IsNullOrWhiteSpace(expectedMd5)) return false;

        byte[] hash = MD5.HashData(data);
        string actualHex = Convert.ToHexString(hash);
        return actualHex.Equals(expectedMd5.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public bool VerifyMd5(byte[] data, string expectedMd5)
    {
        if (data == null || string.IsNullOrWhiteSpace(expectedMd5)) return false;
        return VerifyMd5(data.AsSpan(), expectedMd5);
    }

    public bool VerifyFileMd5(string filePath, string expectedMd5)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath) || string.IsNullOrWhiteSpace(expectedMd5))
        {
            return false;
        }

        using var stream = File.OpenRead(filePath);
        byte[] hash = MD5.HashData(stream);
        string actualHex = Convert.ToHexString(hash);
        return actualHex.Equals(expectedMd5.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public async Task<bool> VerifyFileMd5Async(string filePath, string expectedMd5, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath) || string.IsNullOrWhiteSpace(expectedMd5))
        {
            return false;
        }

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        using var incrementalHash = CreateStreamingHash();

        byte[] buffer = new byte[81920];
        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
        {
            incrementalHash.AppendData(buffer, 0, bytesRead);
        }

        byte[] hash = incrementalHash.GetHashAndReset();
        string actualHex = Convert.ToHexString(hash);
        return actualHex.Equals(expectedMd5.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public IncrementalHash CreateStreamingHash()
    {
        return IncrementalHash.CreateHash(HashAlgorithmName.MD5);
    }

    public static bool StaticVerifyMd5(byte[] data, string expectedMd5)
    {
        if (data == null || string.IsNullOrWhiteSpace(expectedMd5)) return false;
        byte[] hash = MD5.HashData(data);
        string actualHex = Convert.ToHexString(hash);
        return actualHex.Equals(expectedMd5.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public static bool StaticVerifyFileMd5(string filePath, string expectedMd5)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath) || string.IsNullOrWhiteSpace(expectedMd5))
        {
            return false;
        }

        using var stream = File.OpenRead(filePath);
        byte[] hash = MD5.HashData(stream);
        string actualHex = Convert.ToHexString(hash);
        return actualHex.Equals(expectedMd5.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
