using BaBaBooeyStrap.Core.Interop;

namespace BaBaBooeyStrap.Integrations.Window;

public interface IWin32WindowInterop
{
    bool IsWindow(nint hWnd);
    bool IsWindowVisible(nint hWnd);
    uint GetWindowThreadProcessId(nint hWnd, out uint processId);
    string GetClassName(nint hWnd);
    string GetWindowText(nint hWnd);
    bool SetWindowText(nint hWnd, string text);
    nint GetWindowLongPtr(nint hWnd, int nIndex);
    nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);
    bool GetWindowRect(nint hWnd, out User32.RECT rect);
    bool IsIconic(nint hWnd) => false;
    bool TryGetNormalWindowRect(nint hWnd, out User32.RECT rect) => GetWindowRect(hWnd, out rect);
    bool EnumWindows(User32.EnumWindowsProc callback) => false;
    User32.RECT GetMonitorRectFromWindow(nint hWnd);
    int GetSystemMetrics(int nIndex);
    bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    nint SendMessage(nint hWnd, uint msg, nint wParam, nint lParam);
    nint SendMessageTimeout(nint hWnd, uint msg, nint wParam, nint lParam, uint timeoutMs = 200) =>
        SendMessage(hWnd, msg, wParam, lParam);
    nint LoadImageIcon(string filePath, int cx, int cy);
    (nint SmallIcon, nint LargeIcon) ExtractFirstIconPair(string executablePath);
    bool DestroyIcon(nint hIcon);
    nint SetWinEventHook(uint eventMin, uint eventMax, User32.WinEventDelegate callback, uint idProcess, uint idThread, uint flags);
    bool UnhookWinEvent(nint hWinEventHook);
    uint GetCurrentThreadId();
    bool PeekMessage(out User32.MSG msg, nint hWnd, uint filterMin, uint filterMax, uint removeMsg);
    int GetMessage(out User32.MSG msg, nint hWnd, uint filterMin, uint filterMax);
    bool TranslateMessage(ref User32.MSG msg);
    nint DispatchMessage(ref User32.MSG msg);
    bool PostThreadMessage(uint idThread, uint msg, nint wParam, nint lParam);
    bool ShowWindow(nint hWnd, int nCmdShow) => false;
    bool IsZoomed(nint hWnd) => false;
    nint GetForegroundWindow() => nint.Zero;
    bool SetForegroundWindow(nint hWnd) => false;
    bool BringWindowToTop(nint hWnd) => false;
    bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach) => false;
    bool AllowSetForegroundWindow(int dwProcessId) => false;
    bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam) => false;
    bool TryGetExtendedFrameBounds(nint hWnd, out User32.RECT rect)
    {
        rect = default;
        return false;
    }
}
