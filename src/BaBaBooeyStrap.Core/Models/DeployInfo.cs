namespace BaBaBooeyStrap.Core.Models;

public class DeployInfo
{
    public string Timestamp { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string VersionGuid { get; set; } = string.Empty;
    public string ClientVersionUpload { get; set; } = string.Empty;
    public string BootstrapperVersion { get; set; } = string.Empty;
}
