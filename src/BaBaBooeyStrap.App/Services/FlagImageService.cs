using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net.Http;
using System.Windows.Media.Imaging;
using BaBaBooeyStrap.Core;
using BaBaBooeyStrap.Integrations.Matchmaking;

namespace BaBaBooeyStrap.App.Services;

public static class FlagImageService
{
    private const int MaxFlagPngBytes = 64 * 1024;
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly ConcurrentDictionary<string, byte[]> MemoryCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Task<byte[]?>> InFlightFetches = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HttpClient HttpClient = new(
        new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        },
        disposeHandler: true)
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    private static string CacheDirectory => Path.Combine(
        Paths.Current.Base,
        "Cache",
        "Flags");

    public static string ResolveIsoCode(string? country)
    {
        string iso = RoValraService.NormalizeCountryToIso(country);
        if (iso.Length == 2 && char.IsAsciiLetter(iso[0]) && char.IsAsciiLetter(iso[1]))
        {
            return iso.ToUpperInvariant();
        }
        return string.Empty;
    }

    public static byte[]? TryGetCachedFlagPngBytes(string? country)
    {
        string iso = ResolveIsoCode(country);
        if (string.IsNullOrEmpty(iso)) return null;

        if (MemoryCache.TryGetValue(iso, out byte[]? cached))
            return cached;

        string diskPath = Path.Combine(CacheDirectory, $"{iso.ToLowerInvariant()}.png");
        try
        {
            if (File.Exists(diskPath))
            {
                var info = new FileInfo(diskPath);
                if (info.Length is > 8 and <= MaxFlagPngBytes)
                {
                    byte[] bytes = File.ReadAllBytes(diskPath);
                    if (IsValidPngBytes(bytes))
                    {
                        MemoryCache[iso] = bytes;
                        return bytes;
                    }
                }
            }
        }
        catch { }

        return null;
    }

    public static async Task<byte[]?> GetFlagPngBytesAsync(string? country, CancellationToken ct = default)
    {
        string iso = ResolveIsoCode(country);
        if (string.IsNullOrEmpty(iso))
            return null;

        byte[]? cached = TryGetCachedFlagPngBytes(iso);
        if (cached != null)
            return cached;

        var sharedTask = InFlightFetches.GetOrAdd(iso, static key => FetchAndCacheAsync(key));
        return ct.CanBeCanceled
            ? await sharedTask.WaitAsync(ct).ConfigureAwait(false)
            : await sharedTask.ConfigureAwait(false);
    }

    private static async Task<byte[]?> FetchAndCacheAsync(string iso)
    {
        try
        {
            string url = $"https://flagcdn.com/w40/{iso.ToLowerInvariant()}.png";
            using var resp = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;

            if (resp.Content.Headers.ContentLength > MaxFlagPngBytes)
                return null;

            using var stream = await resp.Content.ReadAsStreamAsync(CancellationToken.None).ConfigureAwait(false);
            using var ms = new MemoryStream();
            byte[] buffer = new byte[4096];
            int totalRead = 0;
            while (true)
            {
                int read = await stream.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false);
                if (read == 0) break;
                totalRead += read;
                if (totalRead > MaxFlagPngBytes)
                    return null;
                ms.Write(buffer, 0, read);
            }

            byte[] bytes = ms.ToArray();
            if (!IsValidPngBytes(bytes))
                return null;

            MemoryCache[iso] = bytes;

            try
            {
                Directory.CreateDirectory(CacheDirectory);
                string diskPath = Path.Combine(CacheDirectory, $"{iso.ToLowerInvariant()}.png");
                await File.WriteAllBytesAsync(diskPath, bytes, CancellationToken.None).ConfigureAwait(false);
            }
            catch { }

            return bytes;
        }
        catch
        {
            return null;
        }
        finally
        {
            InFlightFetches.TryRemove(iso, out _);
        }
    }

    private static bool IsValidPngBytes(byte[]? bytes) =>
        bytes is { Length: > 8 and <= MaxFlagPngBytes } &&
        bytes.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature);

    public static Bitmap CreateMenuFlagBitmap(string? country, byte[]? pngBytes = null)
    {
        const int width = 20;
        const int height = 14;
        var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        using var g = Graphics.FromImage(bitmap);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        if (pngBytes is { Length: > 0 })
        {
            try
            {
                using var ms = new MemoryStream(pngBytes);
                using var src = Image.FromStream(ms);
                g.DrawImage(src, new Rectangle(0, 0, width, height));
                using var borderPen = new Pen(Color.FromArgb(64, 0, 0, 0), 1f);
                g.DrawRectangle(borderPen, 0, 0, width - 1, height - 1);
                return bitmap;
            }
            catch { }
        }

        string iso = ResolveIsoCode(country);
        string badgeText = string.IsNullOrEmpty(iso) ? "??" : iso;

        using var bgBrush = new SolidBrush(Color.FromArgb(45, 74, 124));
        g.FillRectangle(bgBrush, 0, 0, width, height);

        using var border = new Pen(Color.FromArgb(90, 255, 255, 255), 1f);
        g.DrawRectangle(border, 0, 0, width - 1, height - 1);

        using var font = new Font("Segoe UI", 6.5f, FontStyle.Bold, GraphicsUnit.Point);
        using var textBrush = new SolidBrush(Color.White);
        using var sf = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        g.DrawString(badgeText, font, textBrush, new RectangleF(0, 0, width, height), sf);

        return bitmap;
    }

    public static BitmapImage? CreateWpfFlagImage(byte[]? pngBytes)
    {
        if (pngBytes is not { Length: > 0 }) return null;

        try
        {
            using var ms = new MemoryStream(pngBytes);
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = ms;
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch
        {
            return null;
        }
    }
}
