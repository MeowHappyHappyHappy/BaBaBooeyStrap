using BaBaBooeyStrap.Core;
using BaBaBooeyStrap.Core.Logging;

namespace BaBaBooeyStrap.Integrations.Cleaner;

public sealed class CacheCleaner : ICleaner
{
    private sealed record TargetSpec(
        string TargetDirectory,
        string AllowedRoot,
        string? ExcludeSubdirectory = null,
        bool ExcludeDmpFiles = false,
        string? OnlyExtension = null,
        string? ActiveFileToPreserve = null);

    private readonly IPaths _paths;
    private readonly ILogger _logger;
    private readonly Func<DateTime> _utcNowProvider;

    public CacheCleaner(IPaths paths, ILogger? logger = null, Func<DateTime>? utcNowProvider = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? Logger.Current;
        _utcNowProvider = utcNowProvider ?? (() => DateTime.UtcNow);
    }

    public CleanerReport ExecuteCleanup(CleanerPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (!policy.CleanLogs &&
            !policy.CleanCrashDumps &&
            !policy.CleanLauncherLogs &&
            !policy.CleanHttpCache &&
            !policy.CleanStudioCache)
        {
            return new CleanerReport(0, 0, Array.Empty<string>());
        }

        var specs = BuildTargetSpecs(policy);
        return ExecuteSpecs(specs, policy.LogAgeThreshold, policy.MaxTotalSizeBytes, throwOnSafetyViolation: false);
    }

    public CleanerReport CleanDirectories(
        IEnumerable<string> directoryPaths,
        string allowedRoot,
        TimeSpan ageThreshold,
        long maxTotalSizeBytes = 0)
    {
        ArgumentNullException.ThrowIfNull(directoryPaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(allowedRoot);

        var specs = directoryPaths
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => new TargetSpec(d, allowedRoot))
            .ToList();

        return ExecuteSpecs(specs, ageThreshold, maxTotalSizeBytes, throwOnSafetyViolation: true);
    }

    private List<TargetSpec> BuildTargetSpecs(CleanerPolicy policy)
    {
        var specs = new List<TargetSpec>();

        if (policy.CleanLogs)
        {
            string crashesSubdir = Path.Combine(_paths.RobloxLogs, "crashes");
            specs.Add(new TargetSpec(
                _paths.RobloxLogs,
                _paths.Roblox,
                ExcludeSubdirectory: policy.CleanCrashDumps ? null : crashesSubdir,
                ExcludeDmpFiles: !policy.CleanCrashDumps));
        }

        if (policy.CleanCrashDumps)
        {
            string crashesDir = Path.Combine(_paths.RobloxLogs, "crashes");
            specs.Add(new TargetSpec(crashesDir, _paths.Roblox));

            if (!policy.CleanLogs)
            {
                specs.Add(new TargetSpec(
                    _paths.RobloxLogs,
                    _paths.Roblox,
                    OnlyExtension: ".dmp"));
            }
        }

        if (policy.CleanLauncherLogs)
        {
            string? activeLog = _logger.FileLocation;
            specs.Add(new TargetSpec(_paths.Logs, _paths.Base, ActiveFileToPreserve: activeLog));
            if (!string.IsNullOrWhiteSpace(_paths.TempLogs))
            {
                string tempLauncherRoot = Path.Combine(_paths.Temp, "BaBaBooeyStrap");
                specs.Add(new TargetSpec(_paths.TempLogs, tempLauncherRoot, ActiveFileToPreserve: activeLog));
            }
        }

        if (policy.CleanHttpCache)
        {
            specs.Add(new TargetSpec(Path.Combine(_paths.Roblox, "http"), _paths.Roblox));

            if (!string.IsNullOrWhiteSpace(_paths.Temp))
            {
                string tempRobloxDir = Path.Combine(_paths.Temp, "Roblox");
                specs.Add(new TargetSpec(tempRobloxDir, _paths.Temp));
            }

            if (!string.IsNullOrWhiteSpace(_paths.RobloxCache))
            {
                string tempRobloxNorm = !string.IsNullOrWhiteSpace(_paths.Temp)
                    ? PathSafetyValidator.NormalizePath(Path.Combine(_paths.Temp, "Roblox"))
                    : string.Empty;
                string cacheNorm = PathSafetyValidator.NormalizePath(_paths.RobloxCache);
                if (!string.Equals(cacheNorm, tempRobloxNorm, StringComparison.OrdinalIgnoreCase))
                {
                    specs.Add(new TargetSpec(Path.Combine(_paths.RobloxCache, "http"), _paths.RobloxCache));
                }
            }
        }

        if (policy.CleanStudioCache)
        {
            specs.Add(new TargetSpec(Path.Combine(_paths.Roblox, "StudioCache"), _paths.Roblox));
            if (!string.IsNullOrWhiteSpace(_paths.Temp))
            {
                specs.Add(new TargetSpec(Path.Combine(_paths.Temp, "RobloxStudio"), _paths.Temp));
            }
            if (!string.IsNullOrWhiteSpace(_paths.RobloxStudio))
            {
                specs.Add(new TargetSpec(Path.Combine(_paths.RobloxStudio, "Cache"), _paths.RobloxStudio));
            }
        }

        return specs;
    }

