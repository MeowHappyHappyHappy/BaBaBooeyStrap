using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Security;
using System.Security.Cryptography;
using BaBaBooeyStrap.Bootstrapper.Deployment.Abstractions;
using BaBaBooeyStrap.Bootstrapper.Deployment.Models;

namespace BaBaBooeyStrap.Bootstrapper.Deployment;

public class PackageDownloader : IPackageDownloader
{
    private const double ProgressThrottleIntervalMs = 50.0;
    private const long MaxPackageDownloadBytes = 2L * 1024 * 1024 * 1024;
    private static readonly TimeSpan ReadStallTimeout = TimeSpan.FromSeconds(30);
    private readonly IPackageVerifier _verifier;

    public PackageDownloader(IPackageVerifier? verifier = null)
    {
        _verifier = verifier ?? new PackageVerifier();
    }

    public Task<IReadOnlyList<string>> DownloadPackagesAsync(
        HttpClient client,
        string cdnBaseUrl,
        string versionGuid,
        IEnumerable<PackageRecord> packages,
        string destinationFolder,
        IProgress<PackageDownloadProgress>? progress = null,
        int maxDegreeOfParallelism = 5,
        CancellationToken ct = default)
    {
        return DownloadPackagesAsync(
            client,
            cdnBaseUrl,
            versionGuid,
            packages,
            destinationFolder,
            progress,
            maxDegreeOfParallelism,
            channel: "production",
            ct);
    }

