using System.Buffers;
using System.Security;
using System.Text.RegularExpressions;
using ICSharpCode.SharpZipLib.Zip;
using BaBaBooeyStrap.Bootstrapper.Deployment.Abstractions;

namespace BaBaBooeyStrap.Bootstrapper.Deployment;

public class PackageExtractor : IPackageExtractor
{
    public const int MaxArchiveEntries = 50_000;
    public const long MaxEntryBytes = 512L * 1024 * 1024;
    public const long MaxTotalExtractedBytes = 4L * 1024 * 1024 * 1024;
    public const double MaxCompressionRatio = 100.0;
    private const long CompressionRatioCheckThresholdBytes = 1024 * 1024;

    private const int ErrorSharingViolation = unchecked((int)0x80070020);
    private const int ErrorLockViolation = unchecked((int)0x80070021);

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static readonly IReadOnlyDictionary<string, string> PackageDirectoryMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "RobloxApp.zip", "" },
        { "redist.zip", "" },
        { "shaders.zip", "shaders\\" },
        { "ssl.zip", "ssl\\" },
        { "WebView2.zip", "" },
        { "WebView2RuntimeInstaller.zip", "WebView2RuntimeInstaller\\" },
        { "content-avatar.zip", "content\\avatar\\" },
        { "content-configs.zip", "content\\configs\\" },
        { "content-fonts.zip", "content\\fonts\\" },
        { "content-sky.zip", "content\\sky\\" },
        { "content-sounds.zip", "content\\sounds\\" },
        { "content-textures2.zip", "content\\textures\\" },
        { "content-models.zip", "content\\models\\" },
        { "content-textures3.zip", "PlatformContent\\pc\\textures\\" },
        { "content-terrain.zip", "PlatformContent\\pc\\terrain\\" },
        { "content-platform-fonts.zip", "PlatformContent\\pc\\fonts\\" },
        { "content-platform-dictionaries.zip", "PlatformContent\\pc\\shared_compression_dictionaries\\" },
        { "extracontent-luapackages.zip", "ExtraContent\\LuaPackages\\" },
        { "extracontent-translations.zip", "ExtraContent\\translations\\" },
        { "extracontent-models.zip", "ExtraContent\\models\\" },
        { "extracontent-textures.zip", "ExtraContent\\textures\\" },
        { "extracontent-places.zip", "ExtraContent\\places\\" },
        { "RobloxStudio.zip", "" },
        { "ApplicationConfig.zip", "ApplicationConfig\\" },
        { "BuiltInPlugins.zip", "BuiltInPlugins\\" },
        { "BuiltInStandalonePlugins.zip", "BuiltInStandalonePlugins\\" },
        { "content-qt_translations.zip", "content\\qt_translations\\" },
        { "content-api-docs.zip", "content\\api_docs\\" },
        { "extracontent-scripts.zip", "ExtraContent\\scripts\\" },
        { "Libraries.zip", "" },
        { "LibrariesQt5.zip", "" },
        { "Plugins.zip", "Plugins\\" },
        { "Qml.zip", "Qml\\" },
        { "StudioFonts.zip", "StudioFonts\\" }
    };

    public void ExtractZip(byte[] zipBytes, string targetDirectory)
    {
        ArgumentNullException.ThrowIfNull(zipBytes);
        ArgumentNullException.ThrowIfNull(targetDirectory);

        if (zipBytes.Length == 0)
        {
            throw new InvalidDataException("Zero-byte zip archive is invalid.");
        }

        using var memoryStream = new MemoryStream(zipBytes);
        ExtractFromStream(memoryStream, targetDirectory);
    }

    public void ExtractZipFile(string zipFilePath, string targetDirectory, string? fileFilter = null)
    {
        ExtractZipFileInternal(zipFilePath, targetDirectory, fileFilter, exactNormalizedFiles: null, CancellationToken.None);
    }

    private static int ExtractZipFileInternal(
        string zipFilePath,
        string targetDirectory,
        string? fileFilter,
        IReadOnlySet<string>? exactNormalizedFiles,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(zipFilePath);
        ArgumentNullException.ThrowIfNull(targetDirectory);
        ct.ThrowIfCancellationRequested();

        try
        {
            using var stream = File.OpenRead(zipFilePath);
            if (stream.Length == 0)
            {
                throw new InvalidDataException("Zero-byte zip file is invalid.");
            }
            return ExtractFromStream(stream, targetDirectory, fileFilter, exactNormalizedFiles, ct);
        }
        catch (FileNotFoundException)
        {
            throw;
        }
        catch (DirectoryNotFoundException)
        {
            throw new FileNotFoundException("Zip package file not found.", zipFilePath);
        }
    }

    public Task ExtractPackageAsync(
        string packageZipPath,
        string targetVersionDirectory,
        string relativeSubdirectory,
        IEnumerable<string>? specificFiles = null,
        CancellationToken ct = default)
    {
        return ExtractPackageWithCountAsync(packageZipPath, targetVersionDirectory, relativeSubdirectory, specificFiles, ct);
    }

    public Task<int> ExtractPackageWithCountAsync(
        string packageZipPath,
        string targetVersionDirectory,
        string relativeSubdirectory,
        IEnumerable<string>? specificFiles = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(targetVersionDirectory);

        HashSet<string>? exactFiles = null;
        if (specificFiles != null)
        {
            var normalized = specificFiles
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(NormalizeZipRelativePath)
                .Where(s => s.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (normalized.Count > 0)
            {
                exactFiles = normalized;
            }
        }

        string fullVersionDir = Path.GetFullPath(targetVersionDirectory);
        string versionPrefix = fullVersionDir.EndsWith(Path.DirectorySeparatorChar)
            ? fullVersionDir
            : fullVersionDir + Path.DirectorySeparatorChar;
        string sub = relativeSubdirectory ?? string.Empty;
        if (Path.IsPathRooted(sub) || sub.Contains(':') || sub.Contains("..", StringComparison.Ordinal))
        {
            throw new SecurityException($"Invalid relative subdirectory '{relativeSubdirectory}'.");
        }

        string fullTargetDir = Path.GetFullPath(Path.Combine(fullVersionDir, sub));
        if (!string.Equals(fullTargetDir.TrimEnd(Path.DirectorySeparatorChar), fullVersionDir.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) &&
            !fullTargetDir.StartsWith(versionPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new SecurityException($"Relative subdirectory '{relativeSubdirectory}' escapes target version directory.");
        }

        return Task.Run(() =>
        {
            Directory.CreateDirectory(fullTargetDir);
            return ExtractZipFileInternal(packageZipPath, fullTargetDir, fileFilter: null, exactFiles, ct);
        }, ct);
    }

    private static string NormalizeZipRelativePath(string path) =>
        path.Replace('\\', '/').Trim().TrimStart('/');

    private static int ExtractFromStream(
        Stream stream,
        string targetDirectory,
        string? fileFilter = null,
        IReadOnlySet<string>? exactNormalizedFiles = null,
        CancellationToken ct = default)
    {
        Regex? filterRegex = string.IsNullOrEmpty(fileFilter)
            ? null
            : new Regex(fileFilter, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));

        Directory.CreateDirectory(targetDirectory);
        string fullTargetDir = Path.GetFullPath(targetDirectory);
        if (!fullTargetDir.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
        {
            fullTargetDir += Path.DirectorySeparatorChar;
        }

        if (HasReparsePointInPath(fullTargetDir))
        {
            throw new SecurityException($"Refused extraction into reparse point path: '{targetDirectory}'.");
        }

        using var zipFile = OpenZipSafe(stream);
        int extractedFiles = 0;
        int processedEntries = 0;
        long totalExtractedBytes = 0;

        byte[] copyBuffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            foreach (ZipEntry entry in zipFile)
            {
                ct.ThrowIfCancellationRequested();

                processedEntries++;
                if (processedEntries > MaxArchiveEntries)
                {
                    throw new InvalidDataException($"Zip archive exceeds maximum allowed entry count ({MaxArchiveEntries}).");
                }

                // Reject Unix symlinks (S_IFLNK = 0xA000)
                if (((entry.ExternalFileAttributes >> 16) & 0xF000) == 0xA000)
                {
                    throw new SecurityException($"Zip archive contains disallowed symlink entry: {entry.Name}");
                }

                string normalizedZipPath = NormalizeZipRelativePath(entry.Name);
                if (exactNormalizedFiles != null)
                {
                    if (!exactNormalizedFiles.Contains(normalizedZipPath))
                    {
                        continue;
                    }
                }
                else if (filterRegex != null && !filterRegex.IsMatch(entry.Name))
                {
                    continue;
                }

                bool isDirectory = entry.IsDirectory || entry.Name.EndsWith('/') || entry.Name.EndsWith('\\');
                string rawName = entry.Name;
                if (string.IsNullOrWhiteSpace(rawName))
                {
                    continue;
                }

                if (rawName.Contains(':') || Path.IsPathRooted(rawName))
                {
                    throw new SecurityException($"ZipSlip invalid/rooted entry name detected: {entry.Name}");
                }

                string normalizedEntryName = rawName
                    .Replace('/', Path.DirectorySeparatorChar)
                    .Replace('\\', Path.DirectorySeparatorChar)
                    .TrimStart(Path.DirectorySeparatorChar);

                if (string.IsNullOrWhiteSpace(normalizedEntryName))
                {
                    continue;
                }

                if (ContainsUnsafeSegment(normalizedEntryName))
                {
                    throw new SecurityException($"ZipSlip or reserved segment detected in entry: {entry.Name}");
                }

                // ZipSlip directory traversal check
                string destinationPath = Path.GetFullPath(Path.Combine(fullTargetDir, normalizedEntryName));
                if (!destinationPath.StartsWith(fullTargetDir, StringComparison.OrdinalIgnoreCase))
                {
                    throw new SecurityException($"ZipSlip directory traversal detected in entry: {entry.Name}");
                }

                if (HasReparsePointAncestor(fullTargetDir, destinationPath))
                {
                    throw new SecurityException($"Refused extraction across reparse point / junction: {entry.Name}");
                }

                if (isDirectory)
                {
                    Directory.CreateDirectory(destinationPath);
                    continue;
                }

                string? parentDir = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(parentDir))
                {
                    Directory.CreateDirectory(parentDir);
                }

                string tempDest = $"{destinationPath}.{Guid.NewGuid():N}.extracttmp";
                try
                {
                    if (File.Exists(destinationPath))
                    {
                        var attrs = File.GetAttributes(destinationPath);
                        if (attrs.HasFlag(FileAttributes.ReparsePoint))
                        {
                            throw new SecurityException($"Refused overwriting reparse point destination: {entry.Name}");
                        }
                        if (attrs.HasFlag(FileAttributes.ReadOnly))
                        {
                            File.SetAttributes(destinationPath, attrs & ~FileAttributes.ReadOnly);
                        }
                    }

                    long entryBytes = 0;
                    using (var entryStream = zipFile.GetInputStream(entry))
                    using (var destStream = new FileStream(tempDest, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        int read;
                        while ((read = entryStream.Read(copyBuffer, 0, copyBuffer.Length)) > 0)
                        {
                            ct.ThrowIfCancellationRequested();

                            entryBytes += read;
                            totalExtractedBytes += read;

                            if (entryBytes > MaxEntryBytes)
                            {
                                throw new InvalidDataException($"Zip entry '{entry.Name}' exceeds maximum extracted size ({MaxEntryBytes} bytes).");
                            }
                            if (totalExtractedBytes > MaxTotalExtractedBytes)
                            {
                                throw new InvalidDataException($"Zip archive exceeds maximum total extracted size ({MaxTotalExtractedBytes} bytes).");
                            }
                            if (entryBytes > CompressionRatioCheckThresholdBytes &&
                                entry.CompressedSize > 0 &&
                                (double)entryBytes / entry.CompressedSize > MaxCompressionRatio)
                            {
                                throw new InvalidDataException($"Zip entry '{entry.Name}' exceeds maximum permitted compression ratio ({MaxCompressionRatio}x).");
                            }

                            destStream.Write(copyBuffer, 0, read);
                        }
                    }

                    File.Move(tempDest, destinationPath, overwrite: true);
                    extractedFiles++;
                }
                catch (UnauthorizedAccessException) when (ProbeLockedFileSharingViolation(destinationPath, out var sharingIoEx))
                {
                    TryDeleteQuietly(tempDest);
                    if (IsToleratedLockedFont(destinationPath, sharingIoEx))
                    {
                        continue;
                    }
                    throw sharingIoEx;
                }
                catch (IOException ex)
                {
                    TryDeleteQuietly(tempDest);
                    if (IsToleratedLockedFont(destinationPath, ex))
                    {
                        // Tolerated font lock - continue extraction
                        continue;
                    }
                    throw;
                }
                catch
                {
                    TryDeleteQuietly(tempDest);
                    throw;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(copyBuffer);
        }

        return extractedFiles;
    }

    private static bool IsToleratedLockedFont(string destinationPath, IOException ex)
    {
        return (ex.HResult == ErrorSharingViolation || ex.HResult == ErrorLockViolation) &&
               (destinationPath.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) ||
                destinationPath.EndsWith(".otf", StringComparison.OrdinalIgnoreCase) ||
                destinationPath.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ProbeLockedFileSharingViolation(string destinationPath, out IOException sharingException)
    {
        sharingException = null!;
        if (!File.Exists(destinationPath))
        {
            return false;
        }

        try
        {
            using var _ = new FileStream(destinationPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException ex)
        {
            sharingException = ex;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static ZipFile OpenZipSafe(Stream stream)
    {
        try
        {
            return new ZipFile(stream) { IsStreamOwner = false };
        }
        catch (Exception ex) when (ex is not SecurityException)
        {
            throw new InvalidDataException("Failed to open zip archive. Archive may be corrupted or malformed.", ex);
        }
    }

    private static bool ContainsUnsafeSegment(string relativePath)
    {
        string[] segments = relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        foreach (string seg in segments)
        {
            if (seg == "..") return true;
            if (seg.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return true;
            int dotIdx = seg.IndexOf('.');
            string stem = dotIdx >= 0 ? seg[..dotIdx] : seg;
            if (ReservedDeviceNames.Contains(stem)) return true;
        }
        return false;
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

    private static bool HasReparsePointAncestor(string rootDirWithSep, string targetFullPath)
    {
        try
        {
            string trimmedRoot = rootDirWithSep.TrimEnd(Path.DirectorySeparatorChar);
            string? current = Path.GetDirectoryName(targetFullPath);
            while (!string.IsNullOrEmpty(current) &&
                   current.Length >= trimmedRoot.Length)
            {
                if (Directory.Exists(current) &&
                    new DirectoryInfo(current).Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    return true;
                }
                if (string.Equals(current, trimmedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                current = Path.GetDirectoryName(current);
            }
        }
        catch { }
        return false;
    }

    private static void TryDeleteQuietly(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
