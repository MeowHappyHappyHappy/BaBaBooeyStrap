using System.Reflection;

namespace BaBaBooeyStrap.Bootstrapper.Mods.Presets;

public static class AvatarBackgroundPresetHandler
{
    public const string RelativeMobileRbxlPath = @"ExtraContent\places\Mobile.rbxl";
    public const string ResourceName = "BaBaBooeyStrap.Bootstrapper.Resources.Mods.OldAvatarBackground.rbxl";

    public static void SetOldAvatarBackground(bool enabled, string destinationBaseDir)
    {
        string dest = Path.Combine(destinationBaseDir, RelativeMobileRbxlPath);
        if (enabled)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream(ResourceName);
            if (stream != null)
            {
                using var destStream = File.Create(dest);
                stream.CopyTo(destStream);
            }
            else
            {
                File.WriteAllText(dest, "<roblox xmlns:xmime=\"http://www.w3.org/2005/05/xmlmime\" version=\"4\"></roblox>");
            }
        }
        else
        {
            if (File.Exists(dest))
            {
                File.Delete(dest);
            }
        }
    }
}
