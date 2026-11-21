using BaBaBooeyStrap.Bootstrapper.Deployment.Abstractions;
using BaBaBooeyStrap.Bootstrapper.Deployment.Models;

namespace BaBaBooeyStrap.Bootstrapper.Deployment;

public class DeploymentManifestParser : IDeploymentManifestParser
{
    private static readonly HashSet<string> IgnoredPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "RobloxPlayerInstaller.exe",
        "RobloxPlayerLauncher.exe",
        "WebView2RuntimeInstaller.zip"
    };

    public IReadOnlyList<PackageRecord> Parse(string manifestContent)
    {
        return ParseManifest(manifestContent);
    }

    public static IReadOnlyList<PackageRecord> ParseManifest(string manifestContent)
    {
        if (string.IsNullOrWhiteSpace(manifestContent))
        {
            throw new FormatException("Manifest content is empty.");
        }

        string[] lines = manifestContent.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                                        .Select(l => l.Trim())
                                        .Where(l => !string.IsNullOrEmpty(l))
                                        .ToArray();

        if (lines.Length == 0 || lines[0] != "v0")
        {
            throw new InvalidDataException("Invalid manifest header. Expected 'v0'.");
        }

        var packages = new List<PackageRecord>();
        int i = 1;

        while (i < lines.Length)
        {
            if (i + 2 >= lines.Length)
            {
                // Incomplete package record triplet/quad
                break;
            }

            string name = lines[i];
            string signature = lines[i + 1].ToLowerInvariant();
            string line3 = lines[i + 2];

            if (string.IsNullOrEmpty(name) ||
                string.IsNullOrEmpty(signature) ||
                string.IsNullOrEmpty(line3))
            {
                break;
            }

            if (!long.TryParse(line3, out long val1)) val1 = 0;

            long val2 = 0;
            bool isQuad = i + 3 < lines.Length &&
                          !string.IsNullOrEmpty(lines[i + 3]) &&
                          long.TryParse(lines[i + 3], out val2) &&
                          !(i + 4 < lines.Length && IsMd5Hex(lines[i + 4]));

            long compressedSize;
            long uncompressedSize;
            if (isQuad)
            {
                compressedSize = Math.Min(val1, val2);
                uncompressedSize = Math.Max(val1, val2);
                i += 4;
            }
            else
            {
                compressedSize = val1;
                uncompressedSize = val1;
                i += 3;
            }

            if (!IgnoredPackages.Contains(name))
            {
                packages.Add(new PackageRecord(name, signature, compressedSize, uncompressedSize));
            }
        }

        return packages;
    }

    private static bool IsMd5Hex(string value) =>
        value.Length == 32 && value.All(char.IsAsciiHexDigit);

    public async Task<IReadOnlyList<PackageRecord>> FetchAndParseAsync(
        string cdnBaseUrl,
        string versionGuid,
        HttpClient client,
        string channel = "production",
        string cdnExtension = "",
        CancellationToken ct = default)
    {
        string baseUrl = cdnBaseUrl.TrimEnd('/');
        string channelPart = channel.Equals("production", StringComparison.OrdinalIgnoreCase) ||
                             channel.Equals("live", StringComparison.OrdinalIgnoreCase)
                             ? string.Empty
                             : $"/channel/{channel.ToLowerInvariant()}";

        string manifestUrl = $"{baseUrl}{channelPart}{cdnExtension}/{versionGuid}-rbxPkgManifest.txt";
        string manifestContent = await client.GetStringAsync(manifestUrl, ct);
        return Parse(manifestContent);
    }
}
