using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
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
    bool IsUpdateAvailable,
    string? AssetSha256 = null);

public interface IAppUpdateService
{
    Task<AppUpdateInfo> CheckForUpdateAsync(CancellationToken ct = default);
    Task<string?> DownloadAndScheduleInstallAsync(AppUpdateInfo info, IProgress<double>? progress = null, CancellationToken ct = default);
}

public sealed class AppUpdateService : IAppUpdateService
{
    public const string DefaultRepositoryOwner = "MeowHappyHappyHappy";
    public const string DefaultRepositoryName = "BaBaBooeyStrap";
    private const long MaxUpdateFileBytes = 256L * 1024 * 1024;
    private const int MaxChecksumsFileBytes = 128 * 1024;

    private static readonly HttpClient FallbackHttpClient = CreateDefaultHttpClient();
    private static readonly TimeSpan MetadataCheckTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DownloadReadIdleTimeout = TimeSpan.FromSeconds(30);

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
        _httpClient = httpClient ?? FallbackHttpClient;
        _logger = logger ?? Logger.Current;
        _currentVersion = NormalizeVersion(currentVersionOverride ?? ResolveAssemblyVersion());
        _repositoryOwner = string.IsNullOrWhiteSpace(repositoryOwner) ? DefaultRepositoryOwner : repositoryOwner.Trim();
        _repositoryName = string.IsNullOrWhiteSpace(repositoryName) ? DefaultRepositoryName : repositoryName.Trim();
    }

    private static HttpClient CreateDefaultHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
    }

    public Version CurrentVersion => _currentVersion;

    public static bool IsAllowedGitHubWebUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string host = uri.Host;
        return host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAllowedGitHubDownloadUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string host = uri.Host;
        return host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

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
        string fallbackHtmlUrl = $"https://github.com/{_repositoryOwner}/{_repositoryName}/releases";
        string url = $"https://api.github.com/repos/{_repositoryOwner}/{_repositoryName}/releases/latest";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd($"BaBaBooeyStrap/{_currentVersion}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var checkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        checkCts.CancelAfter(MetadataCheckTimeout);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, checkCts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"GitHub Releases API returned {(int)response.StatusCode} ({response.ReasonPhrase}).");
        }

        string json = await response.Content.ReadAsStringAsync(checkCts.Token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string tagName = root.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() ?? string.Empty : string.Empty;
        string? rawHtmlUrl = root.TryGetProperty("html_url", out var htmlProp) ? htmlProp.GetString() : null;
        string htmlUrl = IsAllowedGitHubWebUrl(rawHtmlUrl) ? rawHtmlUrl! : fallbackHtmlUrl;
        string body = root.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() ?? string.Empty : string.Empty;

        Version latestVersion = ParseVersion(tagName) ?? _currentVersion;

        string? assetDownloadUrl = null;
        string? assetName = null;
        long assetSize = 0;
        string? assetSha256 = null;
        string? checksumsUrl = null;

        if (root.TryGetProperty("assets", out var assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assetsEl.EnumerateArray())
            {
                string? name = asset.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                string? dlUrl = asset.TryGetProperty("browser_download_url", out var dlProp) ? dlProp.GetString() : null;
                long size = asset.TryGetProperty("size", out var sizeProp) && sizeProp.TryGetInt64(out long s) ? s : 0;
                string? digest = asset.TryGetProperty("digest", out var digestProp) ? digestProp.GetString() : null;

                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(dlUrl) || !IsAllowedGitHubDownloadUrl(dlUrl))
                {
                    continue;
                }

                if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("SHA256SUMS", StringComparison.OrdinalIgnoreCase))
                {
                    checksumsUrl = dlUrl;
                    continue;
                }

                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    bool isPreferred = name.Contains("BaBaBooeyStrap", StringComparison.OrdinalIgnoreCase);
                    if (assetDownloadUrl == null || isPreferred)
                    {
                        assetName = name;
                        assetDownloadUrl = dlUrl;
                        assetSize = size;
                        assetSha256 = ParseSha256Digest(digest);
                        if (isPreferred)
                        {
                            // Keep looping only if we haven't seen SHA256SUMS yet and need a hash
                            if (!string.IsNullOrEmpty(assetSha256) || checksumsUrl != null)
                            {
                                break;
                            }
                        }
                    }
                }
            }
        }

        if (assetDownloadUrl != null && string.IsNullOrEmpty(assetSha256) && !string.IsNullOrWhiteSpace(checksumsUrl))
        {
            assetSha256 = await TryFetchAssetSha256FromChecksumsAsync(checksumsUrl, assetName!, checkCts.Token).ConfigureAwait(false);
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
            isUpdateAvailable,
            assetSha256);
    }

    private async Task<string?> TryFetchAssetSha256FromChecksumsAsync(string checksumsUrl, string assetName, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, checksumsUrl);
            req.Headers.UserAgent.ParseAdd($"BaBaBooeyStrap/{_currentVersion}");
            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            if (resp.RequestMessage?.RequestUri is { } finalUri && !IsAllowedGitHubDownloadUrl(finalUri.ToString()))
            {
                return null;
            }
            if (resp.Content.Headers.ContentLength is > MaxChecksumsFileBytes)
            {
                return null;
            }

            string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (text.Length > MaxChecksumsFileBytes) return null;

            foreach (string rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                string line = rawLine.Trim();
                int spaceIdx = line.IndexOfAny([' ', '\t']);
                if (spaceIdx <= 0) continue;

                string hashPart = line[..spaceIdx].Trim();
                string filePart = line[(spaceIdx + 1)..].Trim().TrimStart('*');
                if (IsSha256Hex(hashPart) &&
                    string.Equals(Path.GetFileName(filePart), Path.GetFileName(assetName), StringComparison.OrdinalIgnoreCase))
                {
                    return hashPart.ToLowerInvariant();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Debug("AppUpdateService", $"Failed to fetch SHA256SUMS manifest: {ex.Message}");
        }

        return null;
    }

    internal static string? ParseSha256Digest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest)) return null;
        string trimmed = digest.Trim();
        const string prefix = "sha256:";
        if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[prefix.Length..].Trim();
        }
        return IsSha256Hex(trimmed) ? trimmed.ToLowerInvariant() : null;
    }

    private static bool IsSha256Hex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64) return false;
        foreach (char c in value)
        {
            if (!Uri.IsHexDigit(c)) return false;
        }
        return true;
    }

    public async Task<string?> DownloadAndScheduleInstallAsync(
        AppUpdateInfo info,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(info);

        if (!IsAllowedGitHubDownloadUrl(info.AssetDownloadUrl))
        {
            throw new InvalidOperationException("Update asset URL is missing or not from a trusted GitHub HTTPS host.");
        }

        string? expectedSha256 = ParseSha256Digest(info.AssetSha256);
        if (string.IsNullOrEmpty(expectedSha256))
        {
            _logger.Warn("AppUpdateService", "Release asset does not provide a trusted SHA-256 digest; refusing automatic binary execution.");
            return null;
        }

        if (info.AssetSizeBytes > MaxUpdateFileBytes)
        {
            throw new InvalidOperationException($"Update asset exceeds maximum permitted size ({info.AssetSizeBytes} bytes).");
        }

        string updatesRoot = Path.Combine(_paths.Base, "Updates");
        string stagingDir = Path.Combine(updatesRoot, $"staging_{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDir);

        string downloadedExe = Path.Combine(stagingDir, "BaBaBooeyStrap.new.exe");
        string tempDownloadExe = Path.Combine(stagingDir, $"download_{Guid.NewGuid():N}.part");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, info.AssetDownloadUrl);
            request.Headers.UserAgent.ParseAdd($"BaBaBooeyStrap/{_currentVersion}");

            using var headersCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            headersCts.CancelAfter(MetadataCheckTimeout);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headersCts.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            if (response.RequestMessage?.RequestUri is { } finalUri && !IsAllowedGitHubDownloadUrl(finalUri.ToString()))
            {
                throw new InvalidOperationException($"Update download redirected to untrusted host '{finalUri.Host}'.");
            }

            long totalBytes = response.Content.Headers.ContentLength ?? info.AssetSizeBytes;
            if (totalBytes > MaxUpdateFileBytes)
            {
                throw new InvalidOperationException($"Update response content length ({totalBytes} bytes) exceeds limit.");
            }

            string computedSha256;
            await using (var sourceStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var fileStream = new FileStream(tempDownloadExe, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            using (var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                byte[] buffer = new byte[81920];
                long totalRead = 0;
                bool verifiedPeHeader = false;

                while (true)
                {
                    using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    readCts.CancelAfter(DownloadReadIdleTimeout);

                    int read;
                    try
                    {
                        read = await sourceStream.ReadAsync(buffer, readCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new TimeoutException($"Update download stalled for more than {DownloadReadIdleTimeout.TotalSeconds:F0} seconds.");
                    }

                    if (read <= 0)
                    {
                        break;
                    }

                    if (totalRead + read > MaxUpdateFileBytes)
                    {
                        throw new InvalidOperationException("Downloaded update exceeded maximum permitted size.");
                    }

                    if (!verifiedPeHeader && totalRead == 0)
                    {
                        if (read < 2 || buffer[0] != 0x4D || buffer[1] != 0x5A)
                        {
                            throw new InvalidDataException("Downloaded update payload is not a valid Windows PE executable.");
                        }
                        verifiedPeHeader = true;
                    }

                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    sha256.AppendData(buffer, 0, read);
                    totalRead += read;

                    if (totalBytes > 0)
                    {
                        progress?.Report(Math.Clamp((double)totalRead / totalBytes, 0.0, 1.0));
                    }
                }

                if (totalBytes > 0 && totalRead != totalBytes)
                {
                    throw new IOException($"Incomplete update download ({totalRead} / {totalBytes} bytes).");
                }
                if (totalRead < 1024)
                {
                    throw new IOException("Downloaded update file is unexpectedly small.");
                }

                computedSha256 = Convert.ToHexString(sha256.GetHashAndReset()).ToLowerInvariant();
            }

            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(computedSha256),
                    Encoding.ASCII.GetBytes(expectedSha256)))
            {
                throw new InvalidDataException(
                    $"SHA-256 verification failed for downloaded update. Expected {expectedSha256}, computed {computedSha256}.");
            }

            File.Move(tempDownloadExe, downloadedExe, overwrite: true);
            progress?.Report(1.0);
        }
        catch
        {
            TryDeleteDirectory(stagingDir);
            throw;
        }

        string currentExe = !string.IsNullOrWhiteSpace(_paths.Process)
            ? _paths.Process
            : _paths.Application;

        string targetExe = !string.IsNullOrWhiteSpace(currentExe) ? currentExe : _paths.Application;
        string? installedExe = !string.IsNullOrWhiteSpace(_paths.Application) ? _paths.Application : null;

        if (TryInPlaceAtomicReplaceAndRelaunch(downloadedExe, targetExe, installedExe, out string? oldCleanupPath))
        {
            TryDeleteDirectory(stagingDir);
            _logger.Info("AppUpdateService", $"Installed update {info.LatestVersion} in-place (cleanup='{oldCleanupPath}').");
            return targetExe;
        }

        // Fallback only if in-place move failed (e.g., cross-volume or directory permissions)
        string scriptPath = Path.Combine(stagingDir, $"apply_update_{Guid.NewGuid():N}.cmd");
        string scriptContent = BuildSelfUpdateScript(
            Environment.ProcessId,
            downloadedExe,
            targetExe,
            installedExe);

        await File.WriteAllTextAsync(scriptPath, scriptContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct)
            .ConfigureAwait(false);

        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            CreateNoWindow = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add(scriptPath);
        using var proc = Process.Start(startInfo);

        _logger.Info("AppUpdateService", $"Scheduled fallback self-update script '{scriptPath}' for version {info.LatestVersion}.");
        return scriptPath;
    }

    private bool TryInPlaceAtomicReplaceAndRelaunch(
        string verifiedNewExe,
        string targetExe,
        string? installedExe,
        out string? oldBackupPath)
    {
        oldBackupPath = null;
        try
        {
            string fullTarget = Path.GetFullPath(targetExe);
            string? targetDir = Path.GetDirectoryName(fullTarget);
            if (string.IsNullOrWhiteSpace(targetDir)) return false;
            Directory.CreateDirectory(targetDir);

            oldBackupPath = $"{fullTarget}.old";
            if (File.Exists(oldBackupPath))
            {
                try { File.Delete(oldBackupPath); }
                catch
                {
                    oldBackupPath = $"{fullTarget}.{Guid.NewGuid().ToString("N")[..8]}.old";
                }
            }

            if (File.Exists(fullTarget))
            {
                File.Move(fullTarget, oldBackupPath, overwrite: true);
            }

            try
            {
                File.Move(verifiedNewExe, fullTarget, overwrite: true);
            }
            catch
            {
                // Restore original binary if move failed
                try
                {
                    if (File.Exists(oldBackupPath) && !File.Exists(fullTarget))
                    {
                        File.Move(oldBackupPath, fullTarget, overwrite: true);
                    }
                }
                catch { }
                throw;
            }

            if (!string.IsNullOrWhiteSpace(installedExe))
            {
                string fullInstalled = Path.GetFullPath(installedExe);
                if (!string.Equals(fullTarget, fullInstalled, StringComparison.OrdinalIgnoreCase))
                {
                    string? installedDir = Path.GetDirectoryName(fullInstalled);
                    if (!string.IsNullOrWhiteSpace(installedDir)) Directory.CreateDirectory(installedDir);

                    string installedOld = $"{fullInstalled}.old";
                    try
                    {
                        if (File.Exists(installedOld)) File.Delete(installedOld);
                        if (File.Exists(fullInstalled)) File.Move(fullInstalled, installedOld, overwrite: true);
                        File.Copy(fullTarget, fullInstalled, overwrite: true);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn("AppUpdateService", $"Failed to sync installed copy '{fullInstalled}': {ex.Message}");
                    }
                }
            }

            var psi = new ProcessStartInfo
            {
                FileName = fullTarget,
                WorkingDirectory = targetDir,
                UseShellExecute = false
            };
            psi.ArgumentList.Add("-menu");
            if (!string.IsNullOrWhiteSpace(oldBackupPath) && File.Exists(oldBackupPath))
            {
                psi.ArgumentList.Add("-cleanup-old");
                psi.ArgumentList.Add(oldBackupPath);
            }

            using var launched = Process.Start(psi);
            return launched != null;
        }
        catch (Exception ex)
        {
            _logger.Warn("AppUpdateService", $"In-place update replacement failed, falling back: {ex.Message}");
            return false;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch { }
    }

    internal static string BuildSelfUpdateScript(
        int currentPid,
        string downloadedExe,
        string currentExe,
        string? installedExe)
    {
        string targetExe = !string.IsNullOrWhiteSpace(currentExe) ? currentExe : (installedExe ?? string.Empty);
        ValidateBatchPathSafe(downloadedExe, nameof(downloadedExe));
        ValidateBatchPathSafe(targetExe, nameof(currentExe));

        string escapedDownloaded = EscapeBatchPath(downloadedExe);
        string escapedTarget = EscapeBatchPath(targetExe);
        bool copyInstalled = !string.IsNullOrWhiteSpace(installedExe) &&
            !string.Equals(Path.GetFullPath(targetExe), Path.GetFullPath(installedExe), StringComparison.OrdinalIgnoreCase);
        if (copyInstalled)
        {
            ValidateBatchPathSafe(installedExe!, nameof(installedExe));
        }
        string escapedInstalled = copyInstalled ? EscapeBatchPath(installedExe!) : string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("chcp 65001 >NUL");
        sb.AppendLine("setlocal EnableDelayedExpansion");
        sb.AppendLine("set WAIT_COUNT=0");
        sb.AppendLine(":waitloop");
        sb.AppendLine($"tasklist /FI \"PID eq {currentPid}\" /FO CSV /NH 2>NUL | findstr /C:\"\\\"{currentPid}\\\"\" >NUL");
        sb.AppendLine("if %errorlevel%==0 (");
        sb.AppendLine("  set /a WAIT_COUNT+=1");
        sb.AppendLine("  if !WAIT_COUNT! GEQ 60 goto copyphase");
        sb.AppendLine("  ping -n 2 127.0.0.1 >NUL");
        sb.AppendLine("  goto waitloop");
        sb.AppendLine(")");
        sb.AppendLine(":copyphase");
        sb.AppendLine("set COPY_OK=0");
        sb.AppendLine("for /L %%I in (1,1,10) do (");
        sb.AppendLine($"  copy /Y \"{escapedDownloaded}\" \"{escapedTarget}\" >NUL 2>&1");
        sb.AppendLine("  if not errorlevel 1 (");
        sb.AppendLine("    set COPY_OK=1");
        sb.AppendLine("    goto aftertarget");
        sb.AppendLine("  )");
        sb.AppendLine("  ping -n 2 127.0.0.1 >NUL");
        sb.AppendLine(")");
        sb.AppendLine(":aftertarget");
        if (copyInstalled)
        {
            sb.AppendLine("if !COPY_OK!==1 (");
            sb.AppendLine("  for /L %%I in (1,1,5) do (");
            sb.AppendLine($"    copy /Y \"{escapedDownloaded}\" \"{escapedInstalled}\" >NUL 2>&1");
            sb.AppendLine("    if not errorlevel 1 goto afterinstalled");
            sb.AppendLine("    ping -n 2 127.0.0.1 >NUL");
            sb.AppendLine("  )");
            sb.AppendLine(")");
            sb.AppendLine(":afterinstalled");
        }
        sb.AppendLine($"if !COPY_OK!==1 del /F /Q \"{escapedDownloaded}\" >NUL 2>&1");
        sb.AppendLine($"if !COPY_OK!==1 start \"\" \"{escapedTarget}\" -menu");
        sb.AppendLine("del /F /Q \"%~f0\" >NUL 2>&1");
        return sb.ToString();

        static string EscapeBatchPath(string path) => path.Replace("%", "%%");
    }

    private static void ValidateBatchPathSafe(string path, string paramName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Update path cannot be empty.", paramName);
        }

        foreach (char ch in path)
        {
            if (ch is '"' or '&' or '^' or '|' or '<' or '>' or '!' or '\r' or '\n' or '\0')
            {
                throw new ArgumentException($"Update path contains unsafe character '{ch}'.", paramName);
            }
        }
    }
}
