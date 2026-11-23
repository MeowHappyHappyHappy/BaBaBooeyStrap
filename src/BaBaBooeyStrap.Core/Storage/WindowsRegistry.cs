using BaBaBooeyStrap.Core.Logging;
using Microsoft.Win32;

namespace BaBaBooeyStrap.Core.Storage;

public static class WindowsRegistry
{
    public const string AppName = "BaBaBooeyStrap";
    public const string AppRegistryRoot = @"Software\BaBaBooeyStrap";
    public const string StartupRunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static bool IsSafeClassesSubkey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        string trimmed = key.Trim();
        return !trimmed.Contains('\\') &&
               !trimmed.Contains('/') &&
               !trimmed.Contains("..", StringComparison.Ordinal);
    }

    private static bool IsSafeHandlerPath(string? handlerPath)
    {
        if (string.IsNullOrWhiteSpace(handlerPath)) return false;
        foreach (char ch in handlerPath)
        {
            if (ch == '"' || char.IsControl(ch)) return false;
        }
        return true;
    }

    public static bool RegisterProtocol(string key, string name, string handlerPath, string handlerParam = "\"%1\"")
    {
        if (!IsSafeClassesSubkey(key) || !IsSafeHandlerPath(handlerPath))
        {
            Logger.WarnStatic("WindowsRegistry", $"Rejected invalid protocol registration for key '{key}'.");
            return false;
        }

        try
        {
            string safeParam = string.IsNullOrWhiteSpace(handlerParam) ? "\"%1\"" : handlerParam;
            string command = $"\"{handlerPath}\" {safeParam}";
            string iconValue = $"\"{handlerPath}\",0";
            using var regKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{key.Trim()}");
            if (regKey == null) return false;

            using var defaultIcon = regKey.CreateSubKey("DefaultIcon");
            using var shellOpenCmd = regKey.CreateSubKey(@"shell\open\command");

            string expectedTitle = $"URL: {name} Protocol";
            if (regKey.GetValue("") as string != expectedTitle)
            {
                regKey.SetValue("", expectedTitle);
            }

            if (regKey.GetValue("URL Protocol") == null)
            {
                regKey.SetValue("URL Protocol", "");
            }

            if (defaultIcon != null && defaultIcon.GetValue("") as string != iconValue)
            {
                defaultIcon.SetValue("", iconValue);
            }

            if (shellOpenCmd != null && shellOpenCmd.GetValue("") as string != command)
            {
                shellOpenCmd.SetValue("", command);
            }

            return true;
        }
        catch (Exception ex)
        {
            Logger.WarnStatic("WindowsRegistry", $"Failed to register protocol '{key}': {ex.Message}");
            return false;
        }
    }

    public static string? ExtractExecutablePathFromCommand(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        string trimmed = commandLine.Trim();
        if (trimmed.StartsWith('"'))
        {
            int endQuote = trimmed.IndexOf('"', 1);
            if (endQuote > 1)
            {
                return trimmed[1..endQuote].Trim();
            }

            return trimmed.Trim('"').Trim();
        }

        int searchPos = 0;
        while (searchPos < trimmed.Length)
        {
            int exeIdx = trimmed.IndexOf(".exe", searchPos, StringComparison.OrdinalIgnoreCase);
            if (exeIdx < 0) break;

            int afterExe = exeIdx + 4;
            if (afterExe == trimmed.Length || char.IsWhiteSpace(trimmed[afterExe]))
            {
                return trimmed[..afterExe].Trim();
            }

            searchPos = afterExe;
        }

        int spaceIdx = trimmed.IndexOf(' ');
        return spaceIdx > 0 ? trimmed[..spaceIdx].Trim() : trimmed;
    }

    public static bool IsProtocolHandlerValid(string key, string? expectedHandlerPath = null)
    {
        try
        {
            using var regKey = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{key}", writable: false)
                ?? Registry.ClassesRoot.OpenSubKey(key, writable: false);
            if (regKey == null)
            {
                return false;
            }

            if (regKey.GetValue("URL Protocol") == null)
            {
                return false;
            }

            using var cmdKey = regKey.OpenSubKey(@"shell\open\command", writable: false);
            string? command = cmdKey?.GetValue("") as string;
            string? exePath = ExtractExecutablePathFromCommand(command);
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(expectedHandlerPath))
            {
                try
                {
                    if (!string.Equals(
                            Path.GetFullPath(exePath),
                            Path.GetFullPath(expectedHandlerPath),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }
                catch
                {
                    return false;
                }
            }
            else if (!Path.GetFileName(exePath).Equals($"{AppName}.exe", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool ArePlayerProtocolsRegistered(string? expectedHandlerPath = null) =>
        IsProtocolHandlerValid("roblox-player", expectedHandlerPath) &&
        IsProtocolHandlerValid("roblox", expectedHandlerPath);

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
        if (!IsSafeHandlerPath(handlerPath)) return;

        try
        {
            using var placeKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Roblox.Place");
            if (placeKey != null)
            {
                using var iconKey = placeKey.CreateSubKey("DefaultIcon");
                using var cmdKey = placeKey.CreateSubKey(@"shell\Open\command");

                placeKey.SetValue("", "Roblox Place");
                iconKey?.SetValue("", $"\"{handlerPath}\",0");
                cmdKey?.SetValue("", $"\"{handlerPath}\" -studio \"%1\"");
            }

            RegisterExtension(".rbxl");
            RegisterExtension(".rbxlx");
        }
        catch (Exception ex)
        {
            Logger.WarnStatic("WindowsRegistry", $"Failed to register Studio file associations: {ex.Message}");
        }

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
            using var key = Registry.CurrentUser.OpenSubKey(StartupRunRegistryPath, writable: true);
            if (key?.GetValue("RobloxPlayerBeta") is string val && val.Contains("RobloxPlayerBeta.exe", StringComparison.OrdinalIgnoreCase))
            {
                key.DeleteValue("RobloxPlayerBeta");
            }
        }
        catch { }
    }

    public static void SetAppStartup(string handlerPath, bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(StartupRunRegistryPath);
            if (key == null) return;

            if (enabled && IsSafeHandlerPath(handlerPath))
            {
                key.SetValue(AppName, $"\"{handlerPath}\" -quiet -nolaunch");
            }
            else
            {
                key.DeleteValue(AppName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Logger.WarnStatic("WindowsRegistry", $"Failed to update startup registry entry: {ex.Message}");
        }
    }

    public static bool IsAppStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRunRegistryPath, writable: false);
            return key?.GetValue(AppName) is string val && !string.IsNullOrWhiteSpace(val);
        }
        catch
        {
            return false;
        }
    }

    public static bool UnregisterProtocol(string key)
    {
        if (!IsSafeClassesSubkey(key))
        {
            Logger.WarnStatic("WindowsRegistry", $"Refused unsafe protocol key unregister: '{key}'.");
            return false;
        }

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{key.Trim()}", throwOnMissingSubKey: false);
            return true;
        }
        catch (Exception ex)
        {
            Logger.WarnStatic("WindowsRegistry", $"Failed to unregister protocol '{key}': {ex.Message}");
            return false;
        }
    }

    public static void RegisterUninstall(string handlerPath)
    {
        if (!IsSafeHandlerPath(handlerPath)) return;

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{AppName}");
            if (key == null) return;

            key.SetValue("DisplayName", AppName);
            key.SetValue("DisplayIcon", $"\"{handlerPath}\",0");
            key.SetValue("UninstallString", $"\"{handlerPath}\" -uninstall");
            key.SetValue("Publisher", AppName);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            Logger.WarnStatic("WindowsRegistry", $"Failed to register uninstall entry: {ex.Message}");
        }
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
        SetAppStartup(string.Empty, false);

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
