namespace BaBaBooeyStrap.Core.Models;

public class AppState
{
    public string Version { get; set; } = string.Empty;
    public string VersionGuid { get; set; } = string.Empty;
    private Dictionary<string, string> _packageHashes = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> PackageHashes
    {
        get => _packageHashes;
        set
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (value != null)
            {
                foreach (var kv in value)
                {
                    dict[kv.Key] = kv.Value;
                }
            }
            _packageHashes = dict;
        }
    }
    public long Size { get; set; }
}
