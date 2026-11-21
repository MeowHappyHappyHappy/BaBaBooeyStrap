using System.Diagnostics;
using System.IO;
using BaBaBooeyStrap.Core;
using BaBaBooeyStrap.Core.Interop;
using BaBaBooeyStrap.Core.Logging;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Storage;

namespace BaBaBooeyStrap.App.Services;

public sealed class InstallerService
{
    private readonly IPaths _paths;
    private readonly IJsonManager<State> _stateManager;
    private readonly ILogger _logger;

    public InstallerService(
        IPaths? paths = null,
        IJsonManager<State>? stateManager = null,
        ILogger? logger = null)
    {
        _paths = paths ?? Paths.Current;
        _stateManager = stateManager ?? new JsonManager<State>(_paths.StateFile);
        _logger = logger ?? Logger.Current;
    }

    public bool IsInstalledHere()
    {
        string currentExe = _paths.Process;
        if (string.IsNullOrWhiteSpace(currentExe)) return false;

        return string.Equals(
            Path.GetFullPath(currentExe),
            Path.GetFullPath(_paths.Application),
            StringComparison.OrdinalIgnoreCase);
    }

    public void Install(bool createDesktopShortcut = true, bool createStartMenuShortcuts = true, bool registerProtocols = true)
    {
        _paths.EnsureDirectoriesExist();

        string targetExe = _paths.Application;
        string currentExe = _paths.Process;

        if (!string.IsNullOrWhiteSpace(currentExe) &&
            File.Exists(currentExe) &&
            !string.Equals(Path.GetFullPath(currentExe), Path.GetFullPath(targetExe), StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                File.Copy(currentExe, targetExe, overwrite: true);
                _logger.Info("InstallerService", $"Copied executable to {targetExe}");
            }
            catch (Exception ex)
            {
                _logger.Warn("InstallerService", $"Failed to copy executable to {targetExe}: {ex.Message}");
                targetExe = currentExe;
            }
        }
        else if (!File.Exists(targetExe) && !string.IsNullOrWhiteSpace(currentExe) && File.Exists(currentExe))
        {
            targetExe = currentExe;
        }

        if (registerProtocols)
        {
            WindowsRegistry.RegisterPlayer(targetExe);
            WindowsRegistry.RegisterStudio(targetExe);
            WindowsRegistry.RegisterAppLocation(targetExe, _paths.Base);
            WindowsRegistry.RegisterUninstall(targetExe);
            WindowsRegistry.RemoveRobloxStartupEntry();
        }

        if (createStartMenuShortcuts)
        {
            CreateStartMenuShortcuts(targetExe);
        }

        if (createDesktopShortcut)
        {
            CreateDesktopShortcut(targetExe);
        }

        _stateManager.Prop.InstallLocation = _paths.Base;
        _stateManager.Save();
        _logger.Info("InstallerService", $"Installation completed at {_paths.Base}");
    }

    public void CreateStartMenuShortcuts(string? targetExe = null)
    {
        string exe = !string.IsNullOrWhiteSpace(targetExe) ? targetExe : ResolveHandlerExe();
        string folder = Path.Combine(_paths.WindowsStartMenu, "BaBaBooeyStrap");
        Directory.CreateDirectory(folder);

        WindowsShortcut.CreateShortcut(
            exe,
            Path.Combine(folder, "Play Roblox.lnk"),
            "-player",
            _paths.Base,
            "Launch Roblox via BaBaBooeyStrap",
            exe,
            0);

        WindowsShortcut.CreateShortcut(
            exe,
            Path.Combine(folder, "BaBaBooeyStrap Settings.lnk"),
            "-menu",
            _paths.Base,
            "Configure BaBaBooeyStrap settings",
            exe,
            0);
    }

    public void CreateDesktopShortcut(string? targetExe = null)
    {
        string exe = !string.IsNullOrWhiteSpace(targetExe) ? targetExe : ResolveHandlerExe();
        WindowsShortcut.CreateShortcut(
            exe,
            Path.Combine(_paths.Desktop, "Play Roblox.lnk"),
            "-player",
            _paths.Base,
            "Launch Roblox via BaBaBooeyStrap",
            exe,
            0);
    }

    public void Uninstall(bool removeDataDirectory = true, bool scheduleSelfDelete = true)
    {
        _logger.Info("InstallerService", "Uninstalling BaBaBooeyStrap...");

        WindowsRegistry.UnregisterAll();

        try
        {
            string startMenuFolder = Path.Combine(_paths.WindowsStartMenu, "BaBaBooeyStrap");
            if (Directory.Exists(startMenuFolder))
            {
                Directory.Delete(startMenuFolder, recursive: true);
            }
        }
        catch { }

        try
        {
            string desktopLink = Path.Combine(_paths.Desktop, "Play Roblox.lnk");
            if (File.Exists(desktopLink))
            {
                File.Delete(desktopLink);
            }
        }
        catch { }

        if (removeDataDirectory && Directory.Exists(_paths.Base))
        {
            if (scheduleSelfDelete && IsInstalledHere())
            {
                try
                {
                    string escapedBase = _paths.Base.Replace("\"", "\\\"");
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c timeout /t 2 /nobreak >nul & rmdir /s /q \"{escapedBase}\"",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    });
                }
                catch { }
            }
            else
            {
                try
                {
                    Directory.Delete(_paths.Base, recursive: true);
                }
                catch { }
            }
        }
    }

    private string ResolveHandlerExe()
    {
        if (File.Exists(_paths.Application)) return _paths.Application;
        if (!string.IsNullOrWhiteSpace(_paths.Process) && File.Exists(_paths.Process)) return _paths.Process;
        return _paths.Application;
    }
}
