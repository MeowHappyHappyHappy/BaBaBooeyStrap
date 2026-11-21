namespace BaBaBooeyStrap.Bootstrapper.Mods.Presets;

public static class DeathSoundPresetHandler
{
    private static readonly byte[] OggHeader = { 0x4F, 0x67, 0x67, 0x53 }; // 'OggS'
    public const string RelativeOuchPath = @"content\sounds\ouch.ogg";

    public static bool IsValidOgg(byte[] data)
    {
        if (data == null || data.Length < 4) return false;
        return data.AsSpan(0, 4).SequenceEqual(OggHeader);
    }

    public static void ApplyDeathSound(byte[] oggBytes, string destinationBaseDir)
    {
        if (!IsValidOgg(oggBytes))
        {
            throw new InvalidDataException("Invalid OggS audio container format.");
        }

        string destPath = Path.Combine(destinationBaseDir, RelativeOuchPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        File.WriteAllBytes(destPath, oggBytes);
    }

    public static void ApplyDeathSoundFile(string sourceFilePath, string destinationBaseDir)
    {
        if (!File.Exists(sourceFilePath))
        {
            throw new FileNotFoundException("Death sound audio file not found.", sourceFilePath);
        }

        byte[] bytes = File.ReadAllBytes(sourceFilePath);
        ApplyDeathSound(bytes, destinationBaseDir);
    }

    public static void ResetDeathSound(string destinationBaseDir)
    {
        string destPath = Path.Combine(destinationBaseDir, RelativeOuchPath);
        if (File.Exists(destPath))
        {
            File.Delete(destPath);
        }
    }
}
