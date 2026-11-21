namespace BaBaBooeyStrap.Core.Models;

public class CustomIntegration
{
    public string Name { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string LaunchArgs { get; set; } = string.Empty;
    public int Delay { get; set; }
    public bool PreLaunch { get; set; }
    public bool AutoClose { get; set; } = true;
}
