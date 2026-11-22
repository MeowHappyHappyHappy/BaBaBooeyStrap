using BaBaBooeyStrap.Core.Interop;

namespace BaBaBooeyStrap.Integrations.Window;

public sealed record MonitorInfo(
    int Index,
    string DeviceName,
    bool IsPrimary,
    User32.RECT MonitorBounds,
    User32.RECT WorkArea)
{
    public int Width => MonitorBounds.Width;
    public int Height => MonitorBounds.Height;

    public string DisplayLabel => IsPrimary
        ? $"Monitor {Index} [Primary] ({Width}×{Height})"
        : $"Monitor {Index} ({Width}×{Height})";
}
