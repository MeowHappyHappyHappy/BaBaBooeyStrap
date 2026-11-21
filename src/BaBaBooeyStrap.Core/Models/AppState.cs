namespace BaBaBooeyStrap.Core.Models;

public class AppState
{
    public string Version { get; set; } = string.Empty;
    public string VersionGuid { get; set; } = string.Empty;
    public Dictionary<string, string> PackageHashes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public long Size { get; set; }
}
