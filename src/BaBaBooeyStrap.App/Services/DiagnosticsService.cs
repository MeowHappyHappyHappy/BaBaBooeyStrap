using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BaBaBooeyStrap.Core;
using BaBaBooeyStrap.Core.FastFlags;
using BaBaBooeyStrap.Core.Logging;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Integrations.Cleaner;

namespace BaBaBooeyStrap.App.Services;

public static class DiagnosticsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static void ResetFastFlags(IFastFlagManager fastFlagManager)
    {
        ArgumentNullException.ThrowIfNull(fastFlagManager);

        foreach (string key in fastFlagManager.Flags.Keys.ToList())
        {
            fastFlagManager.RemoveFlag(key);
        }
        fastFlagManager.Save();
    }

    public static CleanerReport ClearAllCachesImmediate(IPaths paths, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var cleaner = new CacheCleaner(paths, logger, () => DateTime.UtcNow.AddSeconds(5));
        var policy = new CleanerPolicy(
            LogAgeThreshold: TimeSpan.Zero,
            MaxTotalSizeBytes: 0,
            CleanLogs: true,
            CleanCrashDumps: true,
            CleanLauncherLogs: true,
            CleanHttpCache: true,
            CleanStudioCache: true);

        return cleaner.ExecuteCleanup(policy);
    }

    public static void RepairProtocols(InstallerService installerService)
    {
        ArgumentNullException.ThrowIfNull(installerService);
        installerService.Install(
            createDesktopShortcut: false,
            createStartMenuShortcuts: false,
            registerProtocols: true);
    }

    public static void ExportDiagnosticsZip(
        string outputZipPath,
        IPaths paths,
        Settings settings,
        State state,
        RobloxState robloxState,
        string? appVersion = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputZipPath);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(robloxState);

        string? outDir = Path.GetDirectoryName(outputZipPath);
        if (!string.IsNullOrWhiteSpace(outDir))
        {
            Directory.CreateDirectory(outDir);
        }

        string tempZip = $"{outputZipPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var fs = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                string resolvedVersion = !string.IsNullOrWhiteSpace(appVersion)
                    ? appVersion
                    : typeof(DiagnosticsService).Assembly.GetName().Version?.ToString() ?? "1.0.0";

                var sysInfo = new StringBuilder();
                sysInfo.AppendLine($"AppVersion: {resolvedVersion}");
                sysInfo.AppendLine($"ExportedAtUtc: {DateTimeOffset.UtcNow:O}");
                sysInfo.AppendLine($"OS: {RuntimeInformation.OSDescription}");
                sysInfo.AppendLine($"OSArchitecture: {RuntimeInformation.OSArchitecture}");
                sysInfo.AppendLine($"Framework: {RuntimeInformation.FrameworkDescription}");
                sysInfo.AppendLine($"BasePath: {paths.Base}");
                sysInfo.AppendLine($"LogsPath: {paths.Logs}");
                sysInfo.AppendLine($"RobloxLogsPath: {paths.RobloxLogs}");
                sysInfo.AppendLine($"ProcessPath: {paths.Process}");

                AddTextEntry(archive, "system_info.txt", sysInfo.ToString());
                AddTextEntry(archive, "settings.json", JsonSerializer.Serialize(settings, JsonOptions));
                AddTextEntry(archive, "state.json", JsonSerializer.Serialize(state, JsonOptions));
                AddTextEntry(archive, "roblox_state.json", JsonSerializer.Serialize(robloxState, JsonOptions));

                string clientSettingsPath = paths.GetClientSettingsPath();
                if (File.Exists(clientSettingsPath))
                {
                    AddFileCopySafe(archive, clientSettingsPath, "ClientAppSettings.json");
                }

                AddRecentLogs(archive, paths.Logs, "logs/app", maxFiles: 5);
                AddRecentLogs(archive, paths.RobloxLogs, "logs/roblox", maxFiles: 3);
            }

            File.Move(tempZip, outputZipPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            throw;
        }
    }

    private static void AddTextEntry(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private static void AddRecentLogs(ZipArchive archive, string directory, string entryPrefix, int maxFiles)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        try
        {
            var recentLogs = new DirectoryInfo(directory)
                .EnumerateFiles("*.log", SearchOption.TopDirectoryOnly)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(maxFiles)
                .ToList();

            foreach (var log in recentLogs)
            {
                AddFileCopySafe(archive, log.FullName, $"{entryPrefix}/{log.Name}");
            }
        }
        catch { }
    }

    private static void AddFileCopySafe(ZipArchive archive, string sourceFilePath, string entryName)
    {
        try
        {
            using var source = new FileStream(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            using var dest = entry.Open();
            source.CopyTo(dest);
        }
        catch { }
    }
}
