namespace BaBaBooeyStrap.Core;

public interface IPaths
{
    string Base { get; }
    string Downloads { get; }
    string Logs { get; }
    string Versions { get; }
    string Modifications { get; }
    string CustomThemes { get; }
    string SavedFlagProfiles { get; }
    string Integrations { get; }
    string Application { get; }

    string AccountsFile { get; }
    string TargetsFile { get; }
    string SettingsFile { get; }
    string StateFile { get; }
    string RobloxStateFile { get; }

    string Temp { get; }
    string TempUpdates { get; }
    string TempLogs { get; }

    string UserProfile { get; }
    string LocalAppData { get; }
    string Desktop { get; }
    string WindowsStartMenu { get; }
    string System { get; }
    string Process { get; }

    string Roblox { get; }
    string RobloxStudio { get; }
    string RobloxLogs { get; }
    string RobloxVersions { get; }
    string RobloxGlobalSettings { get; }
    string RobloxLocalStorage { get; }
    string RobloxCache { get; }

    bool Initialized { get; }

    void Initialize(string? baseDirectory = null);
    void EnsureDirectoriesExist();
    string GetVersionDirectory(string versionGuid);
    string GetClientSettingsPath(string? versionGuid = null);
}
