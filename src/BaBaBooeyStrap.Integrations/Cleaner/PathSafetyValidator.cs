namespace BaBaBooeyStrap.Integrations.Cleaner;

public static class PathSafetyValidator
{
    private static readonly Lazy<HashSet<string>> ForbiddenRoots = new(BuildForbiddenRoots);

    private static HashSet<string> BuildForbiddenRoots()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                set.Add(NormalizePath(path));
            }
            catch { }
        }

        Add(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.System));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        Add(Path.GetTempPath());

        return set;
    }

    public static string NormalizePath(string path)
    {
        string full = Path.GetFullPath(path);
        return Path.TrimEndingDirectorySeparator(full);
    }

    public static bool ValidateDirectorySafety(string? targetDir, string? allowedRoot, bool allowExactMatch = false)
    {
        if (string.IsNullOrWhiteSpace(targetDir) || string.IsNullOrWhiteSpace(allowedRoot))
        {
            return false;
        }

        string fullTarget;
        string fullRoot;
        try
        {
            fullTarget = NormalizePath(targetDir);
            fullRoot = NormalizePath(allowedRoot);
        }
        catch
        {
            return false;
        }

        if (IsDriveOrUncRoot(fullTarget) || ForbiddenRoots.Value.Contains(fullTarget))
        {
            return false;
        }

        bool isDescendant = fullTarget.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        bool isExact = allowExactMatch && string.Equals(fullTarget, fullRoot, StringComparison.OrdinalIgnoreCase);

        if (!isDescendant && !isExact)
        {
            return false;
        }

        if (!ValidateNoExternalReparsePoints(fullTarget, fullRoot))
        {
            return false;
        }

        return true;
    }

    public static void AssertSafeTargetDirectory(string? targetDir, string? allowedRoot, bool allowExactMatch = false)
    {
        if (!ValidateDirectorySafety(targetDir, allowedRoot, allowExactMatch))
        {
            throw new InvalidOperationException(
                $"Safety violation: Target directory '{targetDir}' is outside allowed root '{allowedRoot}' or violates path sandbox rules.");
        }
    }

    private static bool IsDriveOrUncRoot(string normalizedPath)
    {
        string? root = Path.GetPathRoot(normalizedPath);
        if (string.IsNullOrEmpty(root)) return true;

        string normalizedRoot = Path.TrimEndingDirectorySeparator(root);
        return string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ValidateNoExternalReparsePoints(string fullTarget, string fullRoot)
    {
        string? current = fullTarget;
        while (!string.IsNullOrEmpty(current))
        {
            if (Directory.Exists(current))
            {
                try
                {
                    var attrs = File.GetAttributes(current);
                    if ((attrs & FileAttributes.ReparsePoint) != 0)
                    {
                        var resolved = Directory.ResolveLinkTarget(current, returnFinalTarget: true);
                        if (resolved == null) return false;

                        string resolvedFull = NormalizePath(resolved.FullName);
                        bool insideRoot = resolvedFull.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                                          string.Equals(resolvedFull, fullRoot, StringComparison.OrdinalIgnoreCase);
                        if (!insideRoot || ForbiddenRoots.Value.Contains(resolvedFull) || IsDriveOrUncRoot(resolvedFull))
                        {
                            return false;
                        }
                    }
                }
                catch
                {
                    return false;
                }
            }

            if (string.Equals(current, fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            string? parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            current = Path.TrimEndingDirectorySeparator(parent);
        }

        return true;
    }
}
