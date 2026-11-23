using System.Text.RegularExpressions;

namespace BaBaBooeyStrap.Core;

public partial class PathProvider : IPaths
{
    private static readonly Lazy<string> CachedUserProfile = new(() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    private static readonly Lazy<string> CachedLocalAppData = new(() => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    private static readonly Lazy<string> CachedDesktop = new(() => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
    private static readonly Lazy<string> CachedWindowsStartMenu = new(() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"));
    private static readonly Lazy<string> CachedSystem = new(() => Environment.GetFolderPath(Environment.SpecialFolder.System));

    [GeneratedRegex(@"^version-[A-Za-z0-9][A-Za-z0-9_-]{0,127}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex SafeVersionGuidRegex();

    private volatile string _base;
    private volatile bool _initialized;

    public string Base => _base;
    public string Downloads => Path.Combine(_base, "Downloads");
    public string Logs => Path.Combine(_base, "Logs");
    public string Versions => Path.Combine(_base, "Versions");
    public string Modifications => Path.Combine(_base, "Modifications");
    public string CustomThemes => Path.Combine(_base, "CustomThemes");
    public string SavedFlagProfiles => Path.Combine(_base, "SavedFlagProfiles");
    public string Integrations => Path.Combine(_base, "Integrations");
    public string Application => Path.Combine(_base, "BaBaBooeyStrap.exe");

    public string AccountsFile => Path.Combine(_base, "accounts.dat");
    public string TargetsFile => Path.Combine(_base, "targets.json");
    public string SettingsFile => Path.Combine(_base, "Settings.json");
    public string StateFile => Path.Combine(_base, "State.json");
    public string RobloxStateFile => Path.Combine(_base, "RobloxState.json");

    public string Temp => Path.GetTempPath();
    public string TempUpdates => Path.Combine(Temp, "BaBaBooeyStrap", "Updates");
    public string TempLogs => Path.Combine(Temp, "BaBaBooeyStrap", "Logs");

    public string UserProfile => CachedUserProfile.Value;
    public string LocalAppData => CachedLocalAppData.Value;
    public string Desktop => CachedDesktop.Value;
    public string WindowsStartMenu => CachedWindowsStartMenu.Value;
    public string System => CachedSystem.Value;
    public virtual string Process => Environment.ProcessPath ?? string.Empty;

    public string Roblox => Path.Combine(LocalAppData, "Roblox");
    public string RobloxStudio => Path.Combine(LocalAppData, "Roblox Studio");
    public string RobloxLogs => Path.Combine(Roblox, "logs");
    public string RobloxVersions => Path.Combine(Roblox, "Versions");
    public string RobloxGlobalSettings => Path.Combine(Roblox, "GlobalBasicSettings_13.xml");
    public string RobloxLocalStorage => Path.Combine(Roblox, "LocalStorage");
    public string RobloxCache => Path.Combine(Temp, "Roblox");

    public bool Initialized => _initialized;

    public PathProvider(string? baseDirectory = null)
    {
        string rawBase = !string.IsNullOrWhiteSpace(baseDirectory)
            ? baseDirectory
            : Path.Combine(CachedLocalAppData.Value, "BaBaBooeyStrap");
        _base = Path.GetFullPath(rawBase);
        _initialized = !string.IsNullOrWhiteSpace(baseDirectory);
    }

    public void Initialize(string? baseDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(baseDirectory))
        {
            _base = Path.GetFullPath(baseDirectory);
        }
        _initialized = true;
    }

    public void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(Base);
        Directory.CreateDirectory(Downloads);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Versions);
        Directory.CreateDirectory(Modifications);
        Directory.CreateDirectory(CustomThemes);
        Directory.CreateDirectory(SavedFlagProfiles);
        Directory.CreateDirectory(Integrations);
    }

    public static bool TryValidateVersionGuid(string? versionGuid, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(versionGuid)) return false;

        string trimmed = versionGuid.Trim();
        if (!SafeVersionGuidRegex().IsMatch(trimmed) ||
            trimmed.Contains("..", StringComparison.Ordinal) ||
            trimmed.IndexOfAny(['/', '\\', ':']) >= 0)
        {
            return false;
        }

        normalized = trimmed;
        return true;
    }

    public static string ValidateVersionGuid(string versionGuid)
    {
        if (!TryValidateVersionGuid(versionGuid, out string normalized))
        {
            throw new ArgumentException($"Invalid Roblox version GUID '{versionGuid}'.", nameof(versionGuid));
        }

        return normalized;
    }

    public string GetVersionDirectory(string versionGuid)
    {
        string safeGuid = ValidateVersionGuid(versionGuid);
        string versionsRoot = Path.GetFullPath(Versions);
        string combined = Path.GetFullPath(Path.Combine(versionsRoot, safeGuid));

        string prefix = versionsRoot.EndsWith(Path.DirectorySeparatorChar)
            ? versionsRoot
            : versionsRoot + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Version directory '{versionGuid}' escapes Versions root.", nameof(versionGuid));
        }

        return combined;
    }

    public string GetClientSettingsPath(string? versionGuid = null)
    {
        if (!string.IsNullOrWhiteSpace(versionGuid))
        {
            return Path.Combine(GetVersionDirectory(versionGuid), "ClientSettings", "ClientAppSettings.json");
        }
        return Path.Combine(Modifications, "ClientSettings", "ClientAppSettings.json");
    }
}
