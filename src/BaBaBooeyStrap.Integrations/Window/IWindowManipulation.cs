namespace BaBaBooeyStrap.Integrations.Window;

public interface IWindowManipulation : IDisposable
{
    void StartHook();
    void StopHook();
    bool SetCustomTitle(nint hWnd, string template, IReadOnlyDictionary<string, string> placeholders);
    bool SetCustomIcon(nint hWnd, string iconPath);
    bool SetBorderlessFullscreen(nint hWnd, bool enabled);
}
