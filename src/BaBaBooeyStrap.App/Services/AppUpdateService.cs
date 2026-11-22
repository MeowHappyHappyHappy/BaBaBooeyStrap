using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using BaBaBooeyStrap.Core;
using BaBaBooeyStrap.Core.Logging;

namespace BaBaBooeyStrap.App.Services;

public sealed record AppUpdateInfo(
    Version CurrentVersion,
    Version LatestVersion,
    string TagName,
    string ReleaseNotes,
    string HtmlUrl,
    string? AssetDownloadUrl,
    string? AssetFileName,
    long AssetSizeBytes,
    bool IsUpdateAvailable);

public interface IAppUpdateService
{
    Task<AppUpdateInfo> CheckForUpdateAsync(CancellationToken ct = default);
    Task<string?> DownloadAndScheduleInstallAsync(AppUpdateInfo info, IProgress<double>? progress = null, CancellationToken ct = default);
}

public sealed class AppUpdateService : IAppUpdateService
{
    public const string DefaultRepositoryOwner = "MeowHappyHappyHappy";
    public const string DefaultRepositoryName = "BaBaBooeyStrap";

    private readonly IPaths _paths;
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private readonly Version _currentVersion;
    private readonly string _repositoryOwner;
    private readonly string _repositoryName;

    public AppUpdateService(
        IPaths? paths = null,
        HttpClient? httpClient = null,
        ILogger? logger = null,
        Version? currentVersionOverride = null,
        string? repositoryOwner = null,
        string? repositoryName = null)
    {
        _paths = paths ?? Paths.Current;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _logger = logger ?? Logger.Current;
        _currentVersion = NormalizeVersion(currentVersionOverride ?? ResolveAssemblyVersion());
        _repositoryOwner = string.IsNullOrWhiteSpace(repositoryOwner) ? DefaultRepositoryOwner : repositoryOwner.Trim();
        _repositoryName = string.IsNullOrWhiteSpace(repositoryName) ? DefaultRepositoryName : repositoryName.Trim();
    }

    public Version CurrentVersion => _currentVersion;

    public static Version? ParseVersion(string? rawTag)
    {
        if (string.IsNullOrWhiteSpace(rawTag))
        {
            return null;
        }

        string cleaned = rawTag.Trim();
        if (cleaned.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned[1..].Trim();
        }

        int plusIdx = cleaned.IndexOf('+');
        if (plusIdx >= 0)
        {
            cleaned = cleaned[..plusIdx];
        }

        int dashIdx = cleaned.IndexOf('-');
        if (dashIdx >= 0)
        {
            cleaned = cleaned[..dashIdx];
        }

        if (!Version.TryParse(cleaned, out var parsed))
        {
            return null;
        }

        return NormalizeVersion(parsed);
    }

