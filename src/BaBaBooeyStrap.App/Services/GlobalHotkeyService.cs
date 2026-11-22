using System.Windows.Interop;
using BaBaBooeyStrap.Core.Interop;
using BaBaBooeyStrap.Core.Logging;
using BaBaBooeyStrap.Core.Models;

namespace BaBaBooeyStrap.App.Services;

public sealed class GlobalHotkeyService : IDisposable
{
    public const string KeyQuickSwitcher = "QuickSwitcher";
    public const string KeyCycleWindow = "CycleWindow";
    public const string KeyDirectSlots = "DirectSlots";

    private const int QuickSwitcherHotkeyId = 100;
    private const int CycleWindowHotkeyId = 101;
    private const int DirectSlotBaseHotkeyId = 200;

    private readonly ILogger _logger;
    private readonly List<int> _registeredIds = new();
    private readonly Dictionary<string, bool> _registrationResults = new(StringComparer.OrdinalIgnoreCase);
    private HwndSource? _hwndSource;
    private bool _disposed;

    public event EventHandler? QuickSwitcherRequested;
    public event EventHandler? CycleNextWindowRequested;
    public event EventHandler<int>? DirectSlotRequested;

    public GlobalHotkeyService(ILogger? logger = null)
    {
        _logger = logger ?? Logger.Current;
    }

    public IReadOnlyDictionary<string, bool> RegistrationResults => _registrationResults;

    public bool IsRegistered(string key) =>
        _registrationResults.TryGetValue(key, out bool ok) && ok;

    public void Initialize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_hwndSource != null) return;

        var parameters = new HwndSourceParameters("BaBaBooeyStrap.GlobalHotkeySink")
        {
            ParentWindow = new nint(-3), // HWND_MESSAGE
            Width = 0,
            Height = 0,
            WindowStyle = 0
        };

        _hwndSource = new HwndSource(parameters);
        _hwndSource.AddHook(WndProc);
    }

    public void ReloadFromSettings(Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_disposed) return;

        if (_hwndSource == null)
        {
            Initialize();
        }

        UnregisterAll();
        _registrationResults.Clear();

        if (_hwndSource == null || _hwndSource.Handle == nint.Zero)
        {
            return;
        }

        nint hwnd = _hwndSource.Handle;

        if (settings.EnableQuickSwitcherHotkey)
        {
            bool ok = false;
            if (HotkeyParser.TryParse(settings.QuickSwitcherHotkey, out var parsed))
            {
                ok = User32.RegisterHotKey(hwnd, QuickSwitcherHotkeyId, parsed.Modifiers, parsed.VirtualKey);
                if (ok)
                {
                    _registeredIds.Add(QuickSwitcherHotkeyId);
                }
                else
                {
                    _logger.Warn("GlobalHotkeyService", $"Failed to register QuickSwitcher hotkey '{settings.QuickSwitcherHotkey}'.");
                }
            }
            _registrationResults[KeyQuickSwitcher] = ok;
        }
        else
        {
            _registrationResults[KeyQuickSwitcher] = true;
        }

        if (settings.EnableCycleWindowHotkey)
        {
            bool ok = false;
            if (HotkeyParser.TryParse(settings.CycleWindowHotkey, out var parsed))
            {
                ok = User32.RegisterHotKey(hwnd, CycleWindowHotkeyId, parsed.Modifiers, parsed.VirtualKey);
                if (ok)
                {
                    _registeredIds.Add(CycleWindowHotkeyId);
                }
                else
                {
                    _logger.Warn("GlobalHotkeyService", $"Failed to register CycleWindow hotkey '{settings.CycleWindowHotkey}'.");
                }
            }
            _registrationResults[KeyCycleWindow] = ok;
        }
        else
        {
            _registrationResults[KeyCycleWindow] = true;
        }

        if (settings.EnableDirectSlotHotkeys)
        {
            bool allSlotsOk = false;
            if (HotkeyParser.TryParseModifiersOnly(settings.DirectSlotHotkeyModifier, out uint mods, out _))
            {
                int successCount = 0;
                for (int slot = 1; slot <= 9; slot++)
                {
                    int id = DirectSlotBaseHotkeyId + slot;
                    uint vk = (uint)('0' + slot);
                    if (User32.RegisterHotKey(hwnd, id, mods, vk))
                    {
                        _registeredIds.Add(id);
                        successCount++;
                    }
                    else
                    {
                        _logger.Warn("GlobalHotkeyService", $"Failed to register direct slot hotkey '{settings.DirectSlotHotkeyModifier}+{slot}'.");
                    }
                }
                allSlotsOk = successCount == 9;
            }
            _registrationResults[KeyDirectSlots] = allSlotsOk;
        }
        else
        {
            _registrationResults[KeyDirectSlots] = true;
        }
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == User32.WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (id == QuickSwitcherHotkeyId)
            {
                handled = true;
                QuickSwitcherRequested?.Invoke(this, EventArgs.Empty);
            }
            else if (id == CycleWindowHotkeyId)
            {
                handled = true;
                CycleNextWindowRequested?.Invoke(this, EventArgs.Empty);
            }
            else if (id is >= DirectSlotBaseHotkeyId + 1 and <= DirectSlotBaseHotkeyId + 9)
            {
                handled = true;
                DirectSlotRequested?.Invoke(this, id - DirectSlotBaseHotkeyId);
            }
        }

        return nint.Zero;
    }

    private void UnregisterAll()
    {
        if (_hwndSource == null || _hwndSource.Handle == nint.Zero)
        {
            _registeredIds.Clear();
            return;
        }

        nint hwnd = _hwndSource.Handle;
        foreach (int id in _registeredIds)
        {
            try { User32.UnregisterHotKey(hwnd, id); } catch { }
        }
        _registeredIds.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        UnregisterAll();

        if (_hwndSource != null)
        {
            try { _hwndSource.RemoveHook(WndProc); } catch { }
            try { _hwndSource.Dispose(); } catch { }
            _hwndSource = null;
        }

        GC.SuppressFinalize(this);
    }
}
