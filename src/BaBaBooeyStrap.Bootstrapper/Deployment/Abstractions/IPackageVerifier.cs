using System.Security.Cryptography;

namespace BaBaBooeyStrap.Bootstrapper.Deployment.Abstractions;

public interface IPackageVerifier
{
    bool VerifyMd5(ReadOnlySpan<byte> data, string expectedMd5);
    bool VerifyMd5(byte[] data, string expectedMd5);
    Task<bool> VerifyFileMd5Async(string filePath, string expectedMd5, CancellationToken ct = default);
    bool VerifyFileMd5(string filePath, string expectedMd5);
    IncrementalHash CreateStreamingHash();
}
