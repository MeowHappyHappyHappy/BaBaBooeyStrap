using System.IO;
using BaBaBooeyStrap.Bootstrapper.Deployment;
using BaBaBooeyStrap.Bootstrapper.Mods;
using BaBaBooeyStrap.Core;
using BaBaBooeyStrap.Core.Logging;

namespace BaBaBooeyStrap.App.Services.Pipeline;

internal static class VersionInstallationValidator
{
    private static readonly HashSet<string> StudioOnlyPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "RobloxStudio.zip",
        "ApplicationConfig.zip",
        "BuiltInPlugins.zip",
        "BuiltInStandalonePlugins.zip",
        "content-qt_translations.zip",
        "content-api-docs.zip",
        "extracontent-scripts.zip",
        "Libraries.zip",
        "LibrariesQt5.zip",
        "Plugins.zip",
        "Qml.zip",
        "StudioFonts.zip"
    };

    private static readonly HashSet<string> PlayerOnlyPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "RobloxApp.zip",
        "WebView2.zip"
    };

    public static bool IsPackageApplicable(string packageName, bool isStudio)
    {
        if (string.Equals(packageName, "WebView2RuntimeInstaller.zip", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return isStudio
            ? !PlayerOnlyPackages.Contains(packageName)
            : !StudioOnlyPackages.Contains(packageName);
    }

    public static bool IsVersionInstallationComplete(string versionDir, bool isStudio)
    {
        if (string.IsNullOrWhiteSpace(versionDir) || !Directory.Exists(versionDir))
            return false;

        string exeName = isStudio ? "RobloxStudioBeta.exe" : "RobloxPlayerBeta.exe";
        if (!File.Exists(Path.Combine(versionDir, exeName)))
            return false;

        if (!isStudio)
        {
            string appSettingsPath = Path.Combine(versionDir, "AppSettings.xml");
            if (!File.Exists(Path.Combine(versionDir, "WebView2Loader.dll")) ||
                !File.Exists(appSettingsPath) ||
                !File.Exists(Path.Combine(versionDir, "ssl", "cacert.pem")) ||
                !Directory.Exists(Path.Combine(versionDir, "PlatformContent", "pc", "textures")) ||
                !Directory.Exists(Path.Combine(versionDir, "PlatformContent", "pc", "terrain")) ||
                !Directory.Exists(Path.Combine(versionDir, "PlatformContent", "pc", "fonts")) ||
                !Directory.Exists(Path.Combine(versionDir, "content", "configs")) ||
                !Directory.Exists(Path.Combine(versionDir, "content", "fonts")) ||
                !Directory.Exists(Path.Combine(versionDir, "content", "sky")) ||
                !Directory.Exists(Path.Combine(versionDir, "content", "sounds")) ||
                !Directory.Exists(Path.Combine(versionDir, "content", "textures")) ||
                !Directory.Exists(Path.Combine(versionDir, "ExtraContent", "LuaPackages")) ||
                !Directory.Exists(Path.Combine(versionDir, "ExtraContent", "translations")) ||
                !Directory.Exists(Path.Combine(versionDir, "shaders")))
            {
                return false;
            }

            try
            {
                var doc = System.Xml.Linq.XDocument.Load(appSettingsPath);
                string? contentFolder = doc.Root?.Element("ContentFolder")?.Value;
                string? baseUrl = doc.Root?.Element("BaseUrl")?.Value;

                if (string.IsNullOrWhiteSpace(contentFolder) ||
                    string.IsNullOrWhiteSpace(baseUrl) ||
                    !Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out Uri? baseUri) ||
                    (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps) ||
                    string.IsNullOrWhiteSpace(baseUri.Host))
                {
                    return false;
                }
            }
            catch
            {
                return false;
            }
        }

        return true;
    }

    public static string ResolveFallbackVersionDir(IPaths paths, bool isStudio)
    {
        string exeName = isStudio ? "RobloxStudioBeta.exe" : "RobloxPlayerBeta.exe";
        string? incompleteFallback = null;

        foreach (var root in new[] { paths.Versions, paths.RobloxVersions })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.GetDirectories(root).OrderByDescending(Directory.GetLastWriteTimeUtc))
            {
                if (IsVersionInstallationComplete(dir, isStudio))
                    return dir;

                if (incompleteFallback == null && File.Exists(Path.Combine(dir, exeName)))
                    incompleteFallback = dir;
            }
        }

        return incompleteFallback ?? string.Empty;
    }

    public static void EnsureAppSettingsXml(string versionDir, string robloxDomain, ILogger logger)
    {
        try
        {
            ModOverlayEngine.EnsureAppSettingsXml(versionDir, robloxDomain);
        }
        catch (Exception ex)
        {
            logger.Warn("VersionInstallationValidator", $"Failed to write AppSettings.xml: {ex.Message}");
        }
    }

    public static async Task<bool> TryRepairFromCachedDownloadsAsync(
        IPaths paths,
        string versionDir,
        bool isStudio,
        ILogger logger,
        IReadOnlyDictionary<string, string>? expectedHashes = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(versionDir) || !Directory.Exists(paths.Downloads))
        {
            return IsVersionInstallationComplete(versionDir, isStudio);
        }

        try
        {
            var extractor = new PackageExtractor();
            var verifier = new PackageVerifier();
            string versionKey = Path.GetFileName(Path.TrimEndingDirectorySeparator(versionDir));
            string versionDownloadsDir = !string.IsNullOrWhiteSpace(versionKey)
                ? Path.Combine(paths.Downloads, versionKey)
                : string.Empty;

            foreach (var (pkgName, relSubdir) in PackageExtractor.PackageDirectoryMap)
            {
                ct.ThrowIfCancellationRequested();
                if (!IsPackageApplicable(pkgName, isStudio))
                {
                    continue;
                }

                string? cachedZip = ResolveValidatedCachedZip(
                    pkgName,
                    versionDownloadsDir,
                    paths.Downloads,
                    expectedHashes,
                    verifier,
                    logger);

                if (cachedZip == null)
                {
                    continue;
                }

                try
                {
                    await extractor.ExtractPackageAsync(cachedZip, versionDir, relSubdir, null, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.Warn("VersionInstallationValidator", $"Cached repair skipped '{pkgName}': {ex.Message}");
                }
            }

            if (!isStudio)
            {
                string targetCacert = Path.Combine(versionDir, "ssl", "cacert.pem");
                string rootCacert = Path.Combine(versionDir, "cacert.pem");
                if (!File.Exists(targetCacert) && File.Exists(rootCacert))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(targetCacert)!);
                    File.Copy(rootCacert, targetCacert, overwrite: true);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Warn("VersionInstallationValidator", $"Cached repair failed: {ex.Message}");
        }

        return IsVersionInstallationComplete(versionDir, isStudio);
    }

    private static string? ResolveValidatedCachedZip(
        string pkgName,
        string versionDownloadsDir,
        string flatDownloadsDir,
        IReadOnlyDictionary<string, string>? expectedHashes,
        PackageVerifier verifier,
        ILogger logger)
    {
        string? expectedHash = null;
        bool hasExpectedHashes = expectedHashes != null && expectedHashes.Count > 0;
        if (hasExpectedHashes)
        {
            expectedHashes!.TryGetValue(pkgName, out expectedHash);
        }

        if (!string.IsNullOrEmpty(versionDownloadsDir))
        {
            string versionedZip = Path.Combine(versionDownloadsDir, pkgName);
            if (File.Exists(versionedZip))
            {
                if (!string.IsNullOrWhiteSpace(expectedHash) && !verifier.VerifyFileMd5(versionedZip, expectedHash))
                {
                    logger.Warn("VersionInstallationValidator", $"Cached package '{pkgName}' failed MD5 check; skipping repair.");
                    return null;
                }
                return versionedZip;
            }
        }

        string flatZip = Path.Combine(flatDownloadsDir, pkgName);
        if (!File.Exists(flatZip))
        {
            return null;
        }

        if (hasExpectedHashes)
        {
            if (string.IsNullOrWhiteSpace(expectedHash) || !verifier.VerifyFileMd5(flatZip, expectedHash))
            {
                logger.Warn("VersionInstallationValidator", $"Flat cached package '{pkgName}' does not match expected version hash; skipping.");
                return null;
            }
        }

        return flatZip;
    }

    public static void TryRecoverMissingWebView2Loader(
        IPaths paths,
        string versionDir,
        ILogger logger,
        IReadOnlyDictionary<string, string>? expectedHashes = null)
    {
        try
        {
            string targetDll = Path.Combine(versionDir, "WebView2Loader.dll");
            if (File.Exists(targetDll)) return;

            var verifier = new PackageVerifier();
            string versionKey = Path.GetFileName(Path.TrimEndingDirectorySeparator(versionDir));
            string versionDownloadsDir = !string.IsNullOrWhiteSpace(versionKey)
                ? Path.Combine(paths.Downloads, versionKey)
                : string.Empty;

            string? webViewZip = ResolveValidatedCachedZip(
                "WebView2.zip",
                versionDownloadsDir,
                paths.Downloads,
                expectedHashes,
                verifier,
                logger);

            if (webViewZip != null)
            {
                var extractor = new PackageExtractor();
                extractor.ExtractZipFile(webViewZip, versionDir);
                if (File.Exists(targetDll)) return;
            }

            foreach (var root in new[] { paths.Versions, paths.RobloxVersions })
            {
                if (!Directory.Exists(root)) continue;
                foreach (var dir in Directory.GetDirectories(root).OrderByDescending(Directory.GetLastWriteTimeUtc))
                {
                    string candidateDll = Path.Combine(dir, "WebView2Loader.dll");
                    if (File.Exists(candidateDll))
                    {
                        File.Copy(candidateDll, targetDll, overwrite: true);
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.Warn("VersionInstallationValidator", $"Failed to recover WebView2Loader.dll: {ex.Message}");
        }
    }
}
