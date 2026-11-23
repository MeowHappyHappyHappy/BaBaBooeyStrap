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
                            if (IsFileOutOfSync(srcFile, dstFile))
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
                            if (IsFileOutOfSync(srcFile, dstFile))
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
            string tempExe = $"{targetExe}.{Guid.NewGuid():N}.tmp";
            try
            {
                string? targetDir = Path.GetDirectoryName(targetExe);
                if (!string.IsNullOrWhiteSpace(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                File.Copy(currentExe, tempExe, overwrite: true);
                File.Move(tempExe, targetExe, overwrite: true);
                CopyCompanionFiles(Path.GetDirectoryName(currentExe), targetDir);
                _logger.Info("InstallerService", $"Copied executable and companion files to {targetExe}");
                return targetExe;
            }
            catch (Exception ex)
            {
                try { if (File.Exists(tempExe)) File.Delete(tempExe); } catch { }
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
            try
            {
                CreateStartMenuShortcuts(targetExe);
            }
            catch (Exception ex)
            {
                _logger.Warn("InstallerService", $"Failed to create Start Menu shortcuts: {ex.Message}");
            }
        }

        if (createDesktopShortcut)
        {
            try
            {
                CreateDesktopShortcut(targetExe);
            }
            catch (Exception ex)
            {
                _logger.Warn("InstallerService", $"Failed to create Desktop shortcut: {ex.Message}");
            }
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

    private static bool IsFileOutOfSync(string srcFile, string dstFile)
    {
        if (!File.Exists(dstFile)) return true;
        var srcInfo = new FileInfo(srcFile);
        var dstInfo = new FileInfo(dstFile);
        return srcInfo.Length != dstInfo.Length ||
               Math.Abs((srcInfo.LastWriteTimeUtc - dstInfo.LastWriteTimeUtc).TotalSeconds) > 2;
    }

    private void TryCopyFileIfChanged(string srcFile, string dstFile)
    {
        string tempDst = $"{dstFile}.{Guid.NewGuid():N}.tmp";
        try
        {
            if (!IsFileOutOfSync(srcFile, dstFile))
            {
                return;
            }

            File.Copy(srcFile, tempDst, overwrite: true);
            File.Move(tempDst, dstFile, overwrite: true);
        }
        catch (Exception ex)
        {
            try { if (File.Exists(tempDst)) File.Delete(tempDst); } catch { }
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
            if (!IsSafeToDeleteBaseDirectory(_paths.Base))
            {
                _logger.Error("InstallerService", $"Refused unsafe recursive directory deletion for base path '{_paths.Base}'.");
                return;
            }

            string fullBase = Path.GetFullPath(_paths.Base).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (scheduleSelfDelete && IsInstalledHere())
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };
                    psi.ArgumentList.Add("-NoProfile");
                    psi.ArgumentList.Add("-NonInteractive");
                    psi.ArgumentList.Add("-WindowStyle");
                    psi.ArgumentList.Add("Hidden");
                    psi.ArgumentList.Add("-Command");
                    psi.ArgumentList.Add("Start-Sleep -Seconds 2; if (Test-Path -LiteralPath $env:BBBS_UNINSTALL_DIR) { Remove-Item -LiteralPath $env:BBBS_UNINSTALL_DIR -Recurse -Force -ErrorAction SilentlyContinue }");
                    psi.Environment["BBBS_UNINSTALL_DIR"] = fullBase;

                    using var proc = Process.Start(psi);
                }
                catch (Exception ex)
                {
                    _logger.Warn("InstallerService", $"Failed to schedule directory cleanup: {ex.Message}");
                }
            }
            else
            {
                try
                {
                    Directory.Delete(fullBase, recursive: true);
                }
                catch (Exception ex)
                {
                    _logger.Warn("InstallerService", $"Failed to delete base directory '{fullBase}': {ex.Message}");
                }
            }
        }
    }

    internal static bool IsSafeToDeleteBaseDirectory(string? baseDir)
    {
        if (string.IsNullOrWhiteSpace(baseDir)) return false;

        string full;
        try
        {
            full = Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return false;
        }

        if (full.StartsWith(@"\\", StringComparison.Ordinal)) return false;

        string? root = Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) return false;

        string[] forbiddenDirs =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.GetTempPath()
        ];

        foreach (string forbidden in forbiddenDirs)
        {
            if (string.IsNullOrWhiteSpace(forbidden)) continue;
            string normForbidden = Path.GetFullPath(forbidden).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(full, normForbidden, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        string leafName = Path.GetFileName(full);
        bool hasAppLeafName = leafName.Contains("BaBaBooeyStrap", StringComparison.OrdinalIgnoreCase);
        bool hasAppMarkers = File.Exists(Path.Combine(full, "Settings.json")) ||
                             File.Exists(Path.Combine(full, "State.json")) ||
                             File.Exists(Path.Combine(full, "BaBaBooeyStrap.exe"));
        return hasAppLeafName || hasAppMarkers;
    }

    private string ResolveHandlerExe()
    {
        if (File.Exists(_paths.Application)) return _paths.Application;
        if (!string.IsNullOrWhiteSpace(_paths.Process) && File.Exists(_paths.Process)) return _paths.Process;
        return _paths.Application;
    }
}
