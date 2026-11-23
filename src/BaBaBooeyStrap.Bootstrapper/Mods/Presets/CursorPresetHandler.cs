using System.Reflection;

namespace BaBaBooeyStrap.Bootstrapper.Mods.Presets;

public enum CursorPreset
{
    Default,
    Classic2006,
    Classic2013,
    From2006 = Classic2006,
    From2013 = Classic2013
}

public static class CursorPresetHandler
{
    private static readonly byte[] PngHeader = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private static readonly byte[] CurHeader = { 0x00, 0x00, 0x02, 0x00 };

    public const string RelativeArrowCursor = @"content\textures\Cursors\KeyboardMouse\ArrowCursor.png";
    public const string RelativeArrowFarCursor = @"content\textures\Cursors\KeyboardMouse\ArrowFarCursor.png";

    public static bool IsValidCursor(byte[] data)
    {
        if (data == null || data.Length < 4 || data.Length > 10 * 1024 * 1024) return false;
        if (data.Length >= 8 && data.AsSpan(0, 8).SequenceEqual(PngHeader)) return true;
        return data.AsSpan(0, 4).SequenceEqual(CurHeader);
    }

    public static void ApplyCursorPreset(string destinationBaseDir, CursorPreset preset)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationBaseDir);

        string arrowPath = Path.Combine(destinationBaseDir, RelativeArrowCursor);
        string arrowFarPath = Path.Combine(destinationBaseDir, RelativeArrowFarCursor);

        if (preset == CursorPreset.Default)
        {
            DeleteIfExists(arrowPath);
            DeleteIfExists(arrowFarPath);
            return;
        }

        string folder = preset switch
        {
            CursorPreset.Classic2006 => "From2006",
            CursorPreset.Classic2013 => "From2013",
            _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Unsupported cursor preset.")
        };

        Directory.CreateDirectory(Path.GetDirectoryName(arrowPath)!);

        var assembly = Assembly.GetExecutingAssembly();
        string resArrow = $"BaBaBooeyStrap.Bootstrapper.Resources.Mods.Cursor.{folder}.ArrowCursor.png";
        string resArrowFar = $"BaBaBooeyStrap.Bootstrapper.Resources.Mods.Cursor.{folder}.ArrowFarCursor.png";

        ExtractResourceToFile(assembly, resArrow, arrowPath);
        ExtractResourceToFile(assembly, resArrowFar, arrowFarPath);
    }

    public static CursorPreset DetectCursorPreset(string destinationBaseDir)
    {
        if (string.IsNullOrWhiteSpace(destinationBaseDir))
        {
            return CursorPreset.Default;
        }

        string arrowPath = Path.Combine(destinationBaseDir, RelativeArrowCursor);
        if (!File.Exists(arrowPath))
        {
            return CursorPreset.Default;
        }

        try
        {
            byte[] actualBytes = File.ReadAllBytes(arrowPath);
            var assembly = Assembly.GetExecutingAssembly();

            if (MatchesEmbeddedResource(assembly, "BaBaBooeyStrap.Bootstrapper.Resources.Mods.Cursor.From2006.ArrowCursor.png", actualBytes))
            {
                return CursorPreset.Classic2006;
            }

            if (MatchesEmbeddedResource(assembly, "BaBaBooeyStrap.Bootstrapper.Resources.Mods.Cursor.From2013.ArrowCursor.png", actualBytes))
            {
                return CursorPreset.Classic2013;
            }
        }
        catch
        {
        }

        return CursorPreset.Default;
    }

    private static bool MatchesEmbeddedResource(Assembly assembly, string resourceName, byte[] actualBytes)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null || stream.Length != actualBytes.Length)
        {
            return false;
        }

        byte[] expectedBytes = new byte[actualBytes.Length];
        stream.ReadExactly(expectedBytes);
        return actualBytes.AsSpan().SequenceEqual(expectedBytes);
    }

    private static void ExtractResourceToFile(Assembly assembly, string resourceName, string destinationFilePath)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing embedded cursor resource '{resourceName}'.");

        if (File.Exists(destinationFilePath))
        {
            var attrs = File.GetAttributes(destinationFilePath);
            if (attrs.HasFlag(FileAttributes.ReadOnly))
            {
                File.SetAttributes(destinationFilePath, attrs & ~FileAttributes.ReadOnly);
            }
        }

        string tempPath = $"{destinationFilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var dest = File.Create(tempPath))
            {
                stream.CopyTo(dest);
            }
            File.Move(tempPath, destinationFilePath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (!File.Exists(path)) return;
        var attrs = File.GetAttributes(path);
        if (attrs.HasFlag(FileAttributes.ReadOnly))
        {
            File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
        }
        File.Delete(path);
    }
}
