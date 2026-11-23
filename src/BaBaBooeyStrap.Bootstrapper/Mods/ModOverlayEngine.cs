using System.Security;
using System.Security.Cryptography;
using System.Xml.Linq;
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

        string fullModificationsDir = string.IsNullOrWhiteSpace(modificationsFolder)
            ? string.Empty
            : Path.GetFullPath(modificationsFolder);
        string fullVersionDir = Path.GetFullPath(versionFolder);

        if (HasReparsePointInPath(fullVersionDir) ||
            (!string.IsNullOrEmpty(fullModificationsDir) && HasReparsePointInPath(fullModificationsDir)))
        {
            return new ModApplyResult(false, 0, 0, Array.Empty<string>(), "Refused mod overlay on reparse point / symlink path.");
        }

        Directory.CreateDirectory(fullVersionDir);
        var currentManifestedFiles = new List<string>();
        int appliedCount = 0;
        int restoredCount = 0;
        string versionFolderKey = GetVersionBackupKey(fullVersionDir);
        var previousSet = hasPreviousManifest
            ? new HashSet<string>(previousManifest!, StringComparer.OrdinalIgnoreCase)
            : null;

        try
        {
            if (hasModificationsDir)
            {
                var modDir = new DirectoryInfo(fullModificationsDir);
                var enumOptions = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    AttributesToSkip = FileAttributes.ReparsePoint
                };
                var files = modDir.GetFiles("*", enumOptions);

                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();

                    if (file.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                        HasReparsePointAncestor(fullModificationsDir, file.FullName))
                    {
                        continue;
                    }

                    string relativePath = Path.GetRelativePath(fullModificationsDir, file.FullName);
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

                    // 2. Exclude reserved device names (Windows treats everything before the first dot as the device stem)
                    if (IsReservedDeviceFileName(file.Name))
                    {
                        continue;
                    }

                    // 3. Exclude dangerous avatar mesh files
                    if (IsDangerousAvatarMesh(normalizedRel))
                    {
                        continue;
                    }

                    if (!TryResolveContainedPath(fullVersionDir, relativePath, out string destinationPath) ||
                        HasReparsePointAncestor(fullVersionDir, destinationPath))
                    {
                        continue;
                    }

                    string? destDir = Path.GetDirectoryName(destinationPath);
                    if (!string.IsNullOrEmpty(destDir))
                    {
                        Directory.CreateDirectory(destDir);
                    }

                    // 4. Assert ReadOnly attribute removed before copying (and refuse reparse point targets)
                    if (File.Exists(destinationPath))
                    {
                        var attr = File.GetAttributes(destinationPath);
                        if (attr.HasFlag(FileAttributes.ReparsePoint))
                        {
                            continue;
                        }
                        if (attr.HasFlag(FileAttributes.ReadOnly))
                        {
                            File.SetAttributes(destinationPath, attr & ~FileAttributes.ReadOnly);
                        }

                        // 5. MD5 check to skip identical redundant writes (null never matches null)
                        string? srcHash = ComputeFileMd5(file.FullName);
                        string? dstHash = ComputeFileMd5(destinationPath);
                        if (srcHash != null && srcHash == dstHash)
                        {
                            currentManifestedFiles.Add(normalizedRel);
                            continue;
                        }

                        // Backup original file before overwriting if not already manifested by a previous mod run
                        if ((previousSet == null || !previousSet.Contains(normalizedRel)) &&
                            !normalizedRel.Equals("AppSettings.xml", StringComparison.OrdinalIgnoreCase))
                        {
                            TryBackupOriginalFile(fullModificationsDir, versionFolderKey, relativePath, destinationPath);
                        }
                    }

                    file.CopyTo(destinationPath, overwrite: true);
                    currentManifestedFiles.Add(normalizedRel);
                    appliedCount++;
                }
            }

            // 6. Generate default AppSettings.xml in version directory
            EnsureAppSettingsXml(fullVersionDir, robloxDomain);

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
                    if (!TryResolveContainedPath(fullVersionDir, relNative, out string targetFile) ||
                        HasReparsePointAncestor(fullVersionDir, targetFile))
                    {
                        continue;
                    }

                    if (!string.IsNullOrEmpty(fullModificationsDir) &&
                        TryRestoreFromBackup(fullModificationsDir, versionFolderKey, relNative, targetFile))
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
                        else if (outcome == PackageFileRestoreOutcome.NotInPackage && !IsProtectedCoreRootFile(removed))
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
                    else if (!IsProtectedCoreRootFile(removed))
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
        catch (OperationCanceledException)
        {
            throw;
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
            string backupRoot = Path.Combine(modificationsFolder, BackupsFolderName, versionFolderKey);
            if (!TryResolveContainedPath(backupRoot, relativePath, out string backupPath) ||
                File.Exists(backupPath))
            {
                return;
            }

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
            string backupRoot = Path.Combine(modificationsFolder, BackupsFolderName, versionFolderKey);
            if (!TryResolveContainedPath(backupRoot, relativeNativePath, out string backupPath) ||
                !File.Exists(backupPath))
            {
                return false;
            }

            if (File.GetAttributes(backupPath).HasFlag(FileAttributes.ReparsePoint))
            {
                return false;
            }

            string? targetDir = Path.GetDirectoryName(targetFile);
            if (!string.IsNullOrEmpty(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            if (File.Exists(targetFile))
            {
                var attr = File.GetAttributes(targetFile);
                if (attr.HasFlag(FileAttributes.ReparsePoint))
                {
                    return false;
                }
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
            if (attr.HasFlag(FileAttributes.ReparsePoint))
            {
                return false;
            }
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
        ct.ThrowIfCancellationRequested();
        CleanModifications(versionFolder, manifestedFiles);
        return Task.CompletedTask;
    }

    public static List<string> ApplyModifications(string modificationsFolder, string versionFolder)
    {
        var manifested = new List<string>();
        if (string.IsNullOrWhiteSpace(modificationsFolder) || !Directory.Exists(modificationsFolder)) return manifested;

        string fullMods = Path.GetFullPath(modificationsFolder);
        string fullVersion = Path.GetFullPath(versionFolder);
        if (HasReparsePointInPath(fullMods) || HasReparsePointInPath(fullVersion)) return manifested;

        Directory.CreateDirectory(fullVersion);
        var modDir = new DirectoryInfo(fullMods);
        var enumOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        var files = modDir.GetFiles("*", enumOptions);

        foreach (var file in files)
        {
            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                HasReparsePointAncestor(fullMods, file.FullName))
            {
                continue;
            }

            string relativePath = Path.GetRelativePath(fullMods, file.FullName);
            string normalizedRel = relativePath.Replace('\\', '/');

            if (normalizedRel.StartsWith(BackupsFolderName + "/", StringComparison.OrdinalIgnoreCase) ||
                normalizedRel.Equals(BackupsFolderName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (file.Name.Equals("README.txt", StringComparison.OrdinalIgnoreCase) ||
                file.Extension.Equals(".lock", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (IsReservedDeviceFileName(file.Name))
            {
                continue;
            }

            if (IsDangerousAvatarMesh(normalizedRel))
            {
                continue;
            }

            if (!TryResolveContainedPath(fullVersion, relativePath, out string destinationPath) ||
                HasReparsePointAncestor(fullVersion, destinationPath))
            {
                continue;
            }

            string? destDir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            if (File.Exists(destinationPath))
            {
                var attr = File.GetAttributes(destinationPath);
                if (attr.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }
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
        if (string.IsNullOrWhiteSpace(versionFolder) || !Directory.Exists(versionFolder)) return;

        string fullVersion = Path.GetFullPath(versionFolder);
        if (HasReparsePointInPath(fullVersion)) return;

        foreach (var relPath in manifestedFiles)
        {
            if (string.IsNullOrWhiteSpace(relPath)) continue;
            string relNative = relPath.Replace('/', Path.DirectorySeparatorChar);
            if (!TryResolveContainedPath(fullVersion, relNative, out string targetPath) ||
                HasReparsePointAncestor(fullVersion, targetPath))
            {
                continue;
            }

            DeleteTargetFileIfExists(targetPath);
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

        if (string.IsNullOrWhiteSpace(domain) ||
            domain.Equals("roblox.com", StringComparison.OrdinalIgnoreCase) ||
            Uri.CheckHostName(domain) == UriHostNameType.Unknown ||
            domain.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '<' or '>' or '&' or '"' or '\'' or '/' or '\\'))
        {
            domain = "www.roblox.com";
        }

        string appSettingsPath = Path.Combine(versionFolder, "AppSettings.xml");
        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement("Settings",
                new XElement("ContentFolder", "content"),
                new XElement("BaseUrl", $"http://{domain}")));

        if (File.Exists(appSettingsPath))
        {
            var attrs = File.GetAttributes(appSettingsPath);
            if (attrs.HasFlag(FileAttributes.ReadOnly))
            {
                File.SetAttributes(appSettingsPath, attrs & ~FileAttributes.ReadOnly);
            }
        }

        string tempPath = $"{appSettingsPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            doc.Save(tempPath);
            File.Move(tempPath, appSettingsPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }
    }

    private static string? ComputeFileMd5(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            byte[] hash = MD5.HashData(stream);
            return Convert.ToHexString(hash);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsReservedDeviceFileName(string fileName)
    {
        int dotIdx = fileName.IndexOf('.');
        string stem = (dotIdx >= 0 ? fileName[..dotIdx] : fileName).ToUpperInvariant();
        return ReservedDeviceNames.Contains(stem);
    }

    private static bool IsProtectedCoreRootFile(string normalizedRelativePath)
    {
        if (normalizedRelativePath.Contains('/', StringComparison.Ordinal)) return false;
        return normalizedRelativePath.Equals("RobloxPlayerBeta.exe", StringComparison.OrdinalIgnoreCase) ||
               normalizedRelativePath.Equals("RobloxStudioBeta.exe", StringComparison.OrdinalIgnoreCase) ||
               normalizedRelativePath.Equals("WebView2Loader.dll", StringComparison.OrdinalIgnoreCase) ||
               normalizedRelativePath.Equals("AppSettings.xml", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool TryResolveContainedPath(string baseDir, string relativePath, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(baseDir) || string.IsNullOrWhiteSpace(relativePath)) return false;
        if (Path.IsPathRooted(relativePath) || relativePath.Contains(':') || relativePath.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        string fullBase = Path.GetFullPath(baseDir);
        string basePrefix = fullBase.EndsWith(Path.DirectorySeparatorChar)
            ? fullBase
            : fullBase + Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(Path.Combine(fullBase, relativePath));
        if (!candidate.StartsWith(basePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        fullPath = candidate;
        return true;
    }

    private static bool HasReparsePointInPath(string fullPath)
    {
        try
        {
            var dir = new DirectoryInfo(fullPath);
            while (dir != null)
            {
                if (dir.Exists && dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    return true;
                }
                dir = dir.Parent;
            }
        }
        catch { }
        return false;
    }

    private static bool HasReparsePointAncestor(string baseDir, string targetFullPath)
    {
        try
        {
            string trimmedBase = Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar);
            string? current = Path.GetDirectoryName(Path.GetFullPath(targetFullPath));
            while (!string.IsNullOrEmpty(current) && current.Length >= trimmedBase.Length)
            {
                if (Directory.Exists(current) && new DirectoryInfo(current).Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    return true;
                }
                if (string.Equals(current, trimmedBase, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                current = Path.GetDirectoryName(current);
            }
        }
        catch { }
        return false;
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
