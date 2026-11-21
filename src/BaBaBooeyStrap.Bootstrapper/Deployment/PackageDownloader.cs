using System.Security.Cryptography;
using BaBaBooeyStrap.Bootstrapper.Deployment.Abstractions;
using BaBaBooeyStrap.Bootstrapper.Deployment.Models;

namespace BaBaBooeyStrap.Bootstrapper.Deployment;

public class PackageDownloader : IPackageDownloader
{
    private readonly IPackageVerifier _verifier;

    public PackageDownloader(IPackageVerifier? verifier = null)
    {
        _verifier = verifier ?? new PackageVerifier();
    }

    public async Task<IReadOnlyList<string>> DownloadPackagesAsync(
        HttpClient client,
        string cdnBaseUrl,
        string versionGuid,
        IEnumerable<PackageRecord> packages,
        string destinationFolder,
        IProgress<PackageDownloadProgress>? progress = null,
        int maxDegreeOfParallelism = 5,
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

        long totalBytes = packageList.Sum(x => x.Package.CompressedSize > 0 ? x.Package.CompressedSize : x.Package.UncompressedSize);
        long totalDownloadedBytes = 0;
        int completedCount = 0;
        int totalCount = packageList.Count;

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

                // 1. Check if valid cached copy already exists
                if (File.Exists(destinationFile) && _verifier.VerifyFileMd5(destinationFile, package.Signature))
                {
                    Interlocked.Add(ref totalDownloadedBytes, package.CompressedSize);
                    Interlocked.Increment(ref completedCount);
                    ReportProgress(package.Name);
                    downloadedFilePaths[originalIndex] = destinationFile;
                    return;
                }

                // 2. Download with retry, HTTPS->HTTP downgrade, and on-the-fly streaming MD5 calculation
                string primaryUrl = $"{cdnBaseUrl.TrimEnd('/')}/{versionGuid}-{package.Name}";
                bool downloaded = false;
                Exception? lastException = null;

                for (int attempt = 1; attempt <= 5; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    string currentUrl = primaryUrl;

                    // On retry after failure, downgrade https to http for flaky connections
                    if (attempt > 1 && currentUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
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
                                Interlocked.Add(ref totalDownloadedBytes, read);
                                ReportProgress(package.Name);
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
                        if (File.Exists(destinationFile))
                        {
                            File.Delete(destinationFile);
                        }
                        File.Move(tempFile, destinationFile);

                        downloadedFilePaths[originalIndex] = destinationFile;
                        downloaded = true;
                        Interlocked.Increment(ref completedCount);
                        ReportProgress(package.Name);
                        break;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        TryDeleteFile(tempFile);
                        throw;
                    }
                    catch (InvalidDataException)
                    {
                        // Checksum failure: rollback bytes and fail immediately
                        Interlocked.Add(ref totalDownloadedBytes, -bytesReadThisAttempt);
                        TryDeleteFile(tempFile);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Add(ref totalDownloadedBytes, -bytesReadThisAttempt);
                        TryDeleteFile(tempFile);
                        lastException = ex;

                        if (attempt < 5)
                        {
                            await Task.Delay(TimeSpan.FromMilliseconds(attempt * 250), ct);
                        }
                    }
                }

                if (!downloaded)
                {
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
        return downloadedFilePaths;

        void ReportProgress(string currentPackageName)
        {
            if (progress == null) return;
            long currentBytes = Interlocked.Read(ref totalDownloadedBytes);
            double percentage = totalBytes > 0 ? Math.Min(100.0, (double)currentBytes / totalBytes * 100.0) : 0.0;
            progress.Report(new PackageDownloadProgress(
                currentPackageName,
                currentBytes,
                totalBytes,
                percentage,
                Volatile.Read(ref completedCount),
                totalCount));
        }

        static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