    private CleanerReport ExecuteSpecs(
        List<TargetSpec> specs,
        TimeSpan logAgeThreshold,
        long maxTotalSizeBytes,
        bool throwOnSafetyViolation)
    {
        var errors = new List<string>();
        var safeSpecs = new List<TargetSpec>(specs.Count);

        // Validate every target directory strictly before modifying any file
        foreach (var spec in specs)
        {
            try
            {
                PathSafetyValidator.AssertSafeTargetDirectory(spec.TargetDirectory, spec.AllowedRoot);
                safeSpecs.Add(spec);
            }
            catch (Exception ex) when (!throwOnSafetyViolation)
            {
                _logger.Warn("CacheCleaner", $"Skipped unsafe cleanup target '{spec.TargetDirectory}': {ex.Message}");
                errors.Add(ex.Message);
            }
        }

        int filesDeleted = 0;
        long bytesFreed = 0;
        DateTime cutoffUtc = _utcNowProvider() - logAgeThreshold;

        var survivingFiles = new List<(FileInfo File, string TargetRoot)>();
        var visitedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Phase 1: Age Threshold Cleanup
        foreach (var spec in safeSpecs)
        {
            string fullTarget = PathSafetyValidator.NormalizePath(spec.TargetDirectory);
            if (!Directory.Exists(fullTarget)) continue;

            foreach (var file in EnumerateSafeFiles(spec, fullTarget, errors))
            {
                if (!visitedPaths.Add(file.FullName)) continue;

                if (file.LastWriteTimeUtc < cutoffUtc)
                {
                    if (TryDeleteFile(file, out long deletedBytes, out string? error))
                    {
                        filesDeleted++;
                        bytesFreed += deletedBytes;
                    }
                    else if (error != null)
                    {
                        errors.Add(error);
                    }
                }
                else
                {
                    survivingFiles.Add((file, fullTarget));
                }
            }
        }

        // Phase 2: Directory Size Cap (oldest first)
        if (maxTotalSizeBytes > 0 && survivingFiles.Count > 0)
        {
            long totalRemainingBytes = 0;
            var sized = new List<(FileInfo File, long Length)>(survivingFiles.Count);
            foreach (var (file, _) in survivingFiles)
            {
                try
                {
                    if (!file.Exists) continue;
                    long len = file.Length;
                    totalRemainingBytes += len;
                    sized.Add((file, len));
                }
                catch { }
            }

            if (totalRemainingBytes > maxTotalSizeBytes)
            {
                var orderedOldestFirst = sized
                    .OrderBy(item => item.File.LastWriteTimeUtc)
                    .ThenBy(item => item.File.FullName, StringComparer.OrdinalIgnoreCase);

                foreach (var (file, _) in orderedOldestFirst)
                {
                    if (totalRemainingBytes <= maxTotalSizeBytes) break;

                    if (TryDeleteFile(file, out long deletedBytes, out string? error))
                    {
                        filesDeleted++;
                        bytesFreed += deletedBytes;
                        totalRemainingBytes -= deletedBytes;
                    }
                    else if (error != null)
                    {
                        errors.Add(error);
                    }
                }
            }
        }

        // Phase 3: Empty Subdirectory Pruning (never deletes target root itself)
        foreach (var spec in safeSpecs)
        {
            string fullTarget = PathSafetyValidator.NormalizePath(spec.TargetDirectory);
            if (!Directory.Exists(fullTarget)) continue;
            PruneEmptySubdirectories(fullTarget, spec.ExcludeSubdirectory);
        }

        return new CleanerReport(filesDeleted, bytesFreed, errors);
    }

