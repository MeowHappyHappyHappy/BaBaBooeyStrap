using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BaBaBooeyStrap.Core;
using BaBaBooeyStrap.Core.FastFlags;
using BaBaBooeyStrap.Core.Logging;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Integrations.Cleaner;

namespace BaBaBooeyStrap.App.Services;

public static partial class DiagnosticsService
{
    private const int MaxLogFileExportBytes = 2 * 1024 * 1024;

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

    public static Task ExportDiagnosticsZipAsync(
        string outputZipPath,
        IPaths paths,
        Settings settings,
        State state,
        RobloxState robloxState,
        string? appVersion = null,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            ExportDiagnosticsZip(outputZipPath, paths, settings, state, robloxState, appVersion);
        }, ct);
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
                sysInfo.AppendLine($"BasePath: {SanitizeText(paths.Base, paths)}");
                sysInfo.AppendLine($"LogsPath: {SanitizeText(paths.Logs, paths)}");
                sysInfo.AppendLine($"RobloxLogsPath: {SanitizeText(paths.RobloxLogs, paths)}");
                sysInfo.AppendLine($"ProcessPath: {SanitizeText(paths.Process, paths)}");

                AddTextEntry(archive, "system_info.txt", SanitizeText(sysInfo.ToString(), paths));
                AddTextEntry(archive, "settings.json", SerializeSanitizedSettings(settings, paths));
                AddTextEntry(archive, "state.json", SerializeSanitizedState(state, paths));
                AddTextEntry(archive, "roblox_state.json", SanitizeText(JsonSerializer.Serialize(robloxState, JsonOptions), paths));

                string clientSettingsPath = paths.GetClientSettingsPath();
                if (File.Exists(clientSettingsPath))
                {
                    AddSanitizedFileCopySafe(archive, clientSettingsPath, "ClientAppSettings.json", paths);
                }

                AddRecentLogs(archive, paths.Logs, "logs/app", maxFiles: 5, paths);
                AddRecentLogs(archive, paths.RobloxLogs, "logs/roblox", maxFiles: 3, paths);
            }

            File.Move(tempZip, outputZipPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            throw;
        }
    }

    private static string SerializeSanitizedSettings(Settings settings, IPaths paths)
    {
        var node = JsonSerializer.SerializeToNode(settings, JsonOptions) as JsonObject;
        if (node == null)
        {
            return SanitizeText(JsonSerializer.Serialize(settings, JsonOptions), paths);
        }

        if (node["CustomIntegrations"] is JsonArray integrations)
        {
            foreach (var item in integrations.OfType<JsonObject>())
            {
                if (item["LaunchArgs"] is JsonValue argsVal &&
                    argsVal.TryGetValue<string>(out string? args) &&
                    !string.IsNullOrWhiteSpace(args))
                {
                    item["LaunchArgs"] = "[REDACTED]";
                }
            }
        }

        if (node["BootstrapperIconCustomLocation"] is JsonValue bootIcon &&
            bootIcon.TryGetValue<string>(out string? bootPath) &&
            !string.IsNullOrEmpty(bootPath))
        {
            node["BootstrapperIconCustomLocation"] = SanitizeText(bootPath, paths);
        }

        if (node["RobloxIconCustomLocation"] is JsonValue rbxIcon &&
            rbxIcon.TryGetValue<string>(out string? rbxPath) &&
            !string.IsNullOrEmpty(rbxPath))
        {
            node["RobloxIconCustomLocation"] = SanitizeText(rbxPath, paths);
        }

        return SanitizeText(node.ToJsonString(JsonOptions), paths);
    }

    private static string SerializeSanitizedState(State state, IPaths paths)
    {
        var node = JsonSerializer.SerializeToNode(state, JsonOptions) as JsonObject;
        if (node == null)
        {
            return SanitizeText(JsonSerializer.Serialize(state, JsonOptions), paths);
        }

        if (node["LastTargetInput"] is JsonValue targetVal &&
            targetVal.TryGetValue<string>(out string? target) &&
            !string.IsNullOrWhiteSpace(target))
        {
            node["LastTargetInput"] = "[REDACTED]";
        }

        if (node["InstallLocation"] is JsonValue installVal &&
            installVal.TryGetValue<string>(out string? installLoc) &&
            !string.IsNullOrEmpty(installLoc))
        {
            node["InstallLocation"] = SanitizeText(installLoc, paths);
        }

        if (node["AccountWindowBounds"] is JsonObject boundsObj)
        {
            var anonymized = new JsonObject();
            int idx = 1;
            foreach (var kvp in boundsObj)
            {
                anonymized[$"Account_{idx++}"] = kvp.Value?.DeepClone();
            }
            node["AccountWindowBounds"] = anonymized;
        }

        return SanitizeText(node.ToJsonString(JsonOptions), paths);
    }

    internal static string SanitizeText(string? text, IPaths? paths = null)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        string redacted = Logger.RedactSensitiveText(text);

        if (paths != null)
        {
            if (!string.IsNullOrEmpty(paths.UserProfile))
            {
                redacted = redacted.Replace(paths.UserProfile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
            }
            if (!string.IsNullOrEmpty(paths.LocalAppData))
            {
                redacted = redacted.Replace(paths.LocalAppData, "%LOCALAPPDATA%", StringComparison.OrdinalIgnoreCase);
            }
        }

        redacted = WindowsUsersPathRegex().Replace(redacted, "$1%USER%");
        redacted = TicketParamRegex().Replace(redacted, "$1=[REDACTED]");
        return redacted;
    }

    [GeneratedRegex(@"([A-Za-z]:\\Users\\)[^\\/:*?""<>|\r\n]+", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 100)]
    private static partial Regex WindowsUsersPathRegex();

    [GeneratedRegex(@"\b(ticket|authenticationTicket)=([^&\s""'+`#]+)", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 100)]
    private static partial Regex TicketParamRegex();

    private static void AddTextEntry(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private static void AddRecentLogs(ZipArchive archive, string directory, string entryPrefix, int maxFiles, IPaths paths)
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
                AddSanitizedFileCopySafe(archive, log.FullName, $"{entryPrefix}/{log.Name}", paths);
            }
        }
        catch { }
    }

    private static void AddSanitizedFileCopySafe(ZipArchive archive, string sourceFilePath, string entryName, IPaths paths)
    {
        try
        {
            using var source = new FileStream(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            bool truncated = false;
            if (source.Length > MaxLogFileExportBytes)
            {
                source.Seek(-MaxLogFileExportBytes, SeekOrigin.End);
                truncated = true;
            }

            using var reader = new StreamReader(source, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string raw = reader.ReadToEnd();
            string sanitized = SanitizeText(raw, paths);
            if (truncated)
            {
                sanitized = $"[... truncated to last {MaxLogFileExportBytes / (1024 * 1024)} MB ...]{Environment.NewLine}{sanitized}";
            }

            AddTextEntry(archive, entryName, sanitized);
        }
        catch { }
    }
}
