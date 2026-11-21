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
    User32.RECT GetMonitorRectFromWindow(nint hWnd);
    int GetSystemMetrics(int nIndex);
    bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    nint SendMessage(nint hWnd, uint msg, nint wParam, nint lParam);
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
}