    private static IEnumerable<FileInfo> EnumerateSafeFiles(TargetSpec spec, string rootDir, List<string> errors)
    {
        string? normalizedExcludeSubdir = !string.IsNullOrWhiteSpace(spec.ExcludeSubdirectory)
            ? PathSafetyValidator.NormalizePath(spec.ExcludeSubdirectory)
            : null;
        string? normalizedActiveFile = !string.IsNullOrWhiteSpace(spec.ActiveFileToPreserve)
            ? PathSafetyValidator.NormalizePath(spec.ActiveFileToPreserve)
            : null;

        var pendingDirs = new Stack<string>();
        pendingDirs.Push(rootDir);

        while (pendingDirs.Count > 0)
        {
            string currentDir = pendingDirs.Pop();

            if (normalizedExcludeSubdir != null &&
                (string.Equals(currentDir, normalizedExcludeSubdir, StringComparison.OrdinalIgnoreCase) ||
                 currentDir.StartsWith(normalizedExcludeSubdir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            DirectoryInfo dirInfo;
            try
            {
                dirInfo = new DirectoryInfo(currentDir);
                if (!dirInfo.Exists) continue;
                if ((dirInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    // Never traverse NTFS junctions/symlinks
                    continue;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"Skipped directory '{currentDir}': {ex.Message}");
                continue;
            }

            FileInfo[] files;
            try
            {
                files = dirInfo.GetFiles();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"Failed to enumerate files in '{currentDir}': {ex.Message}");
                continue;
            }

            foreach (var file in files)
            {
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0) continue;

                if (normalizedActiveFile != null &&
                    string.Equals(PathSafetyValidator.NormalizePath(file.FullName), normalizedActiveFile, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (spec.ExcludeDmpFiles &&
                    string.Equals(file.Extension, ".dmp", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(spec.OnlyExtension) &&
                    !string.Equals(file.Extension, spec.OnlyExtension, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                yield return file;
            }

            try
            {
                foreach (var subDir in dirInfo.GetDirectories())
                {
                    if ((subDir.Attributes & FileAttributes.ReparsePoint) == 0)
                    {
                        pendingDirs.Push(PathSafetyValidator.NormalizePath(subDir.FullName));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"Failed to enumerate subdirectories in '{currentDir}': {ex.Message}");
            }
        }
    }

    private bool TryDeleteFile(FileInfo file, out long size, out string? error)
    {
        size = 0;
        error = null;
        bool clearedReadOnly = false;

        try
        {
            if (!file.Exists) return false;
            size = file.Length;

            try
            {
                file.Delete();
                return true;
            }
            catch (UnauthorizedAccessException) when ((file.Attributes & FileAttributes.ReadOnly) != 0)
            {
                file.Attributes &= ~FileAttributes.ReadOnly;
                clearedReadOnly = true;
                file.Delete();
                return true;
            }
        }
        catch (IOException ex)
        {
            if (clearedReadOnly)
            {
                try { file.Attributes |= FileAttributes.ReadOnly; } catch { }
            }
            error = $"Skipped locked file '{file.FullName}': {ex.Message}";
            _logger.Debug("CacheCleaner", error);
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            if (clearedReadOnly)
            {
                try { file.Attributes |= FileAttributes.ReadOnly; } catch { }
            }
            error = $"Skipped inaccessible file '{file.FullName}': {ex.Message}";
            _logger.Debug("CacheCleaner", error);
            return false;
        }
    }

    private static void PruneEmptySubdirectories(string rootDir, string? excludeSubdir)
    {
        string? normalizedExclude = !string.IsNullOrWhiteSpace(excludeSubdir)
            ? PathSafetyValidator.NormalizePath(excludeSubdir)
            : null;

        try
        {
            var rootInfo = new DirectoryInfo(rootDir);
            foreach (var child in rootInfo.GetDirectories())
            {
                PruneRecursive(child, normalizedExclude);
            }
        }
        catch { }
    }

    private static void PruneRecursive(DirectoryInfo dir, string? normalizedExclude)
    {
        try
        {
            if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) return;

            string full = PathSafetyValidator.NormalizePath(dir.FullName);
            if (normalizedExclude != null &&
                string.Equals(full, normalizedExclude, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            foreach (var sub in dir.GetDirectories())
            {
                PruneRecursive(sub, normalizedExclude);
            }

            if (dir.GetFiles().Length == 0 && dir.GetDirectories().Length == 0)
            {
                dir.Delete(recursive: false);
            }
        }
        catch { }
    }
}
