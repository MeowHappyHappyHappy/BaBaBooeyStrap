namespace BaBaBooeyStrap.Integrations.Window;

public interface IMonitorProvider
{
    IReadOnlyList<MonitorInfo> GetMonitors();
    MonitorInfo GetPrimaryMonitor();
    MonitorInfo GetMonitorFromCursor();
    MonitorInfo GetMonitorFromWindow(nint hWnd);
    MonitorInfo? FindMonitorByDeviceName(string? deviceName);
}
