using System.Diagnostics;
using BaBaBooeyStrap.Bootstrapper.Deployment.Abstractions;

namespace BaBaBooeyStrap.Bootstrapper.Deployment;

public class CdnMirrorSelector : ICdnMirrorSelector
{
    private readonly HttpClient? _httpClient;

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

    public async Task<string> ResolveOptimalCdnUrlAsync(CancellationToken ct = default)
    {
        var candidates = CdnMirrorSelectorDefaults.GlobalMirrors;
        var client = _httpClient ?? new HttpClient();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(4));
        var tasks = candidates.Select(async mirror =>
        {
            try
            {
                string endpoint = $"{mirror.TrimEnd('/')}/versionStudio";
                using var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
                var sw = Stopwatch.StartNew();
                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseContentRead, linkedCts.Token);
                sw.Stop();

                if (resp.IsSuccessStatusCode)
                {
                    string content = (await resp.Content.ReadAsStringAsync(linkedCts.Token)).Trim();
                    if (content == CdnMirrorSelectorDefaults.VersionStudioHash ||
                        content.StartsWith("version-", StringComparison.OrdinalIgnoreCase))
                    {
                        return (Mirror: mirror, Latency: sw.ElapsedMilliseconds, Success: true);
                    }
                }
            }
            catch
            {
                // Ignore failure on individual candidate
            }

            return (Mirror: mirror, Latency: long.MaxValue, Success: false);
        }).ToList();

        var results = await Task.WhenAll(tasks);
        var fastest = results.Where(r => r.Success).MinBy(r => r.Latency);

        if (fastest.Success)
        {
            return fastest.Mirror;
        }

        // Fallback to primary setup CDN if all candidates failed or timed out
        return candidates[0];
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
