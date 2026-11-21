using BaBaBooeyStrap.Bootstrapper.Deployment.Models;

namespace BaBaBooeyStrap.Bootstrapper.Deployment.Abstractions;

public interface IDeploymentManifestParser
{
    IReadOnlyList<PackageRecord> Parse(string manifestContent);

    Task<IReadOnlyList<PackageRecord>> FetchAndParseAsync(
        string cdnBaseUrl,
        string versionGuid,
        HttpClient client,
        string channel = "production",
        string cdnExtension = "",
        CancellationToken ct = default);
}