    public async Task<IReadOnlyList<string>> DownloadPackagesAsync(
        HttpClient client,
        string cdnBaseUrl,
        string versionGuid,
        IEnumerable<PackageRecord> packages,
        string destinationFolder,
        IProgress<PackageDownloadProgress>? progress,
        int maxDegreeOfParallelism,
        string channel,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(cdnBaseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(versionGuid);
        ArgumentNullException.ThrowIfNull(packages);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFolder);

        if (maxDegreeOfParallelism < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDegreeOfParallelism), "Concurrency must be at least 1.");
        }

        if (!Uri.TryCreate(cdnBaseUrl.Trim(), UriKind.Absolute, out var cdnUri) ||
            !string.Equals(cdnUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"CDN base URL must use HTTPS: '{cdnBaseUrl}'.");
        }

        string cleanGuid = versionGuid.Trim();
        if (!DeploymentManifestParser.IsSafePackageFileName(cleanGuid))
        {
            throw new ArgumentException($"Invalid version GUID '{versionGuid}'.", nameof(versionGuid));
        }

        var packageList = packages
            .Select((pkg, index) => (Package: pkg, OriginalIndex: index))
            .OrderByDescending(x => x.Package.CompressedSize)
            .ToList();
        if (packageList.Count == 0)
        {
            return Array.Empty<string>();
        }

        string fullDestFolder = Path.GetFullPath(destinationFolder);
        string destFolderPrefix = fullDestFolder.EndsWith(Path.DirectorySeparatorChar)
            ? fullDestFolder
            : fullDestFolder + Path.DirectorySeparatorChar;

        foreach (var item in packageList)
        {
            if (!DeploymentManifestParser.IsSafePackageFileName(item.Package.Name))
            {
                throw new SecurityException($"Unsafe package filename '{item.Package.Name}'.");
            }
        }

        Directory.CreateDirectory(fullDestFolder);
        CleanupStalePartFiles(fullDestFolder);

        long totalBytes = 0;
        foreach (var item in packageList)
        {
            long size = item.Package.CompressedSize > 0
                ? item.Package.CompressedSize
                : Math.Max(0, item.Package.UncompressedSize);
            totalBytes = checked(totalBytes + size);
        }

        if (totalBytes > 0)
        {
            CheckAvailableDiskSpace(fullDestFolder, totalBytes);
        }

        string trimmedCdn = cdnBaseUrl.Trim().TrimEnd('/');
        string effectiveBaseUrl =
            !string.IsNullOrWhiteSpace(channel) &&
            !string.Equals(channel.Trim(), "production", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(channel.Trim(), "live", StringComparison.OrdinalIgnoreCase) &&
            !trimmedCdn.Contains("/channel/", StringComparison.OrdinalIgnoreCase)
                ? $"{trimmedCdn}/channel/{Uri.EscapeDataString(channel.Trim().ToLowerInvariant())}"
                : trimmedCdn;

        long totalDownloadedBytes = 0;
        long maxReportedBytes = 0;
        long lastReportTicks = 0;
        int completedCount = 0;
        int totalCount = packageList.Count;
        object progressLock = new();

        var downloadedFilePaths = new string[totalCount];
        using var semaphore = new SemaphoreSlim(maxDegreeOfParallelism, maxDegreeOfParallelism);

        var downloadTasks = packageList.Select(async item =>
        {
            var package = item.Package;
            int originalIndex = item.OriginalIndex;

            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();

                string destinationFile = Path.GetFullPath(Path.Combine(fullDestFolder, package.Name));
                if (!destinationFile.StartsWith(destFolderPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    throw new SecurityException($"Package path escapes destination folder: '{package.Name}'.");
                }

                string expectedSignature = package.Signature?.Trim() ?? string.Empty;
                long expectedPackageBytes = package.CompressedSize > 0
                    ? package.CompressedSize
                    : Math.Max(0, package.UncompressedSize);

                // 1. Check if valid cached copy already exists
                bool cacheHit = false;
                try
                {
                    if (File.Exists(destinationFile))
                    {
                        cacheHit = await _verifier.VerifyFileMd5Async(destinationFile, expectedSignature, ct).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    cacheHit = false;
                }

                if (cacheHit)
                {
                    Interlocked.Add(ref totalDownloadedBytes, expectedPackageBytes);
                    int done = Interlocked.Increment(ref completedCount);
                    ReportProgress(package.Name, force: done >= totalCount);
                    downloadedFilePaths[originalIndex] = destinationFile;
                    return;
                }

                // 2. Download strictly over HTTPS with retry, stall timeout, and on-the-fly streaming MD5 verification
                string primaryUrl = $"{effectiveBaseUrl}/{Uri.EscapeDataString(cleanGuid)}-{Uri.EscapeDataString(package.Name)}";
                bool downloaded = false;
                Exception? lastException = null;
                long packageHighWaterBytes = 0;

                for (int attempt = 1; attempt <= 5; attempt++)
                {
                    ct.ThrowIfCancellationRequested();

                    long bytesReadThisAttempt = 0;
                    string tempFile = $"{destinationFile}.{Guid.NewGuid():N}.part";

                    try
                    {
                        using var req = new HttpRequestMessage(HttpMethod.Get, primaryUrl);
                        using var response = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                        response.EnsureSuccessStatusCode();

                        if (response.RequestMessage?.RequestUri is { } finalUri &&
                            !string.Equals(finalUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException($"Refused insecure HTTP redirect to '{finalUri}'.");
                        }

                        long maxAllowedBytes = expectedPackageBytes > 0
                            ? Math.Min(MaxPackageDownloadBytes, Math.Max(expectedPackageBytes * 2, expectedPackageBytes + 1024 * 1024))
                            : MaxPackageDownloadBytes;

                        long? contentLength = response.Content.Headers.ContentLength;
                        if (contentLength.HasValue && contentLength.Value > maxAllowedBytes)
                        {
                            throw new InvalidDataException($"Package '{package.Name}' Content-Length ({contentLength.Value}) exceeds permitted limit.");
                        }

                        byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
                        try
                        {
                            await using (var networkStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                            await using (var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                            using (var incrementalHash = _verifier.CreateStreamingHash())
                            {
                                while (true)
                                {
                                    using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                                    stallCts.CancelAfter(ReadStallTimeout);

                                    int read;
                                    try
                                    {
                                        read = await networkStream.ReadAsync(buffer, 0, buffer.Length, stallCts.Token).ConfigureAwait(false);
                                    }
                                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                                    {
                                        throw new TimeoutException($"Download stalled for package '{package.Name}'.");
                                    }

                                    if (read <= 0)
                                    {
                                        break;
                                    }

                                    bytesReadThisAttempt += read;
                                    if (bytesReadThisAttempt > maxAllowedBytes)
                                    {
                                        throw new InvalidDataException($"Package '{package.Name}' exceeded maximum allowed download size.");
                                    }

                                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                                    incrementalHash.AppendData(buffer, 0, read);

                                    if (bytesReadThisAttempt > packageHighWaterBytes)
                                    {
                                        long delta = bytesReadThisAttempt - packageHighWaterBytes;
                                        packageHighWaterBytes = bytesReadThisAttempt;
                                        Interlocked.Add(ref totalDownloadedBytes, delta);
                                        ReportProgress(package.Name, force: false);
                                    }
                                }

                                if (contentLength.HasValue && bytesReadThisAttempt != contentLength.Value)
                                {
                                    throw new IOException($"Incomplete response for '{package.Name}' ({bytesReadThisAttempt} / {contentLength.Value} bytes).");
                                }

                                byte[] hashBytes = incrementalHash.GetHashAndReset();
                                string computedMd5 = Convert.ToHexString(hashBytes).ToLowerInvariant();

                                if (!computedMd5.Equals(expectedSignature, StringComparison.OrdinalIgnoreCase))
                                {
                                    throw new InvalidDataException(
                                        $"MD5 checksum mismatch for package '{package.Name}'. Expected {expectedSignature}, computed {computedMd5}.");
                                }
                            }
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(buffer);
                        }

                        // Atomic move into final destination file (clear ReadOnly & retry transient AV locks)
                        await MoveWithRetryAsync(tempFile, destinationFile, ct).ConfigureAwait(false);

                        downloadedFilePaths[originalIndex] = destinationFile;
                        downloaded = true;
                        int done = Interlocked.Increment(ref completedCount);
                        ReportProgress(package.Name, force: done >= totalCount);
                        break;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        TryDeleteFile(tempFile);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        TryDeleteFile(tempFile);
                        lastException = ex;

                        if (!IsTransientDownloadError(ex) || attempt >= 5)
                        {
                            break;
                        }

                        int delayMs = Math.Min(2000, 100 * (1 << (attempt - 1))) + Random.Shared.Next(0, 75);
                        await Task.Delay(TimeSpan.FromMilliseconds(delayMs), ct).ConfigureAwait(false);
                    }
                }

                if (!downloaded)
                {
                    if (lastException is InvalidDataException)
                    {
                        ExceptionDispatchInfo.Capture(lastException).Throw();
                    }

                    throw new InvalidDataException(
                        $"Failed to download package '{package.Name}' after retries.", lastException);
                }
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(downloadTasks).ConfigureAwait(false);
        if (totalCount > 0)
        {
            ReportProgress(packageList[^1].Package.Name, force: true);
        }
        return downloadedFilePaths;

        void ReportProgress(string currentPackageName, bool force)
        {
            if (progress == null) return;

            long reportedBytes;
            int done;
            lock (progressLock)
            {
                long nowTicks = Stopwatch.GetTimestamp();
                double elapsedMs = lastReportTicks == 0
                    ? ProgressThrottleIntervalMs
                    : (nowTicks - lastReportTicks) * 1000.0 / Stopwatch.Frequency;

                if (!force && elapsedMs < ProgressThrottleIntervalMs)
                {
                    return;
                }

                long currentBytes = Interlocked.Read(ref totalDownloadedBytes);
                reportedBytes = Math.Max(maxReportedBytes, currentBytes);
                if (totalBytes > 0)
                {
                    reportedBytes = Math.Min(reportedBytes, totalBytes);
                }
                maxReportedBytes = reportedBytes;
                lastReportTicks = nowTicks;
                done = Volatile.Read(ref completedCount);
            }

            double percentage = totalBytes > 0 ? Math.Min(100.0, (double)reportedBytes / totalBytes * 100.0) : 0.0;
            if (done >= totalCount)
            {
                percentage = 100.0;
            }

            progress.Report(new PackageDownloadProgress(
                currentPackageName,
                reportedBytes,
                totalBytes,
                percentage,
                done,
                totalCount));
        }

        static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    private static bool IsTransientDownloadError(Exception ex)
    {
        if (ex is UnauthorizedAccessException or SecurityException)
        {
            return false;
        }

        if (ex is HttpRequestException hre &&
            hre.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
        {
            return false;
        }

        return true;
    }

    private static async Task MoveWithRetryAsync(string tempFile, string destinationFile, CancellationToken ct)
    {
        for (int i = 0; i < 4; i++)
        {
            try
            {
                if (File.Exists(destinationFile))
                {
                    var attrs = File.GetAttributes(destinationFile);
                    if (attrs.HasFlag(FileAttributes.ReadOnly))
                    {
                        File.SetAttributes(destinationFile, attrs & ~FileAttributes.ReadOnly);
                    }
                }

                File.Move(tempFile, destinationFile, overwrite: true);
                return;
            }
            catch (IOException) when (i < 3)
            {
                await Task.Delay(50 * (i + 1), ct).ConfigureAwait(false);
            }
        }
    }

    private static void CheckAvailableDiskSpace(string destinationFolder, long totalPackageBytes)
    {
        try
        {
            string? root = Path.GetPathRoot(destinationFolder);
            if (string.IsNullOrWhiteSpace(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return;
            }

            var drive = new DriveInfo(root);
            if (!drive.IsReady) return;

            long requiredBytes = (long)Math.Ceiling(totalPackageBytes * 1.15);
            if (drive.AvailableFreeSpace < requiredBytes)
            {
                throw new IOException(
                    $"Insufficient disk space on '{root}'. Required ~{requiredBytes / (1024 * 1024)} MB, available {drive.AvailableFreeSpace / (1024 * 1024)} MB.");
            }
        }
        catch (IOException)
        {
            throw;
        }
        catch
        {
            // Ignore DriveInfo failures on synthetic/virtual mounts
        }
    }

    private static void CleanupStalePartFiles(string destinationFolder)
    {
        try
        {
            foreach (string partFile in Directory.GetFiles(destinationFolder, "*.part", SearchOption.TopDirectoryOnly))
            {
                try { File.Delete(partFile); } catch { }
            }
        }
        catch { }
    }
}
