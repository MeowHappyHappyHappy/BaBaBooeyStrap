using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BaBaBooeyStrap.Core.Interop;
using BaBaBooeyStrap.Core.Models.Enums;
using Drawing = System.Drawing;

namespace BaBaBooeyStrap.App.Services;

public static class IconResolver
{
    public const string EmbeddedIconPackUri = "pack://application:,,,/BaBaBooeyStrap;component/Resources/Icons/BaBaBooeyStrap.ico";

    public static ImageSource? ResolveImageSource(BootstrapperIcon iconKind, string? customIconPath)
    {
        if (iconKind == BootstrapperIcon.IconCustom && IsSafeLocalFilePath(customIconPath))
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(Path.GetFullPath(customIconPath!), UriKind.Absolute);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                // Fall back to embedded icon
            }
        }

        try
        {
            var streamInfo = Application.GetResourceStream(new Uri(EmbeddedIconPackUri, UriKind.Absolute));
            if (streamInfo != null)
            {
                using var stream = streamInfo.Stream;
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames.FirstOrDefault();
                frame?.Freeze();
                return frame;
            }
        }
        catch { }

        return null;
    }

    public static Drawing.Icon ResolveDrawingIcon(BootstrapperIcon iconKind, string? customIconPath)
    {
        if (iconKind == BootstrapperIcon.IconCustom && IsSafeLocalFilePath(customIconPath))
        {
            try
            {
                string fullPath = Path.GetFullPath(customIconPath!);
                if (string.Equals(Path.GetExtension(fullPath), ".ico", StringComparison.OrdinalIgnoreCase))
                {
                    return new Drawing.Icon(fullPath);
                }

                using var srcBitmap = new Drawing.Bitmap(fullPath);
                using var resizedBitmap = new Drawing.Bitmap(srcBitmap, new Drawing.Size(32, 32));
                nint hIcon = resizedBitmap.GetHicon();
                try
                {
                    using var tempIcon = Drawing.Icon.FromHandle(hIcon);
                    return (Drawing.Icon)tempIcon.Clone();
                }
                finally
                {
                    User32.DestroyIcon(hIcon);
                }
            }
            catch
            {
                // Fall back to embedded/system icon
            }
        }

        try
        {
            var streamInfo = Application.GetResourceStream(new Uri(EmbeddedIconPackUri, UriKind.Absolute));
            if (streamInfo != null)
            {
                using var stream = streamInfo.Stream;
                return new Drawing.Icon(stream);
            }
        }
        catch { }

        try
        {
            string? exePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
            {
                var extracted = Drawing.Icon.ExtractAssociatedIcon(exePath);
                if (extracted != null) return extracted;
            }
        }
        catch { }

        return (Drawing.Icon)Drawing.SystemIcons.Application.Clone();
    }

    private static bool IsSafeLocalFilePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        string trimmed = path.Trim();
        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal) ||
            trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        if (!Path.IsPathRooted(trimmed)) return false;

        try
        {
            string full = Path.GetFullPath(trimmed);
            return !full.StartsWith(@"\\", StringComparison.Ordinal) && File.Exists(full);
        }
        catch
        {
            return false;
        }
    }
}
