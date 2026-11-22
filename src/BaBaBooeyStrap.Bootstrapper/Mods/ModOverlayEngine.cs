using System.Security.Cryptography;
using BaBaBooeyStrap.Bootstrapper.Deployment;

namespace BaBaBooeyStrap.Bootstrapper.Mods;

public enum PackageFileRestoreOutcome
{
    Restored,
    NotInPackage,
    CacheUnavailable
}

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
    private const string BackupsFolderName = ".backups";

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public Task<ModApplyResult> ApplyModificationsAsync(
        string modificationsFolder,
        string versionFolder,
        IReadOnlyList<string>? previousManifest = null,
        Func<string, string, Task>? packageFileRestorer = null,
        string robloxDomain = "www.roblox.com",
        CancellationToken ct = default)
    {
        return ApplyModificationsCoreAsync(
            modificationsFolder,
            versionFolder,
            previousManifest,
            packageFileRestorer,
            packageFileRestorerWithOutcome: null,
            excludeClientSettings: false,
            robloxDomain,
            ct);
    }

    public Task<ModApplyResult> ApplyModificationsAsync(
        string modificationsFolder,
        string versionFolder,
        IReadOnlyList<string>? previousManifest,
        Func<string, string, Task<PackageFileRestoreOutcome>>? packageFileRestorerWithOutcome,
        bool excludeClientSettings = false,
        string robloxDomain = "www.roblox.com",
        CancellationToken ct = default)
    {
        return ApplyModificationsCoreAsync(
            modificationsFolder,
            versionFolder,
            previousManifest,
            packageFileRestorer: null,
            packageFileRestorerWithOutcome,
            excludeClientSettings,
            robloxDomain,
            ct);
    }

    private async Task<ModApplyResult> ApplyModificationsCoreAsync(
        string modificationsFolder,
        string versionFolder,
        IReadOnlyList<string>? previousManifest,
        Func<string, string, Task>? packageFileRestorer,
        Func<string, string, Task<PackageFileRestoreOutcome>>? packageFileRestorerWithOutcome,
        bool excludeClientSettings,
        string robloxDomain,
        CancellationToken ct)
    {
        bool hasModificationsDir = !string.IsNullOrWhiteSpace(modificationsFolder) && Directory.Exists(modificationsFolder);
        bool hasPreviousManifest = previousManifest != null && previousManifest.Count > 0;

        if (!hasModificationsDir && (!hasPreviousManifest || !Directory.Exists(versionFolder)))
        {
            return new ModApplyResult(true, 0, 0, Array.Empty<string>(), null);
        }

        Directory.CreateDirectory(versionFolder);
        var currentManifestedFiles = new List<string>();
        int appliedCount = 0;
        int restoredCount = 0;
        string versionFolderKey = GetVersionBackupKey(versionFolder);
        var previousSet = hasPreviousManifest
            ? new HashSet<string>(previousManifest!, StringComparer.OrdinalIgnoreCase)
            : null;

        try
        {
            if (hasModificationsDir)
            {
                var modDir = new DirectoryInfo(modificationsFolder);
                var files = modDir.GetFiles("*", SearchOption.AllDirectories);

                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();

                    string relativePath = Path.GetRelativePath(modificationsFolder, file.FullName);
                    string normalizedRel = relativePath.Replace('\\', '/');

                    // 0. Exclude internal mod backup cache and (optionally) ClientSettings owned by FastFlagManager
                    if (normalizedRel.StartsWith(BackupsFolderName + "/", StringComparison.OrdinalIgnoreCase) ||
                        normalizedRel.Equals(BackupsFolderName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (excludeClientSettings &&
                        normalizedRel.Equals("ClientSettings/ClientAppSettings.json", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

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

                        // Backup original file before overwriting if not already manifested by a previous mod run
                        if ((previousSet == null || !previousSet.Contains(normalizedRel)) &&
                            !normalizedRel.Equals("AppSettings.xml", StringComparison.OrdinalIgnoreCase))
                        {
                            TryBackupOriginalFile(modificationsFolder, versionFolderKey, relativePath, destinationPath);
                        }
                    }

                    file.CopyTo(destinationPath, overwrite: true);
                    currentManifestedFiles.Add(normalizedRel);
                    appliedCount++;
                }
            }

            // 6. Generate default AppSettings.xml if absent in version directory
            EnsureAppSettingsXml(versionFolder, robloxDomain);

            // 7. Mod Manifest Reconciliation: detect deleted mods
            if (hasPreviousManifest)
            {
                var removedFiles = previousManifest!.Except(currentManifestedFiles, StringComparer.OrdinalIgnoreCase).ToList();
                foreach (string removed in removedFiles)
                {
                    ct.ThrowIfCancellationRequested();

                    if (excludeClientSettings &&
                        removed.Equals("ClientSettings/ClientAppSettings.json", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string relNative = removed.Replace('/', Path.DirectorySeparatorChar);
                    string targetFile = Path.Combine(versionFolder, relNative);

                    if (!string.IsNullOrWhiteSpace(modificationsFolder) &&
                        TryRestoreFromBackup(modificationsFolder, versionFolderKey, relNative, targetFile))
                    {
                        restoredCount++;
                        continue;
                    }

                    bool belongsToPackage = TryMapToPackage(removed, out string? packageName, out string? packageRelativeFile);
                    if (belongsToPackage && packageFileRestorerWithOutcome != null && packageName != null && packageRelativeFile != null)
                    {
                        var outcome = await packageFileRestorerWithOutcome(packageName, packageRelativeFile).ConfigureAwait(false);
                        if (outcome == PackageFileRestoreOutcome.Restored)
                        {
                            restoredCount++;
                        }
                        else if (outcome == PackageFileRestoreOutcome.NotInPackage)
                        {
                            if (DeleteTargetFileIfExists(targetFile))
                            {
                                restoredCount++;
                            }
                        }
                    }
                    else if (belongsToPackage && packageFileRestorer != null && packageName != null && packageRelativeFile != null)
                    {
                        await packageFileRestorer(packageName, packageRelativeFile).ConfigureAwait(false);
                        restoredCount++;
                    }
                    else
                    {
                        if (DeleteTargetFileIfExists(targetFile))
                        {
                            restoredCount++;
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

    private static string GetVersionBackupKey(string versionFolder)
    {
        string trimmed = Path.TrimEndingDirectorySeparator(versionFolder);
        string key = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(key) ? "default" : key;
    }

    private static void TryBackupOriginalFile(
        string modificationsFolder,
        string versionFolderKey,
        string relativePath,
        string originalTargetFile)
    {
        try
        {
            string backupPath = Path.Combine(modificationsFolder, BackupsFolderName, versionFolderKey, relativePath);
            if (File.Exists(backupPath)) return;

            string? backupDir = Path.GetDirectoryName(backupPath);
            if (!string.IsNullOrEmpty(backupDir))
            {
                Directory.CreateDirectory(backupDir);
            }

            File.Copy(originalTargetFile, backupPath, overwrite: false);
        }
        catch
        {
            // Backup is best-effort; package cache fallback still applies
        }
    }

    private static bool TryRestoreFromBackup(
        string modificationsFolder,
        string versionFolderKey,
        string relativeNativePath,
        string targetFile)
    {
        try
        {
            string backupPath = Path.Combine(modificationsFolder, BackupsFolderName, versionFolderKey, relativeNativePath);
            if (!File.Exists(backupPath)) return false;

            string? targetDir = Path.GetDirectoryName(targetFile);
            if (!string.IsNullOrEmpty(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            if (File.Exists(targetFile))
            {
                var attr = File.GetAttributes(targetFile);
                if (attr.HasFlag(FileAttributes.ReadOnly))
                {
                    File.SetAttributes(targetFile, attr & ~FileAttributes.ReadOnly);
                }
            }

            File.Copy(backupPath, targetFile, overwrite: true);
            File.Delete(backupPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool DeleteTargetFileIfExists(string targetFile)
    {
        if (!File.Exists(targetFile)) return false;

        try
        {
            var attr = File.GetAttributes(targetFile);
            if (attr.HasFlag(FileAttributes.ReadOnly))
            {
                File.SetAttributes(targetFile, attr & ~FileAttributes.ReadOnly);
            }
            File.Delete(targetFile);
            return true;
        }
        catch
        {
            return false;
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
