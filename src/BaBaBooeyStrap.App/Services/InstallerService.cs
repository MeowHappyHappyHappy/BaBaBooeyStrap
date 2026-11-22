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

    private static readonly string[] CompanionRootFilePatterns =
    [
        "*.dll",
        "*.deps.json",
        "*.runtimeconfig.json"
    ];

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

    public bool NeedsInstallSync()
    {
        string targetExe = _paths.Application;
        if (!File.Exists(targetExe))
        {
            return true;
        }

        string currentExe = _paths.Process;
        if (!string.IsNullOrWhiteSpace(currentExe) &&
            File.Exists(currentExe) &&
            IsValidBootstrapperExecutable(currentExe) &&
            !string.Equals(Path.GetFullPath(currentExe), Path.GetFullPath(targetExe), StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var currentInfo = new FileInfo(currentExe);
                var targetInfo = new FileInfo(targetExe);
                if (currentInfo.Length != targetInfo.Length ||
                    Math.Abs((currentInfo.LastWriteTimeUtc - targetInfo.LastWriteTimeUtc).TotalSeconds) > 2)
                {
                    return true;
                }

                string? sourceDir = Path.GetDirectoryName(currentExe);
                string? targetDir = Path.GetDirectoryName(targetExe);
                if (!string.IsNullOrWhiteSpace(sourceDir) && !string.IsNullOrWhiteSpace(targetDir))
                {
                    foreach (string pattern in CompanionRootFilePatterns)
                    {
                        foreach (string srcFile in Directory.GetFiles(sourceDir, pattern, SearchOption.TopDirectoryOnly))
                        {
                            string dstFile = Path.Combine(targetDir, Path.GetFileName(srcFile));
                            if (!File.Exists(dstFile))
                            {
                                return true;
                            }
                        }
                    }

                    string srcRuntimes = Path.Combine(sourceDir, "runtimes");
                    if (Directory.Exists(srcRuntimes))
                    {
                        foreach (string srcFile in Directory.GetFiles(srcRuntimes, "*", SearchOption.AllDirectories))
                        {
                            string rel = Path.GetRelativePath(sourceDir, srcFile);
                            string dstFile = Path.Combine(targetDir, rel);
                            if (!File.Exists(dstFile))
                            {
                                return true;
                            }
                        }
                    }
                }
            }
            catch
            {
                return true;
            }
        }

        return false;
    }

    public string EnsureInstalledExeAndCompanionFiles()
    {
        _paths.EnsureDirectoriesExist();

        string targetExe = _paths.Application;
        string currentExe = _paths.Process;

        if (!string.IsNullOrWhiteSpace(currentExe) &&
            File.Exists(currentExe) &&
            IsValidBootstrapperExecutable(currentExe) &&
            !string.Equals(Path.GetFullPath(currentExe), Path.GetFullPath(targetExe), StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                string? targetDir = Path.GetDirectoryName(targetExe);
                if (!string.IsNullOrWhiteSpace(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                File.Copy(currentExe, targetExe, overwrite: true);
                CopyCompanionFiles(Path.GetDirectoryName(currentExe), targetDir);
                _logger.Info("InstallerService", $"Copied executable and companion files to {targetExe}");
                return targetExe;
            }
            catch (Exception ex)
            {
                _logger.Warn("InstallerService", $"Failed to copy executable to {targetExe}: {ex.Message}");
                return File.Exists(targetExe) ? targetExe : currentExe;
            }
        }

        if (!File.Exists(targetExe) && !string.IsNullOrWhiteSpace(currentExe) && File.Exists(currentExe))
        {
            return currentExe;
        }

        return targetExe;
    }

    public void Install(bool createDesktopShortcut = true, bool createStartMenuShortcuts = true, bool registerProtocols = true)
    {
        string targetExe = EnsureInstalledExeAndCompanionFiles();

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
        _logger.Info("InstallerService", $"Installation completed at {_paths.Base} (Handler: {targetExe})");
    }

    public string RegisterPlayerProtocols()
    {
        string targetExe = EnsureInstalledExeAndCompanionFiles();
        WindowsRegistry.RegisterPlayer(targetExe);
        WindowsRegistry.RegisterAppLocation(targetExe, _paths.Base);
        WindowsRegistry.RegisterUninstall(targetExe);
        WindowsRegistry.RemoveRobloxStartupEntry();
        return targetExe;
    }

    public string RegisterStudioProtocols()
    {
        string targetExe = EnsureInstalledExeAndCompanionFiles();
        WindowsRegistry.RegisterStudio(targetExe);
        return targetExe;
    }

    public void EnsureProtocolsPreserved()
    {
        string targetExe = ResolveHandlerExe();
        if (string.IsNullOrWhiteSpace(targetExe) || !File.Exists(targetExe))
        {
            targetExe = EnsureInstalledExeAndCompanionFiles();
        }

        if (!string.IsNullOrWhiteSpace(targetExe) && File.Exists(targetExe))
        {
            if (!WindowsRegistry.ArePlayerProtocolsRegistered(targetExe))
            {
                WindowsRegistry.RegisterPlayer(targetExe);
            }
            WindowsRegistry.RemoveRobloxStartupEntry();
        }
    }

    private static bool IsValidBootstrapperExecutable(string exePath)
    {
        string fileName = Path.GetFileName(exePath);
        return fileName.Equals("BaBaBooeyStrap.exe", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("BaBaBooeyStrap.App.exe", StringComparison.OrdinalIgnoreCase);
    }

    private void CopyCompanionFiles(string? sourceDir, string? targetDir)
    {
        if (string.IsNullOrWhiteSpace(sourceDir) ||
            string.IsNullOrWhiteSpace(targetDir) ||
            !Directory.Exists(sourceDir))
        {
            return;
        }

        Directory.CreateDirectory(targetDir);

        foreach (string pattern in CompanionRootFilePatterns)
        {
            foreach (string srcFile in Directory.GetFiles(sourceDir, pattern, SearchOption.TopDirectoryOnly))
            {
                string dstFile = Path.Combine(targetDir, Path.GetFileName(srcFile));
                TryCopyFileIfChanged(srcFile, dstFile);
            }
        }

        string srcRuntimes = Path.Combine(sourceDir, "runtimes");
        if (Directory.Exists(srcRuntimes))
        {
            foreach (string srcFile in Directory.GetFiles(srcRuntimes, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(sourceDir, srcFile);
                string dstFile = Path.Combine(targetDir, rel);
                string? dstSubDir = Path.GetDirectoryName(dstFile);
                if (!string.IsNullOrWhiteSpace(dstSubDir))
                {
                    Directory.CreateDirectory(dstSubDir);
                }
                TryCopyFileIfChanged(srcFile, dstFile);
            }
        }
    }

    private void TryCopyFileIfChanged(string srcFile, string dstFile)
    {
        try
        {
            if (File.Exists(dstFile))
            {
                var srcInfo = new FileInfo(srcFile);
                var dstInfo = new FileInfo(dstFile);
                if (srcInfo.Length == dstInfo.Length &&
                    Math.Abs((srcInfo.LastWriteTimeUtc - dstInfo.LastWriteTimeUtc).TotalSeconds) <= 2)
                {
                    return;
                }
            }

            File.Copy(srcFile, dstFile, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.Warn("InstallerService", $"Failed to copy companion file '{Path.GetFileName(srcFile)}': {ex.Message}");
        }
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
