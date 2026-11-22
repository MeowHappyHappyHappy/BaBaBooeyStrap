using BaBaBooeyStrap.Bootstrapper.Deployment.Models;

namespace BaBaBooeyStrap.Bootstrapper.Deployment.Abstractions;

public interface IPackageDownloader
{
    Task<IReadOnlyList<string>> DownloadPackagesAsync(
        HttpClient client,
        string cdnBaseUrl,
        string versionGuid,
        IEnumerable<PackageRecord> packages,
        string destinationFolder,
        IProgress<PackageDownloadProgress>? progress = null,
        int maxDegreeOfParallelism = 5,
        CancellationToken ct = default);

    Task<IReadOnlyList<string>> DownloadPackagesAsync(
        HttpClient client,
        string cdnBaseUrl,
        string versionGuid,
        IEnumerable<PackageRecord> packages,
        string destinationFolder,
        IProgress<PackageDownloadProgress>? progress,
        int maxDegreeOfParallelism,
        string channel,
        CancellationToken ct = default)
    {
        return DownloadPackagesAsync(client, cdnBaseUrl, versionGuid, packages, destinationFolder, progress, maxDegreeOfParallelism, ct);
    }
}
