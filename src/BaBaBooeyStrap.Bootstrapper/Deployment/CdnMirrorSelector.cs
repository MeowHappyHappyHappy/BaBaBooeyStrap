using System.Diagnostics;
using BaBaBooeyStrap.Bootstrapper.Deployment.Abstractions;

namespace BaBaBooeyStrap.Bootstrapper.Deployment;

public class CdnMirrorSelector : ICdnMirrorSelector
{
    private static readonly HttpClient FallbackHttpClient = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly TimeSpan MirrorCacheTtl = TimeSpan.FromMinutes(10);

    private readonly HttpClient? _httpClient;
    private readonly object _cacheLock = new();
    private string? _cachedMirror;
    private DateTimeOffset _cacheExpiresUtc;

    public CdnMirrorSelector(HttpClient? httpClient = null)
    {
        _httpClient = httpClient;
    }

    public async Task<string> SelectFastestMirrorAsync(
        IEnumerable<string> mirrorUrls,
        HttpClient? client = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        return await SelectFastestMirrorInternalAsync(mirrorUrls, client ?? _httpClient, timeout, ct);
    }

    public Task<string> ResolveOptimalCdnUrlAsync(CancellationToken ct = default)
    {
        return ResolveOptimalCdnUrlAsync(forceRefresh: false, ct);
    }

    public async Task<string> ResolveOptimalCdnUrlAsync(bool forceRefresh, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!forceRefresh)
        {
            lock (_cacheLock)
            {
                if (!string.IsNullOrWhiteSpace(_cachedMirror) && DateTimeOffset.UtcNow < _cacheExpiresUtc)
                {
                    return _cachedMirror;
                }
            }
        }

        var candidates = CdnMirrorSelectorDefaults.GlobalMirrors;
        var client = _httpClient ?? FallbackHttpClient;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(4));

        var pending = candidates.Select(mirror => ProbeMirrorAsync(client, mirror, linkedCts.Token)).ToList();
        while (pending.Count > 0)
        {
            var completed = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(completed);

            string? winner = await completed.ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(winner))
            {
                linkedCts.Cancel();
                lock (_cacheLock)
                {
                    _cachedMirror = winner;
                    _cacheExpiresUtc = DateTimeOffset.UtcNow.Add(MirrorCacheTtl);
                }
                return winner;
            }
        }

        ct.ThrowIfCancellationRequested();

        // Fallback to primary setup CDN if all candidates failed or timed out
        return candidates[0];

        static async Task<string?> ProbeMirrorAsync(HttpClient httpClient, string mirror, CancellationToken token)
        {
            try
            {
                string endpoint = $"{mirror.TrimEnd('/')}/versionStudio";
                using var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
                using var resp = await httpClient.SendAsync(req, HttpCompletionOption.ResponseContentRead, token).ConfigureAwait(false);

                if (resp.IsSuccessStatusCode)
                {
                    string content = (await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false)).Trim();
                    if (content == CdnMirrorSelectorDefaults.VersionStudioHash ||
                        content.StartsWith("version-", StringComparison.OrdinalIgnoreCase))
                    {
                        return mirror;
                    }
                }
            }
            catch
            {
                // Ignore individual candidate failures/cancellation
            }

            return null;
        }
    }

    public static async Task<string> SelectFastestMirrorInternalAsync(
        IEnumerable<string> mirrorUrls,
        HttpClient? client,
        TimeSpan? timeout,
        CancellationToken ct = default)
    {
        var mirrors = mirrorUrls?.ToList() ?? throw new ArgumentNullException(nameof(mirrorUrls));
        if (mirrors.Count == 0)
        {
            throw new ArgumentException("No mirrors provided.", nameof(mirrorUrls));
        }

        HttpClient httpClient = client ?? new HttpClient();
        TimeSpan pingTimeout = timeout ?? TimeSpan.FromSeconds(3);

        string? fastestUrl = null;
        long lowestElapsedMs = long.MaxValue;
        Exception? lastException = null;

        foreach (string mirror in mirrors)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(pingTimeout);

                var sw = Stopwatch.StartNew();
                using var resp = await httpClient.GetAsync(mirror, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                sw.Stop();

                if (resp.IsSuccessStatusCode)
                {
                    if (sw.ElapsedMilliseconds < lowestElapsedMs)
                    {
                        lowestElapsedMs = sw.ElapsedMilliseconds;
                        fastestUrl = mirror;
                    }
                }
                else
                {
                    lastException = new HttpRequestException(
                        $"Mirror '{mirror}' returned HTTP {(int)resp.StatusCode} ({resp.StatusCode}).");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;
            }
        }

        if (fastestUrl == null)
        {
            throw new HttpRequestException("All CDN mirrors failed or timed out.", lastException);
        }

        return fastestUrl;
    }
}
