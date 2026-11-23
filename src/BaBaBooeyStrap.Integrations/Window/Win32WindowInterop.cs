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

    public bool SetWindowText(nint hWnd, string text)
    {
        bool ok = User32.SetWindowTextW(hWnd, text);
        if (ok && string.Equals(GetWindowText(hWnd), text, StringComparison.Ordinal))
        {
            return true;
        }

        nint sent = User32.SendMessageTimeoutW(
            hWnd,
            User32.WM_SETTEXT,
            nint.Zero,
            text,
            User32.SMTO_NORMAL | User32.SMTO_ABORTIFHUNG,
            250,
            out nint result);

        return (sent != nint.Zero && result != nint.Zero) ||
               string.Equals(GetWindowText(hWnd), text, StringComparison.Ordinal);
    }

    public nint GetWindowLongPtr(nint hWnd, int nIndex) => User32.GetWindowLongPtr(hWnd, nIndex);

    public nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong) => User32.SetWindowLongPtr(hWnd, nIndex, dwNewLong);

    public bool GetWindowRect(nint hWnd, out User32.RECT rect) => User32.GetWindowRect(hWnd, out rect);

    public bool IsIconic(nint hWnd) => User32.IsIconic(hWnd);

    public bool TryGetNormalWindowRect(nint hWnd, out User32.RECT rect)
    {
        var wp = new User32.WINDOWPLACEMENT
        {
            length = Marshal.SizeOf<User32.WINDOWPLACEMENT>()
        };
        if (User32.GetWindowPlacement(hWnd, ref wp) &&
            wp.rcNormalPosition.Width > 0 &&
            wp.rcNormalPosition.Height > 0)
        {
            int dx = 0;
            int dy = 0;
            nint hMonitor = User32.MonitorFromWindow(hWnd, User32.MONITOR_DEFAULTTONEAREST);
            if (hMonitor != nint.Zero)
            {
                var mi = new User32.MONITORINFO
                {
                    cbSize = Marshal.SizeOf<User32.MONITORINFO>()
                };
                if (User32.GetMonitorInfoW(hMonitor, ref mi))
                {
                    dx = mi.rcWork.Left - mi.rcMonitor.Left;
                    dy = mi.rcWork.Top - mi.rcMonitor.Top;
                }
            }

            rect = new User32.RECT
            {
                Left = wp.rcNormalPosition.Left + dx,
                Top = wp.rcNormalPosition.Top + dy,
                Right = wp.rcNormalPosition.Right + dx,
                Bottom = wp.rcNormalPosition.Bottom + dy
            };
            return true;
        }

        return User32.GetWindowRect(hWnd, out rect);
    }

    public bool EnumWindows(User32.EnumWindowsProc callback) => User32.EnumWindows(callback, nint.Zero);

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

    public nint SendMessageTimeout(nint hWnd, uint msg, nint wParam, nint lParam, uint timeoutMs = 200)
    {
        nint sent = User32.SendMessageTimeoutW(
            hWnd,
            msg,
            wParam,
            lParam,
            User32.SMTO_ABORTIFHUNG,
            timeoutMs,
            out nint result);
        return sent != nint.Zero ? result : nint.Zero;
    }

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

    public bool ShowWindow(nint hWnd, int nCmdShow) =>
        User32.ShowWindow(hWnd, nCmdShow);

    public bool IsZoomed(nint hWnd) =>
        User32.IsZoomed(hWnd);

    public nint GetForegroundWindow() =>
        User32.GetForegroundWindow();

    public bool SetForegroundWindow(nint hWnd) =>
        User32.SetForegroundWindow(hWnd);

    public bool BringWindowToTop(nint hWnd) =>
        User32.BringWindowToTop(hWnd);

    public bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach) =>
        User32.AttachThreadInput(idAttach, idAttachTo, fAttach);

    public bool AllowSetForegroundWindow(int dwProcessId) =>
        User32.AllowSetForegroundWindow(dwProcessId);

    public bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam) =>
        User32.PostMessageW(hWnd, msg, wParam, lParam);

    public bool TryGetExtendedFrameBounds(nint hWnd, out User32.RECT rect) =>
        Dwmapi.DwmGetWindowAttribute(
            hWnd,
            Dwmapi.DWMWA_EXTENDED_FRAME_BOUNDS,
            out rect,
            Marshal.SizeOf<User32.RECT>()) == 0;
}
