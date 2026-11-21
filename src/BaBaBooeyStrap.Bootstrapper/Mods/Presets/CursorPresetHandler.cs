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
    private static readonly byte[] PngHeader = { 0x89, 0x50, 0x4E, 0x47 };
    private static readonly byte[] CurHeader = { 0x00, 0x00, 0x02, 0x00 };

    public const string RelativeArrowCursor = @"content\textures\Cursors\KeyboardMouse\ArrowCursor.png";
    public const string RelativeArrowFarCursor = @"content\textures\Cursors\KeyboardMouse\ArrowFarCursor.png";

    public static bool IsValidCursor(byte[] data)
    {
        if (data == null || data.Length < 4) return false;
        ReadOnlySpan<byte> header = data.AsSpan(0, 4);
        return header.SequenceEqual(PngHeader) || header.SequenceEqual(CurHeader);
    }

    public static void ApplyCursorPreset(string destinationBaseDir, CursorPreset preset)
    {
        string arrowPath = Path.Combine(destinationBaseDir, RelativeArrowCursor);
        string arrowFarPath = Path.Combine(destinationBaseDir, RelativeArrowFarCursor);

        if (preset == CursorPreset.Default)
        {
            if (File.Exists(arrowPath)) File.Delete(arrowPath);
            if (File.Exists(arrowFarPath)) File.Delete(arrowFarPath);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(arrowPath)!);

        var assembly = Assembly.GetExecutingAssembly();
        string folder = preset == CursorPreset.Classic2006 ? "From2006" : "From2013";

        string resArrow = $"BaBaBooeyStrap.Bootstrapper.Resources.Mods.Cursor.{folder}.ArrowCursor.png";
        string resArrowFar = $"BaBaBooeyStrap.Bootstrapper.Resources.Mods.Cursor.{folder}.ArrowFarCursor.png";

        ExtractResourceToFile(assembly, resArrow, arrowPath);
        ExtractResourceToFile(assembly, resArrowFar, arrowFarPath);
    }

    private static void ExtractResourceToFile(Assembly assembly, string resourceName, string destinationFilePath)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream != null)
        {
            using var dest = File.Create(destinationFilePath);
            stream.CopyTo(dest);
        }
        else
        {
            // Fallback dummy PNG header if resource not found
            byte[] dummy = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00 };
            File.WriteAllBytes(destinationFilePath, dummy);
        }
    }
}
