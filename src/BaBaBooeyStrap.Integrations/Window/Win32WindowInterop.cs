using System.Runtime.InteropServices;
using System.Text;
using BaBaBooeyStrap.Core.Interop;

namespace BaBaBooeyStrap.Integrations.Window;

public sealed class Win32WindowInterop : IWin32WindowInterop
{
    public bool IsWindow(nint hWnd) => User32.IsWindow(hWnd);

    public bool IsWindowVisible(nint hWnd) => User32.IsWindowVisible(hWnd);

    public uint GetWindowThreadProcessId(nint hWnd, out uint processId) =>
        User32.GetWindowThreadProcessId(hWnd, out processId);

    public string GetClassName(nint hWnd)
    {
        var sb = new StringBuilder(256);
        int len = User32.GetClassNameW(hWnd, sb, sb.Capacity);
        return len > 0 ? sb.ToString(0, len) : string.Empty;
    }

    public string GetWindowText(nint hWnd)
    {
        int len = User32.GetWindowTextLengthW(hWnd);
        int capacity = Math.Max(len + 1, 256);
        var sb = new StringBuilder(capacity);
        int read = User32.GetWindowTextW(hWnd, sb, sb.Capacity);
        return read > 0 ? sb.ToString(0, read) : string.Empty;
    }

    public bool SetWindowText(nint hWnd, string text) => User32.SetWindowTextW(hWnd, text);

    public nint GetWindowLongPtr(nint hWnd, int nIndex) => User32.GetWindowLongPtr(hWnd, nIndex);

    public nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong) => User32.SetWindowLongPtr(hWnd, nIndex, dwNewLong);

    public bool GetWindowRect(nint hWnd, out User32.RECT rect) => User32.GetWindowRect(hWnd, out rect);

    public User32.RECT GetMonitorRectFromWindow(nint hWnd)
    {
        nint hMonitor = User32.MonitorFromWindow(hWnd, User32.MONITOR_DEFAULTTONEAREST);
        if (hMonitor != nint.Zero)
        {
            var mi = new User32.MONITORINFO
            {
                cbSize = Marshal.SizeOf<User32.MONITORINFO>()
            };
            if (User32.GetMonitorInfoW(hMonitor, ref mi))
            {
                return mi.rcMonitor;
            }
        }

        return new User32.RECT
        {
            Left = 0,
            Top = 0,
            Right = Math.Max(1, User32.GetSystemMetrics(User32.SM_CXSCREEN)),
            Bottom = Math.Max(1, User32.GetSystemMetrics(User32.SM_CYSCREEN))
        };
    }

    public int GetSystemMetrics(int nIndex) => User32.GetSystemMetrics(nIndex);

    public bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags) =>
        User32.SetWindowPos(hWnd, hWndInsertAfter, x, y, cx, cy, flags);

    public nint SendMessage(nint hWnd, uint msg, nint wParam, nint lParam) =>
        User32.SendMessageW(hWnd, msg, wParam, lParam);

    public nint LoadImageIcon(string filePath, int cx, int cy) =>
        User32.LoadImageW(nint.Zero, filePath, User32.IMAGE_ICON, cx, cy, User32.LR_LOADFROMFILE);

    public (nint SmallIcon, nint LargeIcon) ExtractFirstIconPair(string executablePath)
    {
        nint[] large = new nint[1];
        nint[] small = new nint[1];
        uint extracted = Shell32.ExtractIconExW(executablePath, 0, large, small, 1);
        if (extracted == 0 || extracted == uint.MaxValue)
        {
            return (nint.Zero, nint.Zero);
        }

        return (small[0], large[0]);
    }

    public bool DestroyIcon(nint hIcon) => hIcon != nint.Zero && User32.DestroyIcon(hIcon);

    public nint SetWinEventHook(uint eventMin, uint eventMax, User32.WinEventDelegate callback, uint idProcess, uint idThread, uint flags) =>
        User32.SetWinEventHook(eventMin, eventMax, nint.Zero, callback, idProcess, idThread, flags);

    public bool UnhookWinEvent(nint hWinEventHook) => User32.UnhookWinEvent(hWinEventHook);

    public uint GetCurrentThreadId() => Kernel32.GetCurrentThreadId();

    public bool PeekMessage(out User32.MSG msg, nint hWnd, uint filterMin, uint filterMax, uint removeMsg) =>
        User32.PeekMessageW(out msg, hWnd, filterMin, filterMax, removeMsg);

    public int GetMessage(out User32.MSG msg, nint hWnd, uint filterMin, uint filterMax) =>
        User32.GetMessageW(out msg, hWnd, filterMin, filterMax);

    public bool TranslateMessage(ref User32.MSG msg) => User32.TranslateMessage(ref msg);

    public nint DispatchMessage(ref User32.MSG msg) => User32.DispatchMessageW(ref msg);

    public bool PostThreadMessage(uint idThread, uint msg, nint wParam, nint lParam) =>
        User32.PostThreadMessageW(idThread, msg, wParam, lParam);
}
