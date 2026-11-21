using System.Runtime.InteropServices;

namespace BaBaBooeyStrap.Core.Interop;

public static class Shell32
{
    public const uint SHOP_PRINTERNAME = 0x00000001;
    public const uint SHOP_FILEPATH = 0x00000002;
    public const uint SHOP_VOLUMEGUID = 0x00000004;

    [DllImport("shell32.dll", EntryPoint = "ShellExecuteW", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern nint ShellExecute(
        nint hwnd,
        string? lpOperation,
        string lpFile,
        string? lpParameters,
        string? lpDirectory,
        int nShowCmd);

    [DllImport("shell32.dll", EntryPoint = "SHObjectProperties", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SHObjectProperties(
        nint hwnd,
        uint shopObjectType,
        [MarshalAs(UnmanagedType.LPWStr)] string pszObjectName,
        [MarshalAs(UnmanagedType.LPWStr)] string pszPropertyPage);

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern uint ExtractIconExW(
        string lpszFile,
        int nIconIndex,
        [Out] nint[]? phiconLarge,
        [Out] nint[]? phiconSmall,
        uint nIcons);
}
