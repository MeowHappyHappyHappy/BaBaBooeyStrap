namespace BaBaBooeyStrap.Core;

public static class Paths
{
    private static IPaths _current = new PathProvider();
    public static IPaths Current => _current;

    public static void SetProvider(IPaths provider) => _current = provider ?? throw new ArgumentNullException(nameof(provider));
    public static void Initialize(string? baseDirectory = null) => _current.Initialize(baseDirectory);

    public static string Base => _current.Base;
    public static string Downloads => _current.Downloads;
    public static string Logs => _current.Logs;
    public static string Versions => _current.Versions;
    public static string Modifications => _current.Modifications;
    public static string CustomThemes => _current.CustomThemes;
    public static string SavedFlagProfiles => _current.SavedFlagProfiles;
    public static string Integrations => _current.Integrations;
    public static string Application => _current.Application;

    public static string AccountsFile => _current.AccountsFile;
    public static string TargetsFile => _current.TargetsFile;
    public static string SettingsFile => _current.SettingsFile;
    public static string StateFile => _current.StateFile;
    public static string RobloxStateFile => _current.RobloxStateFile;

    public static string Temp => _current.Temp;
    public static string TempUpdates => _current.TempUpdates;
    public static string TempLogs => _current.TempLogs;

    public static string UserProfile => _current.UserProfile;
    public static string LocalAppData => _current.LocalAppData;
    public static string Desktop => _current.Desktop;
    public static string WindowsStartMenu => _current.WindowsStartMenu;
    public static string System => _current.System;
    public static string Process => _current.Process;

    public static string Roblox => _current.Roblox;
    public static string RobloxStudio => _current.RobloxStudio;
    public static string RobloxLogs => _current.RobloxLogs;
    public static string RobloxVersions => _current.RobloxVersions;
    public static string RobloxGlobalSettings => _current.RobloxGlobalSettings;
    public static string RobloxLocalStorage => _current.RobloxLocalStorage;
    public static string RobloxCache => _current.RobloxCache;

    public static bool Initialized => _current.Initialized;
    public static void EnsureDirectoriesExist() => _current.EnsureDirectoriesExist();
    public static string GetVersionDirectory(string versionGuid) => _current.GetVersionDirectory(versionGuid);
    public static string GetClientSettingsPath(string? versionGuid = null) => _current.GetClientSettingsPath(versionGuid);
}
