using System.Runtime.InteropServices;

namespace BaBaBooeyStrap.App.Interop;

internal static class AppNativeMethods
{
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessageW(string lpString);
}
