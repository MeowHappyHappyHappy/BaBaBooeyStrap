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

    async Task<int> ExtractPackageWithCountAsync(
        string packageZipPath,
        string targetVersionDirectory,
        string relativeSubdirectory,
        IEnumerable<string>? specificFiles = null,
        CancellationToken ct = default)
    {
        await ExtractPackageAsync(packageZipPath, targetVersionDirectory, relativeSubdirectory, specificFiles, ct)
            .ConfigureAwait(false);
        return 0;
    }
}
