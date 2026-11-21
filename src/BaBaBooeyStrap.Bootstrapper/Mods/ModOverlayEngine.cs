using System.Security.Cryptography;
using BaBaBooeyStrap.Bootstrapper.Deployment;

namespace BaBaBooeyStrap.Bootstrapper.Mods;

public record ModApplyResult(
    bool Success,
    int AppliedCount,
    int RestoredCount,
    IReadOnlyList<string> ManifestedFiles,
    string? ErrorMessage
);

public interface IModOverlayEngine
{
    Task<ModApplyResult> ApplyModificationsAsync(
        string modificationsFolder,
        string versionFolder,
        IReadOnlyList<string>? previousManifest = null,
        Func<string, string, Task>? packageFileRestorer = null,
        string robloxDomain = "www.roblox.com",
        CancellationToken ct = default);

    Task CleanModificationsAsync(
        string versionFolder,
        IEnumerable<string> manifestedFiles,
        CancellationToken ct = default);
}

public class ModOverlayEngine : IModOverlayEngine
{
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public async Task<ModApplyResult> ApplyModificationsAsync(
        string modificationsFolder,
        string versionFolder,
        IReadOnlyList<string>? previousManifest = null,
        Func<string, string, Task>? packageFileRestorer = null,
        string robloxDomain = "www.roblox.com",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(modificationsFolder) || !Directory.Exists(modificationsFolder))
        {
            return new ModApplyResult(true, 0, 0, Array.Empty<string>(), null);
        }

        Directory.CreateDirectory(versionFolder);
        var currentManifestedFiles = new List<string>();
        int appliedCount = 0;
        int restoredCount = 0;

