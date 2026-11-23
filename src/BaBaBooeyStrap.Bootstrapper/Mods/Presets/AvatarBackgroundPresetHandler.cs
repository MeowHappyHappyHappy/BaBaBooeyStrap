using System.Reflection;

namespace BaBaBooeyStrap.Bootstrapper.Mods.Presets;

public static class AvatarBackgroundPresetHandler
{
    public const string RelativeMobileRbxlPath = @"ExtraContent\places\Mobile.rbxl";
    public const string ResourceName = "BaBaBooeyStrap.Bootstrapper.Resources.Mods.OldAvatarBackground.rbxl";

    public static void SetOldAvatarBackground(bool enabled, string destinationBaseDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationBaseDir);

        string dest = Path.Combine(destinationBaseDir, RelativeMobileRbxlPath);
        if (enabled)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (File.Exists(dest))
            {
                var attrs = File.GetAttributes(dest);
                if (attrs.HasFlag(FileAttributes.ReadOnly))
                {
                    File.SetAttributes(dest, attrs & ~FileAttributes.ReadOnly);
                }
            }

            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream(ResourceName)
                ?? throw new InvalidOperationException($"Missing embedded mod resource '{ResourceName}'.");

            string tempPath = $"{dest}.{Guid.NewGuid():N}.tmp";
            try
            {
                using (var destStream = File.Create(tempPath))
                {
                    stream.CopyTo(destStream);
                }
                File.Move(tempPath, dest, overwrite: true);
            }
            catch
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                throw;
            }
        }
        else
        {
            if (File.Exists(dest))
            {
                var attrs = File.GetAttributes(dest);
                if (attrs.HasFlag(FileAttributes.ReadOnly))
                {
                    File.SetAttributes(dest, attrs & ~FileAttributes.ReadOnly);
                }
                File.Delete(dest);
            }
        }
    }
}
