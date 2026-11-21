using System.IO;
using BaBaBooeyStrap.Bootstrapper.Deployment;
using BaBaBooeyStrap.Bootstrapper.Mods;
using BaBaBooeyStrap.Core;
using BaBaBooeyStrap.Core.Logging;

namespace BaBaBooeyStrap.App.Services.Pipeline;

internal static class VersionInstallationValidator
{
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
                if (!File.ReadAllText(appSettingsPath).Contains("http://www.", StringComparison.OrdinalIgnoreCase))
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

    public static void TryRepairFromCachedDownloads(IPaths paths, string versionDir, bool isStudio, ILogger logger)
    {
        if (isStudio || !Directory.Exists(paths.Downloads)) return;

        try
        {
            var extractor = new PackageExtractor();
            foreach (var (pkgName, relSubdir) in PackageExtractor.PackageDirectoryMap)
            {
                if (string.Equals(pkgName, "WebView2RuntimeInstaller.zip", StringComparison.OrdinalIgnoreCase))
                    continue;

                string cachedZip = Path.Combine(paths.Downloads, pkgName);
                if (!File.Exists(cachedZip))
                    continue;

                try
                {
                    extractor.ExtractPackageAsync(cachedZip, versionDir, relSubdir, null, CancellationToken.None)
                        .GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    logger.Warn("VersionInstallationValidator", $"Cached repair skipped '{pkgName}': {ex.Message}");
                }
            }

            string targetCacert = Path.Combine(versionDir, "ssl", "cacert.pem");
            string rootCacert = Path.Combine(versionDir, "cacert.pem");
            if (!File.Exists(targetCacert) && File.Exists(rootCacert))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(targetCacert)!);
                File.Copy(rootCacert, targetCacert, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            logger.Warn("VersionInstallationValidator", $"Cached repair failed: {ex.Message}");
        }
    }

    public static void TryRecoverMissingWebView2Loader(IPaths paths, string versionDir, ILogger logger)
    {
        try
        {
            string targetDll = Path.Combine(versionDir, "WebView2Loader.dll");
            if (File.Exists(targetDll)) return;

            string webViewZip = Path.Combine(paths.Downloads, "WebView2.zip");
            if (File.Exists(webViewZip))
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
