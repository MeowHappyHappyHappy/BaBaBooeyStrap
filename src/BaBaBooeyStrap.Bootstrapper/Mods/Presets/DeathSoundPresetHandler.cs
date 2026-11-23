namespace BaBaBooeyStrap.Bootstrapper.Mods.Presets;

public static class DeathSoundPresetHandler
{
    public const long MaxDeathSoundBytes = 25 * 1024 * 1024;
    private static readonly byte[] OggHeader = { 0x4F, 0x67, 0x67, 0x53 }; // 'OggS'
    public const string RelativeOuchPath = @"content\sounds\ouch.ogg";

    public static bool IsValidOgg(byte[] data)
    {
        if (data == null || data.Length < 4 || data.Length > MaxDeathSoundBytes) return false;
        return data.AsSpan(0, 4).SequenceEqual(OggHeader);
    }

    public static void ApplyDeathSound(byte[] oggBytes, string destinationBaseDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationBaseDir);
        if (!IsValidOgg(oggBytes))
        {
            throw new InvalidDataException("Invalid OggS audio container format.");
        }

        string destPath = Path.Combine(destinationBaseDir, RelativeOuchPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        ClearReadOnlyIfPresent(destPath);
        string tempPath = $"{destPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(tempPath, oggBytes);
            File.Move(tempPath, destPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }
    }

    public static void ApplyDeathSoundFile(string sourceFilePath, string destinationBaseDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationBaseDir);

        if (!File.Exists(sourceFilePath))
        {
            throw new FileNotFoundException("Death sound audio file not found.", sourceFilePath);
        }

        var info = new FileInfo(sourceFilePath);
        if (info.Length > MaxDeathSoundBytes)
        {
            throw new InvalidDataException($"Death sound file exceeds maximum allowed size ({MaxDeathSoundBytes / (1024 * 1024)} MB).");
        }

        using var sourceStream = File.OpenRead(sourceFilePath);
        byte[] header = new byte[4];
        int read = sourceStream.Read(header, 0, 4);
        if (read < 4 || !header.AsSpan().SequenceEqual(OggHeader))
        {
            throw new InvalidDataException("Invalid OggS audio container format.");
        }

        sourceStream.Position = 0;
        string destPath = Path.Combine(destinationBaseDir, RelativeOuchPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        ClearReadOnlyIfPresent(destPath);
        string tempPath = $"{destPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var destStream = File.Create(tempPath))
            {
                sourceStream.CopyTo(destStream);
            }
            File.Move(tempPath, destPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }
    }

    public static void ResetDeathSound(string destinationBaseDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationBaseDir);
        string destPath = Path.Combine(destinationBaseDir, RelativeOuchPath);
        if (File.Exists(destPath))
        {
            ClearReadOnlyIfPresent(destPath);
            File.Delete(destPath);
        }
    }

    private static void ClearReadOnlyIfPresent(string path)
    {
        if (!File.Exists(path)) return;
        var attrs = File.GetAttributes(path);
        if (attrs.HasFlag(FileAttributes.ReadOnly))
        {
            File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
        }
    }
}
