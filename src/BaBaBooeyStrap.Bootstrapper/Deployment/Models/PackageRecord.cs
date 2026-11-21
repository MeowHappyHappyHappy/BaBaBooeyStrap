namespace BaBaBooeyStrap.Bootstrapper.Deployment.Models;

public record PackageRecord
{
    public string Name { get; init; } = string.Empty;
    public string Signature { get; init; } = string.Empty;
    public long CompressedSize { get; init; }
    public long UncompressedSize { get; init; }

    public string Md5 => Signature;
    public long PackedSize => CompressedSize;
    public long Size => UncompressedSize;

    public PackageRecord() { }

    public PackageRecord(string name, string signature, long compressedSize, long uncompressedSize)
    {
        Name = name;
        Signature = signature;
        CompressedSize = compressedSize;
        UncompressedSize = uncompressedSize;
    }
}
