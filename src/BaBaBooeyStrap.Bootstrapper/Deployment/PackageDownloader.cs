using System.Diagnostics;
using System.Security.Cryptography;
using BaBaBooeyStrap.Bootstrapper.Deployment.Abstractions;
using BaBaBooeyStrap.Bootstrapper.Deployment.Models;

namespace BaBaBooeyStrap.Bootstrapper.Deployment;

public class PackageDownloader : IPackageDownloader
{
    private const double ProgressThrottleIntervalMs = 50.0;
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
        Directory.CreateDirectory(destinationFolder);

        var packageList = packages
            .Select((pkg, index) => (Package: pkg, OriginalIndex: index))
            .OrderByDescending(x => x.Package.CompressedSize)
            .ToList();
        if (packageList.Count == 0)
        {
            return Array.Empty<string>();
        }

        string trimmedCdn = cdnBaseUrl.TrimEnd('/');
        string effectiveBaseUrl =
            !string.IsNullOrWhiteSpace(channel) &&
            !string.Equals(channel.Trim(), "production", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(channel.Trim(), "live", StringComparison.OrdinalIgnoreCase) &&
            !trimmedCdn.Contains("/channel/", StringComparison.OrdinalIgnoreCase)
                ? $"{trimmedCdn}/channel/{Uri.EscapeDataString(channel.Trim().ToLowerInvariant())}"
                : trimmedCdn;

        long totalBytes = packageList.Sum(x => x.Package.CompressedSize > 0 ? x.Package.CompressedSize : x.Package.UncompressedSize);
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

            await semaphore.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();

                string destinationFile = Path.Combine(destinationFolder, package.Name);
                long expectedPackageBytes = package.CompressedSize > 0 ? package.CompressedSize : package.UncompressedSize;

                // 1. Check if valid cached copy already exists
                if (File.Exists(destinationFile) && _verifier.VerifyFileMd5(destinationFile, package.Signature))
                {
                    Interlocked.Add(ref totalDownloadedBytes, expectedPackageBytes);
                    int done = Interlocked.Increment(ref completedCount);
                    ReportProgress(package.Name, force: done >= totalCount);
                    downloadedFilePaths[originalIndex] = destinationFile;
                    return;
                }

                // 2. Download with retry, HTTPS->HTTP downgrade, and on-the-fly streaming MD5 calculation
                string primaryUrl = $"{effectiveBaseUrl}/{versionGuid}-{package.Name}";
                bool downloaded = false;
                Exception? lastException = null;
                long packageHighWaterBytes = 0;

                for (int attempt = 1; attempt <= 5; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    string currentUrl = primaryUrl;

                    // On retry after transport/network failure (not checksum mismatch), downgrade https to http for flaky connections
                    if (attempt > 1 &&
                        lastException is not null and not InvalidDataException &&
                        currentUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        currentUrl = "http://" + currentUrl.Substring("https://".Length);
                    }

                    long bytesReadThisAttempt = 0;
                    string tempFile = $"{destinationFile}.{Guid.NewGuid():N}.part";

                    try
                    {
                        using var req = new HttpRequestMessage(HttpMethod.Get, currentUrl);
                        using var response = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                        response.EnsureSuccessStatusCode();

                        await using (var networkStream = await response.Content.ReadAsStreamAsync(ct))
                        await using (var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                        using (var incrementalHash = _verifier.CreateStreamingHash())
                        {
                            byte[] buffer = new byte[81920];
                            int read;

                            while ((read = await networkStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                            {
                                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                                incrementalHash.AppendData(buffer, 0, read);

                                bytesReadThisAttempt += read;
                                if (bytesReadThisAttempt > packageHighWaterBytes)
                                {
                                    long delta = bytesReadThisAttempt - packageHighWaterBytes;
                                    packageHighWaterBytes = bytesReadThisAttempt;
                                    Interlocked.Add(ref totalDownloadedBytes, delta);
                                    ReportProgress(package.Name, force: false);
                                }
                            }

                            byte[] hashBytes = incrementalHash.GetHashAndReset();
                            string computedMd5 = Convert.ToHexString(hashBytes).ToLowerInvariant();

                            if (!computedMd5.Equals(package.Signature.Trim(), StringComparison.OrdinalIgnoreCase))
                            {
                                throw new InvalidDataException(
                                    $"MD5 checksum mismatch for package '{package.Name}'. Expected {package.Signature}, computed {computedMd5}.");
                            }
                        }

                        // Atomic move into final destination file
                        File.Move(tempFile, destinationFile, overwrite: true);

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

                        if (attempt < 5)
                        {
                            await Task.Delay(TimeSpan.FromMilliseconds(attempt * 50), ct);
                        }
                    }
                }

                if (!downloaded)
                {
                    if (lastException is InvalidDataException)
                    {
                        throw lastException;
                    }

                    throw new InvalidDataException(
                        $"Failed to download package '{package.Name}' after 5 attempts.", lastException);
                }
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(downloadTasks);
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
}
