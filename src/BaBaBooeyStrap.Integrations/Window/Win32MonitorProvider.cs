using System.Runtime.InteropServices;
using BaBaBooeyStrap.Core.Interop;

namespace BaBaBooeyStrap.Integrations.Window;

public sealed class Win32MonitorProvider : IMonitorProvider
{
    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var raw = new List<(string DeviceName, bool IsPrimary, User32.RECT Bounds, User32.RECT Work)>();

        try
        {
            User32.EnumDisplayMonitors(
                nint.Zero,
                nint.Zero,
                (nint hMonitor, nint _, ref User32.RECT _, nint _) =>
                {
                    var mi = new User32.MONITORINFOEX
                    {
                        cbSize = Marshal.SizeOf<User32.MONITORINFOEX>(),
                        szDevice = string.Empty
                    };
                    if (User32.GetMonitorInfoW(hMonitor, ref mi))
                    {
                        bool isPrimary = (mi.dwFlags & User32.MONITORINFOF_PRIMARY) != 0;
                        string device = string.IsNullOrWhiteSpace(mi.szDevice)
                            ? $"DISPLAY_{raw.Count + 1}"
                            : mi.szDevice;
                        raw.Add((device, isPrimary, mi.rcMonitor, mi.rcWork));
                    }
                    return true;
                },
                nint.Zero);
        }
        catch
        {
            // Fall back below if monitor enumeration fails
        }

        if (raw.Count == 0)
        {
            int w = Math.Max(1, User32.GetSystemMetrics(User32.SM_CXSCREEN));
            int h = Math.Max(1, User32.GetSystemMetrics(User32.SM_CYSCREEN));
            var rect = new User32.RECT { Left = 0, Top = 0, Right = w, Bottom = h };
            return [new MonitorInfo(1, @"\\.\DISPLAY1", true, rect, rect)];
        }

        var ordered = raw
            .OrderByDescending(m => m.IsPrimary)
            .ThenBy(m => m.Bounds.Left)
            .ThenBy(m => m.Bounds.Top)
            .ThenBy(m => m.DeviceName, StringComparer.OrdinalIgnoreCase)
            .Select((m, idx) => new MonitorInfo(idx + 1, m.DeviceName, m.IsPrimary, m.Bounds, m.Work))
            .ToList();

        return ordered.AsReadOnly();
    }

    public MonitorInfo GetPrimaryMonitor()
    {
        var monitors = GetMonitors();
        return monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
    }

    public MonitorInfo GetMonitorFromCursor()
    {
        if (User32.GetCursorPos(out var pt))
        {
            nint hMonitor = User32.MonitorFromPoint(pt, User32.MONITOR_DEFAULTTONEAREST);
            var matched = MatchMonitorHandle(hMonitor);
            if (matched != null)
            {
                return matched;
            }
        }

        return GetPrimaryMonitor();
    }

    public MonitorInfo GetMonitorFromWindow(nint hWnd)
    {
        if (hWnd != nint.Zero)
        {
            nint hMonitor = User32.MonitorFromWindow(hWnd, User32.MONITOR_DEFAULTTONEAREST);
            var matched = MatchMonitorHandle(hMonitor);
            if (matched != null)
            {
                return matched;
            }
        }

        return GetPrimaryMonitor();
    }

    public MonitorInfo? FindMonitorByDeviceName(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return null;
        }

        return GetMonitors().FirstOrDefault(m =>
            string.Equals(m.DeviceName, deviceName.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private MonitorInfo? MatchMonitorHandle(nint hMonitor)
    {
        if (hMonitor == nint.Zero)
        {
            return null;
        }

        var mi = new User32.MONITORINFOEX
        {
            cbSize = Marshal.SizeOf<User32.MONITORINFOEX>(),
            szDevice = string.Empty
        };

        if (!User32.GetMonitorInfoW(hMonitor, ref mi))
        {
            return null;
        }

        var monitors = GetMonitors();
        if (!string.IsNullOrWhiteSpace(mi.szDevice))
        {
            var byDevice = monitors.FirstOrDefault(m =>
                string.Equals(m.DeviceName, mi.szDevice, StringComparison.OrdinalIgnoreCase));
            if (byDevice != null)
            {
                return byDevice;
            }
        }

        return monitors.FirstOrDefault(m =>
            m.MonitorBounds.Left == mi.rcMonitor.Left &&
            m.MonitorBounds.Top == mi.rcMonitor.Top &&
            m.MonitorBounds.Right == mi.rcMonitor.Right &&
            m.MonitorBounds.Bottom == mi.rcMonitor.Bottom);
    }
}