    private static Version NormalizeVersion(Version v) =>
        new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build), Math.Max(0, v.Revision));

    private static Version ResolveAssemblyVersion()
    {
        var asm = typeof(AppUpdateService).Assembly;
        var infoVer = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (ParseVersion(infoVer) is { } parsedInfo)
        {
            return parsedInfo;
        }

        return asm.GetName().Version ?? new Version(1, 0, 0, 0);
    }

    public async Task<AppUpdateInfo> CheckForUpdateAsync(CancellationToken ct = default)
    {
        string url = $"https://api.github.com/repos/{_repositoryOwner}/{_repositoryName}/releases/latest";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd($"BaBaBooeyStrap/{_currentVersion}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"GitHub Releases API returned {(int)response.StatusCode} ({response.ReasonPhrase}).");
        }

        string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string tagName = root.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() ?? string.Empty : string.Empty;
        string htmlUrl = root.TryGetProperty("html_url", out var htmlProp)
            ? htmlProp.GetString() ?? $"https://github.com/{_repositoryOwner}/{_repositoryName}/releases"
            : $"https://github.com/{_repositoryOwner}/{_repositoryName}/releases";
        string body = root.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() ?? string.Empty : string.Empty;

        Version latestVersion = ParseVersion(tagName) ?? _currentVersion;

        string? assetDownloadUrl = null;
        string? assetName = null;
        long assetSize = 0;

        if (root.TryGetProperty("assets", out var assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assetsEl.EnumerateArray())
            {
                string? name = asset.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                string? dlUrl = asset.TryGetProperty("browser_download_url", out var dlProp) ? dlProp.GetString() : null;
                long size = asset.TryGetProperty("size", out var sizeProp) && sizeProp.TryGetInt64(out long s) ? s : 0;

                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(dlUrl))
                {
                    continue;
                }

                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    if (assetDownloadUrl == null ||
                        name.Contains("BaBaBooeyStrap", StringComparison.OrdinalIgnoreCase))
                    {
                        assetName = name;
                        assetDownloadUrl = dlUrl;
                        assetSize = size;
                        if (name.Contains("BaBaBooeyStrap", StringComparison.OrdinalIgnoreCase))
                        {
                            break;
                        }
                    }
                }
            }
        }

        bool isUpdateAvailable = latestVersion > _currentVersion;
        return new AppUpdateInfo(
            _currentVersion,
            latestVersion,
            string.IsNullOrWhiteSpace(tagName) ? latestVersion.ToString() : tagName,
            body,
            htmlUrl,
            assetDownloadUrl,
            assetName,
            assetSize,
            isUpdateAvailable);
    }

    public async Task<string?> DownloadAndScheduleInstallAsync(
        AppUpdateInfo info,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(info);

        if (string.IsNullOrWhiteSpace(info.AssetDownloadUrl))
        {
            return null;
        }

        Directory.CreateDirectory(_paths.TempUpdates);
        string downloadedExe = Path.Combine(_paths.TempUpdates, $"BaBaBooeyStrap_{info.LatestVersion}.exe");

        using (var request = new HttpRequestMessage(HttpMethod.Get, info.AssetDownloadUrl))
        {
            request.Headers.UserAgent.ParseAdd($"BaBaBooeyStrap/{_currentVersion}");
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            long totalBytes = response.Content.Headers.ContentLength ?? info.AssetSizeBytes;
            await using var sourceStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var fileStream = new FileStream(downloadedExe, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

            byte[] buffer = new byte[81920];
            long totalRead = 0;
            int read;
            while ((read = await sourceStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                totalRead += read;
                if (totalBytes > 0)
                {
                    progress?.Report(Math.Clamp((double)totalRead / totalBytes, 0.0, 1.0));
                }
            }
            progress?.Report(1.0);
        }

        string currentExe = !string.IsNullOrWhiteSpace(_paths.Process)
            ? _paths.Process
            : _paths.Application;

        string scriptPath = Path.Combine(_paths.TempUpdates, $"apply_update_{Guid.NewGuid():N}.cmd");
        string scriptContent = BuildSelfUpdateScript(
            Environment.ProcessId,
            downloadedExe,
            currentExe,
            _paths.Application);

        await File.WriteAllTextAsync(scriptPath, scriptContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct)
            .ConfigureAwait(false);

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{scriptPath}\"",
            CreateNoWindow = true,
            UseShellExecute = false
        });

        _logger.Info("AppUpdateService", $"Scheduled self-update script '{scriptPath}' for version {info.LatestVersion}.");
        return scriptPath;
    }

    internal static string BuildSelfUpdateScript(
        int currentPid,
        string downloadedExe,
        string currentExe,
        string? installedExe)
    {
        string targetExe = !string.IsNullOrWhiteSpace(currentExe) ? currentExe : (installedExe ?? string.Empty);
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("setlocal");
        sb.AppendLine(":waitloop");
        sb.AppendLine($"tasklist /FI \"PID eq {currentPid}\" 2>NUL | find /I \"{currentPid}\" >NUL");
        sb.AppendLine("if %errorlevel%==0 (");
        sb.AppendLine("  timeout /t 1 /nobreak >NUL");
        sb.AppendLine("  goto waitloop");
        sb.AppendLine(")");
        sb.AppendLine($"copy /Y \"{downloadedExe}\" \"{targetExe}\" >NUL");
        if (!string.IsNullOrWhiteSpace(installedExe) &&
            !string.Equals(Path.GetFullPath(targetExe), Path.GetFullPath(installedExe), StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine($"copy /Y \"{downloadedExe}\" \"{installedExe}\" >NUL");
        }
        sb.AppendLine($"del /F /Q \"{downloadedExe}\" >NUL 2>&1");
        sb.AppendLine($"start \"\" \"{targetExe}\" -menu");
        sb.AppendLine("del /F /Q \"%~f0\" >NUL 2>&1");
        return sb.ToString();
    }
}
