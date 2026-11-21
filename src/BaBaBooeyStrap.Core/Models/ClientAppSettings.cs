namespace BaBaBooeyStrap.Core.Models;

public class ClientAppSettings : Dictionary<string, object>
{
    public ClientAppSettings() : base(StringComparer.OrdinalIgnoreCase) { }
    public ClientAppSettings(IDictionary<string, object> dictionary) : base(dictionary, StringComparer.OrdinalIgnoreCase) { }
}
