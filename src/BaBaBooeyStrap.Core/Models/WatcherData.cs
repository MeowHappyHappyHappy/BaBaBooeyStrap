namespace BaBaBooeyStrap.Core.Models;

public class WatcherData
{
    public int ProcessId { get; set; }
    public string? LogFile { get; set; }
    public List<int>? AutoclosePids { get; set; }
    public long Handle { get; set; }
}
