namespace BaBaBooeyStrap.Core;

public class PathProvider : IPaths
{
    private string _base;
    private bool _initialized;

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

    public string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public string Desktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    public string WindowsStartMenu => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
    public string System => Environment.GetFolderPath(Environment.SpecialFolder.System);
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
        _base = !string.IsNullOrWhiteSpace(baseDirectory)
            ? baseDirectory
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BaBaBooeyStrap");
        _initialized = !string.IsNullOrWhiteSpace(baseDirectory);
    }

    public void Initialize(string? baseDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(baseDirectory))
        {
            _base = baseDirectory;
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

    public string GetVersionDirectory(string versionGuid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionGuid);
        return Path.Combine(Versions, versionGuid);
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