        try
        {
            var modDir = new DirectoryInfo(modificationsFolder);
            var files = modDir.GetFiles("*", SearchOption.AllDirectories);

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();

                // 1. Exclude README.txt and .lock files
                if (file.Name.Equals("README.txt", StringComparison.OrdinalIgnoreCase) ||
                    file.Extension.Equals(".lock", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // 2. Exclude reserved device names
                string baseName = Path.GetFileNameWithoutExtension(file.Name).ToUpperInvariant();
                if (ReservedDeviceNames.Contains(baseName))
                {
                    continue;
                }

                string relativePath = Path.GetRelativePath(modificationsFolder, file.FullName);
                string normalizedRel = relativePath.Replace('\\', '/');

                // 3. Exclude dangerous avatar mesh files
                if (IsDangerousAvatarMesh(normalizedRel))
                {
                    continue;
                }

                string destinationPath = Path.Combine(versionFolder, relativePath);
                string? destDir = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }

                // 4. Assert ReadOnly attribute removed before copying
                if (File.Exists(destinationPath))
                {
                    var attr = File.GetAttributes(destinationPath);
                    if (attr.HasFlag(FileAttributes.ReadOnly))
                    {
                        File.SetAttributes(destinationPath, attr & ~FileAttributes.ReadOnly);
                    }

                    // 5. MD5 check to skip identical redundant writes
                    if (ComputeFileMd5(file.FullName) == ComputeFileMd5(destinationPath))
                    {
                        currentManifestedFiles.Add(normalizedRel);
                        continue;
                    }
                }

                file.CopyTo(destinationPath, overwrite: true);
                currentManifestedFiles.Add(normalizedRel);
                appliedCount++;
            }

            // 6. Generate default AppSettings.xml if absent in version directory
            EnsureAppSettingsXml(versionFolder, robloxDomain);

            // 7. Mod Manifest Reconciliation: detect deleted mods
            if (previousManifest != null && previousManifest.Count > 0)
            {
                var removedFiles = previousManifest.Except(currentManifestedFiles, StringComparer.OrdinalIgnoreCase).ToList();
                foreach (string removed in removedFiles)
                {
                    ct.ThrowIfCancellationRequested();
                    string targetFile = Path.Combine(versionFolder, removed.Replace('/', Path.DirectorySeparatorChar));

                    bool belongsToPackage = TryMapToPackage(removed, out string? packageName, out string? packageRelativeFile);
                    if (belongsToPackage && packageFileRestorer != null && packageName != null && packageRelativeFile != null)
                    {
                        await packageFileRestorer(packageName, packageRelativeFile);
                        restoredCount++;
                    }
                    else
                    {
                        if (File.Exists(targetFile))
                        {
                            try
                            {
                                var attr = File.GetAttributes(targetFile);
                                if (attr.HasFlag(FileAttributes.ReadOnly))
                                {
                                    File.SetAttributes(targetFile, attr & ~FileAttributes.ReadOnly);
                                }
                                File.Delete(targetFile);
                                restoredCount++;
                            }
                            catch { }
                        }
                    }
                }
            }

            return new ModApplyResult(true, appliedCount, restoredCount, currentManifestedFiles, null);
        }
        catch (Exception ex)
        {
            return new ModApplyResult(false, appliedCount, restoredCount, currentManifestedFiles, ex.Message);
        }
    }

    public Task CleanModificationsAsync(
        string versionFolder,
        IEnumerable<string> manifestedFiles,
        CancellationToken ct = default)
    {
        CleanModifications(versionFolder, manifestedFiles);
        return Task.CompletedTask;
    }

    public static List<string> ApplyModifications(string modificationsFolder, string versionFolder)
    {
        var manifested = new List<string>();
        if (!Directory.Exists(modificationsFolder)) return manifested;

        Directory.CreateDirectory(versionFolder);
        var modDir = new DirectoryInfo(modificationsFolder);
        var files = modDir.GetFiles("*", SearchOption.AllDirectories);

        foreach (var file in files)
        {
            if (file.Name.Equals("README.txt", StringComparison.OrdinalIgnoreCase) ||
                file.Extension.Equals(".lock", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string baseName = Path.GetFileNameWithoutExtension(file.Name).ToUpperInvariant();
            if (ReservedDeviceNames.Contains(baseName))
            {
                continue;
            }

            string relativePath = Path.GetRelativePath(modificationsFolder, file.FullName);
            string normalizedRel = relativePath.Replace('\\', '/');

            if (IsDangerousAvatarMesh(normalizedRel))
            {
                continue;
            }

            string destinationPath = Path.Combine(versionFolder, relativePath);
            string? destDir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            if (File.Exists(destinationPath))
            {
                var attr = File.GetAttributes(destinationPath);
                if (attr.HasFlag(FileAttributes.ReadOnly))
                {
                    File.SetAttributes(destinationPath, attr & ~FileAttributes.ReadOnly);
                }
            }

            file.CopyTo(destinationPath, overwrite: true);
            manifested.Add(normalizedRel);
        }

        return manifested;
    }

    public static void CleanModifications(string versionFolder, IEnumerable<string> manifestedFiles)
    {
        if (!Directory.Exists(versionFolder)) return;

        foreach (var relPath in manifestedFiles)
        {
            string targetPath = Path.Combine(versionFolder, relPath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(targetPath))
            {
                try
                {
                    var attr = File.GetAttributes(targetPath);
                    if (attr.HasFlag(FileAttributes.ReadOnly))
                    {
                        File.SetAttributes(targetPath, attr & ~FileAttributes.ReadOnly);
                    }
                    File.Delete(targetPath);
                }
                catch { }
            }
        }
    }

    private static bool IsDangerousAvatarMesh(string relativePath)
    {
        if (!relativePath.EndsWith(".mesh", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string lower = relativePath.ToLowerInvariant();
        return lower.StartsWith("content/avatar/heads/") ||
               lower.StartsWith("content/avatar/compositing/") ||
               lower.StartsWith("content/avatar/meshes/");
    }

    public static void EnsureAppSettingsXml(string versionFolder, string robloxDomain = "www.roblox.com")
    {
        string domain = (robloxDomain ?? string.Empty).Trim();
        if (domain.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            domain = domain["https://".Length..];
        else if (domain.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            domain = domain["http://".Length..];
        domain = domain.TrimEnd('/');

        if (string.IsNullOrWhiteSpace(domain) || domain.Equals("roblox.com", StringComparison.OrdinalIgnoreCase))
        {
            domain = "www.roblox.com";
        }

        string appSettingsPath = Path.Combine(versionFolder, "AppSettings.xml");
        string xml = $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <Settings>
            <ContentFolder>content</ContentFolder>
            <BaseUrl>http://{domain}</BaseUrl>
        </Settings>
        """;

        File.WriteAllText(appSettingsPath, xml, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string ComputeFileMd5(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            byte[] hash = MD5.HashData(stream);
            return Convert.ToHexString(hash);
        }
        catch
        {
            return string.Empty;
        }
    }

    public static bool TryMapToPackage(string relativePath, out string? packageName, out string? relativeSubpath)
    {
        string norm = relativePath.Replace('/', '\\');
        packageName = null;
        relativeSubpath = null;

        foreach (var (pkg, prefix) in PackageExtractor.PackageDirectoryMap)
        {
            if (string.IsNullOrEmpty(prefix)) continue;

            if (norm.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                packageName = pkg;
                relativeSubpath = norm.Substring(prefix.Length);
                return true;
            }
        }

        return false;
    }
}
