using BaBaBooeyStrap.Core.Interop;

namespace BaBaBooeyStrap.Integrations.Window;

public sealed class WindowTilingService
{
    private const int MaxReasonableShadowInset = 24;

    private readonly IWin32WindowInterop _interop;
    private readonly IMonitorProvider _monitorProvider;

    public WindowTilingService(IWin32WindowInterop interop, IMonitorProvider monitorProvider)
    {
        _interop = interop ?? throw new ArgumentNullException(nameof(interop));
        _monitorProvider = monitorProvider ?? throw new ArgumentNullException(nameof(monitorProvider));
    }

    public static IReadOnlyList<User32.RECT> CalculateGridRects(User32.RECT workArea, int count)
    {
        if (count <= 0 || workArea.Width <= 0 || workArea.Height <= 0)
        {
            return Array.Empty<User32.RECT>();
        }

        int cols = (int)Math.Ceiling(Math.Sqrt(count));
        int rows = (int)Math.Ceiling((double)count / cols);

        int baseColW = workArea.Width / cols;
        int baseRowH = workArea.Height / rows;

        var rects = new List<User32.RECT>(count);
        for (int i = 0; i < count; i++)
        {
            int row = i / cols;
            int col = i % cols;

            int left = workArea.Left + col * baseColW;
            int top = workArea.Top + row * baseRowH;
            int right = col == cols - 1 ? workArea.Right : left + baseColW;
            int bottom = row == rows - 1 ? workArea.Bottom : top + baseRowH;

            rects.Add(new User32.RECT
            {
                Left = left,
                Top = top,
                Right = right,
                Bottom = bottom
            });
        }

        return rects.AsReadOnly();
    }

    public int TileSingleMonitor(IReadOnlyList<nint> hwnds, MonitorInfo? targetMonitor = null)
    {
        var valid = FilterValidWindows(hwnds);
        if (valid.Count == 0)
        {
            return 0;
        }

        var monitor = targetMonitor ?? _monitorProvider.GetPrimaryMonitor();
        var rects = CalculateGridRects(monitor.WorkArea, valid.Count);

        int positioned = 0;
        for (int i = 0; i < valid.Count && i < rects.Count; i++)
        {
            if (ApplyWindowBounds(valid[i], rects[i]))
            {
                positioned++;
            }
        }

        return positioned;
    }

    public int TileSpreadAcrossMonitors(IReadOnlyList<nint> hwnds, IReadOnlyList<MonitorInfo>? monitors = null)
    {
        var valid = FilterValidWindows(hwnds);
        if (valid.Count == 0)
        {
            return 0;
        }

        var targetMonitors = monitors is { Count: > 0 }
            ? monitors
            : _monitorProvider.GetMonitors();

        if (targetMonitors.Count <= 1)
        {
            return TileSingleMonitor(valid, targetMonitors.FirstOrDefault());
        }

        int total = valid.Count;
        int monitorCount = targetMonitors.Count;
        int basePerMonitor = total / monitorCount;
        int remainder = total % monitorCount;

        int cursor = 0;
        int positioned = 0;
        for (int m = 0; m < monitorCount && cursor < total; m++)
        {
            int sliceCount = basePerMonitor + (m < remainder ? 1 : 0);
            if (sliceCount <= 0)
            {
                continue;
            }

            var slice = valid.Skip(cursor).Take(sliceCount).ToList();
            cursor += sliceCount;
            positioned += TileSingleMonitor(slice, targetMonitors[m]);
        }

        return positioned;
    }

    public int TileMainAndSide(
        nint mainHwnd,
        IReadOnlyList<nint> sideHwnds,
        MonitorInfo? mainMonitor = null,
        MonitorInfo? sideMonitor = null)
    {
        var allMonitors = _monitorProvider.GetMonitors();
        var resolvedMainMonitor = mainMonitor ?? _monitorProvider.GetPrimaryMonitor();
        var resolvedSideMonitor = sideMonitor
            ?? allMonitors.FirstOrDefault(m => !string.Equals(m.DeviceName, resolvedMainMonitor.DeviceName, StringComparison.OrdinalIgnoreCase))
            ?? resolvedMainMonitor;

        bool hasMain = mainHwnd != nint.Zero && _interop.IsWindow(mainHwnd);
        var validSides = FilterValidWindows(sideHwnds)
            .Where(h => !hasMain || h != mainHwnd)
            .ToList();

        if (!hasMain && validSides.Count == 0)
        {
            return 0;
        }

        if (string.Equals(resolvedMainMonitor.DeviceName, resolvedSideMonitor.DeviceName, StringComparison.OrdinalIgnoreCase))
        {
            var combined = new List<nint>();
            if (hasMain) combined.Add(mainHwnd);
            combined.AddRange(validSides);
            return TileSingleMonitor(combined, resolvedMainMonitor);
        }

        int positioned = 0;
        if (hasMain && ApplyWindowBounds(mainHwnd, resolvedMainMonitor.WorkArea))
        {
            positioned++;
        }

        if (validSides.Count > 0)
        {
            positioned += TileSingleMonitor(validSides, resolvedSideMonitor);
        }

        return positioned;
    }

    internal bool ApplyWindowBounds(nint hWnd, User32.RECT targetRect)
    {
        if (hWnd == nint.Zero || !_interop.IsWindow(hWnd))
        {
            return false;
        }

        if (_interop.IsIconic(hWnd) || _interop.IsZoomed(hWnd))
        {
            _interop.ShowWindow(hWnd, User32.SW_RESTORE);
        }

        int x = targetRect.Left;
        int y = targetRect.Top;
        int width = Math.Max(1, targetRect.Width);
        int height = Math.Max(1, targetRect.Height);

        if (_interop.GetWindowRect(hWnd, out var winRect) &&
            _interop.TryGetExtendedFrameBounds(hWnd, out var extRect))
        {
            int leftInset = extRect.Left - winRect.Left;
            int topInset = extRect.Top - winRect.Top;
            int rightInset = winRect.Right - extRect.Right;
            int bottomInset = winRect.Bottom - extRect.Bottom;

            if (leftInset is >= 0 and <= MaxReasonableShadowInset &&
                topInset is >= 0 and <= MaxReasonableShadowInset &&
                rightInset is >= 0 and <= MaxReasonableShadowInset &&
                bottomInset is >= 0 and <= MaxReasonableShadowInset)
            {
                x -= leftInset;
                y -= topInset;
                width += leftInset + rightInset;
                height += topInset + bottomInset;
            }
        }

        return _interop.SetWindowPos(
            hWnd,
            nint.Zero,
            x,
            y,
            width,
            height,
            User32.SWP_NOZORDER | User32.SWP_NOACTIVATE | User32.SWP_SHOWWINDOW);
    }

    private List<nint> FilterValidWindows(IReadOnlyList<nint> hwnds)
    {
        if (hwnds == null || hwnds.Count == 0)
        {
            return [];
        }

        var result = new List<nint>(hwnds.Count);
        var seen = new HashSet<nint>();
        foreach (nint h in hwnds)
        {
            if (h != nint.Zero && seen.Add(h) && _interop.IsWindow(h))
            {
                result.Add(h);
            }
        }

        return result;
    }
}
