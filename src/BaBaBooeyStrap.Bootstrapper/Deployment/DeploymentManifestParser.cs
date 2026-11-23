using System.Globalization;
using System.Text;
using BaBaBooeyStrap.Bootstrapper.Deployment.Abstractions;
using BaBaBooeyStrap.Bootstrapper.Deployment.Models;

namespace BaBaBooeyStrap.Bootstrapper.Deployment;

public class DeploymentManifestParser : IDeploymentManifestParser
{
    private const int MaxManifestBytes = 5 * 1024 * 1024;

    private static readonly HashSet<string> IgnoredPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "RobloxPlayerInstaller.exe",
        "RobloxPlayerLauncher.exe",
        "WebView2RuntimeInstaller.zip"
    };

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
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

        string[] lines = manifestContent.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries)
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
                // Incomplete trailing package record
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

            if (!IsSafePackageFileName(name))
            {
                throw new InvalidDataException($"Manifest contains unsafe package filename '{name}'.");
            }

            if (!IsMd5Hex(signature))
            {
                throw new InvalidDataException($"Manifest contains invalid MD5 signature '{lines[i + 1]}' for package '{name}'.");
            }

            if (!long.TryParse(line3, NumberStyles.None, CultureInfo.InvariantCulture, out long val1))
            {
                throw new InvalidDataException($"Manifest contains invalid size '{line3}' for package '{name}'.");
            }

            long val2 = 0;
            bool isQuad = i + 3 < lines.Length &&
                          !string.IsNullOrEmpty(lines[i + 3]) &&
                          long.TryParse(lines[i + 3], NumberStyles.None, CultureInfo.InvariantCulture, out val2) &&
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

    internal static bool IsSafePackageFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.Contains("..", StringComparison.Ordinal)) return false;
        if (name.IndexOfAny(['/', '\\', ':', '\0']) >= 0) return false;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        if (!string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal)) return false;

        int dotIdx = name.IndexOf('.');
        string stem = dotIdx >= 0 ? name[..dotIdx] : name;
        if (ReservedDeviceNames.Contains(stem)) return false;

        return true;
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
        ArgumentException.ThrowIfNullOrWhiteSpace(cdnBaseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(versionGuid);
        ArgumentNullException.ThrowIfNull(client);

        if (!Uri.TryCreate(cdnBaseUrl.Trim(), UriKind.Absolute, out var baseUri) ||
            !string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"CDN base URL must use HTTPS: '{cdnBaseUrl}'.");
        }

        if (!IsSafePackageFileName(versionGuid.Trim()))
        {
            throw new ArgumentException($"Invalid version GUID '{versionGuid}'.", nameof(versionGuid));
        }

        string baseUrl = cdnBaseUrl.Trim().TrimEnd('/');
        string safeChannel = string.IsNullOrWhiteSpace(channel) ? "production" : channel.Trim();
        string channelPart = safeChannel.Equals("production", StringComparison.OrdinalIgnoreCase) ||
                             safeChannel.Equals("live", StringComparison.OrdinalIgnoreCase)
                             ? string.Empty
                             : $"/channel/{Uri.EscapeDataString(safeChannel.ToLowerInvariant())}";

        string manifestUrl = $"{baseUrl}{channelPart}{cdnExtension}/{Uri.EscapeDataString(versionGuid.Trim())}-rbxPkgManifest.txt";
        using var response = await client.GetAsync(manifestUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is > MaxManifestBytes)
        {
            throw new InvalidDataException($"Manifest exceeds maximum allowed size ({MaxManifestBytes} bytes).");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        byte[] buffer = new byte[16384];
        int total = 0;
        while (true)
        {
            int read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read <= 0) break;
            total += read;
            if (total > MaxManifestBytes)
            {
                throw new InvalidDataException($"Manifest exceeds maximum allowed size ({MaxManifestBytes} bytes).");
            }
            ms.Write(buffer, 0, read);
        }

        string manifestContent = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
        return Parse(manifestContent);
    }
}
