global using MultiInstanceStatus = BaBaBooeyStrap.MultiInstance.Abstractions.MultiInstanceStatus;
global using IMultiInstanceController = BaBaBooeyStrap.MultiInstance.Abstractions.IMultiInstanceController;
global using WindowsMultiInstanceController = BaBaBooeyStrap.MultiInstance.WindowsMultiInstanceController;
global using LaunchGate = BaBaBooeyStrap.MultiInstance.LaunchGate;
global using ProcessSupervisor = BaBaBooeyStrap.MultiInstance.ProcessSupervisor;

using System.Runtime.InteropServices;

namespace BaBaBooeyStrap.Tests.Tier1_FeatureCoverage.Engines;

/// <summary>
/// Native Win32 test window helper for isolated visibility probing tests.
/// </summary>
public sealed class NativeTestWindow : IDisposable
{
    public nint Handle { get; }

    public NativeTestWindow(string title = "TestNativeWindow", bool visible = true)
    {
        int style = visible ? (0x10000000 | 0x00C00000) : 0; // WS_VISIBLE | WS_CAPTION
        Handle = CreateWindowExW(0, "STATIC", title, style, 0, 0, 200, 200, nint.Zero, nint.Zero, nint.Zero, nint.Zero);
    }

    public void Dispose()
    {
        if (Handle != nint.Zero)
        {
            DestroyWindow(Handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(
        int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
        int X, int Y, int nWidth, int nHeight,
        nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hWnd);
}
