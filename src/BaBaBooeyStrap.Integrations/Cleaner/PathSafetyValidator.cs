namespace BaBaBooeyStrap.Integrations.Cleaner;

public static class PathSafetyValidator
{
    private static readonly Lazy<HashSet<string>> SystemForbiddenRoots = new(BuildSystemForbiddenRoots);
    private static readonly Lazy<HashSet<string>> ContainerForbiddenRoots = new(BuildContainerForbiddenRoots);

    private static HashSet<string> BuildSystemForbiddenRoots()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddNormalized(set, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        AddNormalized(set, Environment.GetFolderPath(Environment.SpecialFolder.System));
        AddNormalized(set, Environment.GetFolderPath(Environment.SpecialFolder.SystemX86));
        AddNormalized(set, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        AddNormalized(set, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        return set;
    }

    private static HashSet<string> BuildContainerForbiddenRoots()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddNormalized(set, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        AddNormalized(set, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        AddNormalized(set, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        AddNormalized(set, Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        AddNormalized(set, Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
        AddNormalized(set, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        AddNormalized(set, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        AddNormalized(set, Path.GetTempPath());
        return set;
    }

    private static void AddNormalized(HashSet<string> set, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            set.Add(NormalizePath(path));
        }
        catch { }
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

        if (IsDriveOrUncRoot(fullTarget) ||
            ContainerForbiddenRoots.Value.Contains(fullTarget) ||
            SystemForbiddenRoots.Value.Any(sysRoot => IsSameOrDescendant(fullTarget, sysRoot)))
        {
            return false;
        }

        bool isDescendant = IsDescendant(fullTarget, fullRoot);
        bool isExact = allowExactMatch && string.Equals(fullTarget, fullRoot, StringComparison.OrdinalIgnoreCase);

        if (!isDescendant && !isExact)
        {
            return false;
        }

        if (!ValidateNoReparsePointAncestors(fullTarget, fullRoot))
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

    private static bool IsSameOrDescendant(string path, string root) =>
        string.Equals(path, root, StringComparison.OrdinalIgnoreCase) || IsDescendant(path, root);

    private static bool IsDescendant(string path, string root) =>
        path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool IsDriveOrUncRoot(string normalizedPath)
    {
        string? root = Path.GetPathRoot(normalizedPath);
        if (string.IsNullOrEmpty(root)) return true;

        string normalizedRoot = Path.TrimEndingDirectorySeparator(root);
        return string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ValidateNoReparsePointAncestors(string fullTarget, string fullRoot)
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
                        return false;
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
