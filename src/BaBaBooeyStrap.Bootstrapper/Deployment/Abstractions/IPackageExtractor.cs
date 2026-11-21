namespace BaBaBooeyStrap.Bootstrapper.Deployment.Abstractions;

public interface IPackageExtractor
{
    void ExtractZip(byte[] zipBytes, string targetDirectory);
    void ExtractZipFile(string zipFilePath, string targetDirectory, string? fileFilter = null);
    Task ExtractPackageAsync(
        string packageZipPath,
        string targetVersionDirectory,
        string relativeSubdirectory,
        IEnumerable<string>? specificFiles = null,
        CancellationToken ct = default);
}
