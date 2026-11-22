using BaBaBooeyStrap.Core.Interop;

namespace BaBaBooeyStrap.Integrations.Window;

public sealed class WindowFocusHelper
{
    private readonly IWin32WindowInterop _interop;

    public WindowFocusHelper(IWin32WindowInterop interop)
    {
        _interop = interop ?? throw new ArgumentNullException(nameof(interop));
    }

    public bool ActivateWindow(nint hWnd)
    {
        if (hWnd == nint.Zero || !_interop.IsWindow(hWnd))
        {
            return false;
        }

        if (_interop.IsIconic(hWnd))
        {
            _interop.ShowWindow(hWnd, User32.SW_RESTORE);
        }

        nint fgHwnd = _interop.GetForegroundWindow();
        uint currentThread = _interop.GetCurrentThreadId();
        uint fgThread = fgHwnd != nint.Zero ? _interop.GetWindowThreadProcessId(fgHwnd, out _) : 0;
        uint targetThread = _interop.GetWindowThreadProcessId(hWnd, out _);

        bool attachedFg = false;
        bool attachedTarget = false;

        try
        {
            if (fgThread != 0 && fgThread != currentThread)
            {
                attachedFg = _interop.AttachThreadInput(currentThread, fgThread, true);
            }

            if (targetThread != 0 && targetThread != currentThread && targetThread != fgThread)
            {
                attachedTarget = _interop.AttachThreadInput(currentThread, targetThread, true);
            }

            _interop.AllowSetForegroundWindow(User32.ASFW_ANY);
            _interop.BringWindowToTop(hWnd);
            bool focused = _interop.SetForegroundWindow(hWnd);

            if (!focused && _interop.GetForegroundWindow() != hWnd)
            {
                const uint flags = User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_SHOWWINDOW;
                _interop.SetWindowPos(hWnd, User32.HWND_TOPMOST, 0, 0, 0, 0, flags);
                _interop.SetWindowPos(hWnd, User32.HWND_NOTOPMOST, 0, 0, 0, 0, flags);
                focused = _interop.SetForegroundWindow(hWnd);
            }

            return focused || _interop.GetForegroundWindow() == hWnd;
        }
        finally
        {
            if (attachedTarget)
            {
                _interop.AttachThreadInput(currentThread, targetThread, false);
            }

            if (attachedFg)
            {
                _interop.AttachThreadInput(currentThread, fgThread, false);
            }
        }
    }

    public bool ToggleMinimizeRestore(nint hWnd)
    {
        if (hWnd == nint.Zero || !_interop.IsWindow(hWnd))
        {
            return false;
        }

        if (_interop.IsIconic(hWnd))
        {
            return ActivateWindow(hWnd);
        }

        return _interop.ShowWindow(hWnd, User32.SW_MINIMIZE);
    }

    public bool MinimizeWindow(nint hWnd)
    {
        if (hWnd == nint.Zero || !_interop.IsWindow(hWnd))
        {
            return false;
        }

        if (_interop.IsIconic(hWnd))
        {
            return true;
        }

        return _interop.ShowWindow(hWnd, User32.SW_MINIMIZE);
    }

    public bool CloseWindow(nint hWnd)
    {
        if (hWnd == nint.Zero || !_interop.IsWindow(hWnd))
        {
            return false;
        }

        return _interop.PostMessage(hWnd, User32.WM_CLOSE, nint.Zero, nint.Zero);
    }

    public bool MoveWindowToMonitor(nint hWnd, MonitorInfo targetMonitor, bool activateAfterMove = true)
    {
        ArgumentNullException.ThrowIfNull(targetMonitor);

        if (hWnd == nint.Zero || !_interop.IsWindow(hWnd))
        {
            return false;
        }

        if (_interop.IsIconic(hWnd) || _interop.IsZoomed(hWnd))
        {
            _interop.ShowWindow(hWnd, User32.SW_RESTORE);
        }

        User32.RECT bounds;
        if (!_interop.GetWindowRect(hWnd, out bounds) || bounds.Width <= 0 || bounds.Height <= 0)
        {
            if (!_interop.TryGetNormalWindowRect(hWnd, out bounds) || bounds.Width <= 0 || bounds.Height <= 0)
            {
                bounds = new User32.RECT { Left = 0, Top = 0, Right = 1280, Bottom = 720 };
            }
        }

        var work = targetMonitor.WorkArea;
        int targetW = Math.Clamp(bounds.Width, Math.Min(320, work.Width), Math.Max(1, work.Width));
        int targetH = Math.Clamp(bounds.Height, Math.Min(240, work.Height), Math.Max(1, work.Height));
        int targetX = work.Left + Math.Max(0, (work.Width - targetW) / 2);
        int targetY = work.Top + Math.Max(0, (work.Height - targetH) / 2);

        uint flags = User32.SWP_NOZORDER | User32.SWP_SHOWWINDOW;
        if (!activateAfterMove)
        {
            flags |= User32.SWP_NOACTIVATE;
        }

        bool moved = _interop.SetWindowPos(hWnd, nint.Zero, targetX, targetY, targetW, targetH, flags);
        if (moved && activateAfterMove)
        {
            ActivateWindow(hWnd);
        }

        return moved;
    }
}
