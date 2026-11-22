using System.Runtime.InteropServices;

namespace BaBaBooeyStrap.Core.Interop;

public static class Dwmapi
{
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(
        nint hwnd,
        int dwAttribute,
        out User32.RECT pvAttribute,
        int cbAttribute);
}
