using Microsoft.Win32;

namespace BaBaBooeyStrap.Core.Storage;

public static class WindowsRegistry
{
    public const string AppName = "BaBaBooeyStrap";
    public const string AppRegistryRoot = @"Software\BaBaBooeyStrap";

    public static void RegisterProtocol(string key, string name, string handlerPath, string handlerParam = "%1")
    {
        try
        {
            string command = $"\"{handlerPath}\" {handlerParam}";
            using var regKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{key}");
            if (regKey == null) return;

            using var defaultIcon = regKey.CreateSubKey("DefaultIcon");
            using var shellOpenCmd = regKey.CreateSubKey(@"shell\open\command");

            if (regKey.GetValue("") == null)
            {
                regKey.SetValue("", $"URL: {name} Protocol");
                regKey.SetValue("URL Protocol", "");
            }
            if (shellOpenCmd != null && shellOpenCmd.GetValue("") as string != command)
            {
                defaultIcon?.SetValue("", handlerPath);
                shellOpenCmd.SetValue("", command);
            }
        }
        catch { }
    }

    public static void RegisterPlayer(string handlerPath)
    {
        RegisterProtocol("roblox", "Roblox", handlerPath, "-player \"%1\"");
        RegisterProtocol("roblox-player", "Roblox", handlerPath, "-player \"%1\"");
    }

    public static void RegisterStudio(string handlerPath)
    {
        RegisterProtocol("roblox-studio", "Roblox", handlerPath, "-studio \"%1\"");
        RegisterProtocol("roblox-studio-auth", "Roblox", handlerPath, "-studio \"%1\"");
        RegisterStudioFileAssociations(handlerPath);
    }

    public static void RegisterStudioFileAssociations(string handlerPath)
    {
        try
        {
            using var placeKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Roblox.Place");
            if (placeKey != null)
            {
                using var iconKey = placeKey.CreateSubKey("DefaultIcon");
                using var cmdKey = placeKey.CreateSubKey(@"shell\Open\command");

                placeKey.SetValue("", "Roblox Place");
                iconKey?.SetValue("", $"{handlerPath},0");
                cmdKey?.SetValue("", $"\"{handlerPath}\" -studio \"%1\"");
            }

            RegisterExtension(".rbxl");
            RegisterExtension(".rbxlx");
        }
        catch { }

        static void RegisterExtension(string ext)
        {
            try
            {
                using var extKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ext}");
                if (extKey != null)
                {
                    using var shellNew = extKey.CreateSubKey(@"Roblox.Place\ShellNew");
                    extKey.SetValue("", "Roblox.Place");
                }
            }
            catch { }
        }
    }

    public static void RegisterAppLocation(string applicationPath, string installationPath)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(AppRegistryRoot);
            key?.SetValue("ApplicationPath", applicationPath ?? string.Empty);
            key?.SetValue("InstallationPath", installationPath ?? string.Empty);
        }
        catch { }
    }

    public static void RegisterClientLocation(bool isStudio, string clientPath)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(AppRegistryRoot);
            key?.SetValue(isStudio ? "StudioPath" : "PlayerPath", clientPath ?? string.Empty);
        }
        catch { }
    }

    public static void RemoveRobloxStartupEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key?.GetValue("RobloxPlayerBeta") is string val && val.Contains("RobloxPlayerBeta.exe", StringComparison.OrdinalIgnoreCase))
            {
                key.DeleteValue("RobloxPlayerBeta");
            }
        }
        catch { }
    }

    public static void UnregisterProtocol(string key)
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{key}", throwOnMissingSubKey: false);
        }
        catch { }
    }

    public static void RegisterUninstall(string handlerPath)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{AppName}");
            if (key == null) return;

            key.SetValue("DisplayName", AppName);
            key.SetValue("DisplayIcon", handlerPath);
            key.SetValue("UninstallString", $"\"{handlerPath}\" -uninstall");
            key.SetValue("Publisher", AppName);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        catch { }
    }

    public static void UnregisterAll()
    {
        UnregisterProtocol("roblox");
        UnregisterProtocol("roblox-player");
        UnregisterProtocol("roblox-studio");
        UnregisterProtocol("roblox-studio-auth");
        UnregisterProtocol("Roblox.Place");
        UnregisterProtocol(".rbxl");
        UnregisterProtocol(".rbxlx");

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(AppRegistryRoot, throwOnMissingSubKey: false);
        }
        catch { }

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{AppName}", throwOnMissingSubKey: false);
        }
        catch { }
    }
}
