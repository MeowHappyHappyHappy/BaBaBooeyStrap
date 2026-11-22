using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using BaBaBooeyStrap.Core.Interop;
using BaBaBooeyStrap.Core.Logging;
using BaBaBooeyStrap.Core.Models;

namespace BaBaBooeyStrap.App.Services;

public sealed class GlobalHotkeyService : IDisposable
{
    public const string KeyQuickSwitcher = "QuickSwitcher";
    public const string KeyCycleWindow = "CycleWindow";
    public const string KeyCyclePreviousWindow = "CyclePreviousWindow";
    public const string KeyDirectSlots = "DirectSlots";

    private const int QuickSwitcherHotkeyId = 100;
    private const int CycleWindowHotkeyId = 101;
    private const int CyclePreviousWindowHotkeyId = 102;

    private readonly ILogger _logger;
    private readonly List<int> _registeredIds = new();
    private readonly Dictionary<string, bool> _registrationResults = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<uint> _swallowedDirectSlotVks = new();
    private Dispatcher? _dispatcher;
    private HwndSource? _hwndSource;
    private User32.LowLevelKeyboardProc? _keyboardHookProc;
    private nint _keyboardHookHandle;
    private uint _directSlotModifiers;
    private bool _directSlotsEnabled;
    private bool _disposed;

    public event EventHandler? QuickSwitcherRequested;
    public event EventHandler? CycleNextWindowRequested;
    public event EventHandler? CyclePreviousWindowRequested;
    public event EventHandler<int>? DirectSlotRequested;

    public GlobalHotkeyService(ILogger? logger = null, Dispatcher? dispatcher = null)
    {
        _logger = logger ?? Logger.Current;
        _dispatcher = dispatcher ?? Application.Current?.Dispatcher;
    }

    public IReadOnlyDictionary<string, bool> RegistrationResults => _registrationResults;

    public bool IsRegistered(string key) =>
        _registrationResults.TryGetValue(key, out bool ok) && ok;

    public void Initialize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _dispatcher ??= Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
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

        if (settings.EnableCyclePreviousWindowHotkey)
        {
            bool ok = false;
            if (HotkeyParser.TryParse(settings.CyclePreviousWindowHotkey, out var parsed))
            {
                ok = User32.RegisterHotKey(hwnd, CyclePreviousWindowHotkeyId, parsed.Modifiers, parsed.VirtualKey);
                if (ok)
                {
                    _registeredIds.Add(CyclePreviousWindowHotkeyId);
                }
                else
                {
                    _logger.Warn("GlobalHotkeyService", $"Failed to register CyclePreviousWindow hotkey '{settings.CyclePreviousWindowHotkey}'.");
                }
            }
            _registrationResults[KeyCyclePreviousWindow] = ok;
        }
        else
        {
            _registrationResults[KeyCyclePreviousWindow] = true;
        }

        if (settings.EnableDirectSlotHotkeys)
        {
            bool hookOk = false;
            if (HotkeyParser.TryParseModifiersOnly(settings.DirectSlotHotkeyModifier, out uint mods, out _))
            {
                _directSlotModifiers = mods & ~User32.MOD_NOREPEAT;
                _directSlotsEnabled = true;
                _keyboardHookProc ??= KeyboardHookCallback;
                _keyboardHookHandle = User32.SetWindowsHookExW(User32.WH_KEYBOARD_LL, _keyboardHookProc, nint.Zero, 0);
                hookOk = _keyboardHookHandle != nint.Zero;
                if (!hookOk)
                {
                    _directSlotsEnabled = false;
                    _directSlotModifiers = 0;
                    _logger.Warn("GlobalHotkeyService", $"Failed to install direct slot keyboard hook for '{settings.DirectSlotHotkeyModifier}+1..9'.");
                }
            }
            _registrationResults[KeyDirectSlots] = hookOk;
        }
        else
        {
            _registrationResults[KeyDirectSlots] = true;
        }
    }

    internal static bool TryMatchDirectSlotKey(
        uint configuredModifiers,
        uint currentModifiers,
        uint vkCode,
        bool isKeyDown,
        ISet<uint> swallowedKeys,
        out int matchedSlot,
        out bool shouldSwallow)
    {
        ArgumentNullException.ThrowIfNull(swallowedKeys);

        matchedSlot = 0;
        shouldSwallow = false;

        int slot = vkCode switch
        {
            >= 0x31 and <= 0x39 => (int)(vkCode - 0x30),
            >= 0x61 and <= 0x69 => (int)(vkCode - 0x60),
            _ => 0
        };

        if (slot == 0)
        {
            return false;
        }

        uint requiredMods = configuredModifiers & ~User32.MOD_NOREPEAT;

        if (isKeyDown)
        {
            if (requiredMods != 0 && currentModifiers == requiredMods)
            {
                shouldSwallow = true;
                if (swallowedKeys.Add(vkCode))
                {
                    matchedSlot = slot;
                }
                return true;
            }

            return false;
        }

        if (swallowedKeys.Remove(vkCode))
        {
            shouldSwallow = true;
            return true;
        }

        return false;
    }

    private nint KeyboardHookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && _directSlotsEnabled && !_disposed && lParam != nint.Zero)
        {
            long msg = wParam.ToInt64();
            bool isKeyDown = msg is User32.WM_KEYDOWN or User32.WM_SYSKEYDOWN;
            bool isKeyUp = msg is User32.WM_KEYUP or User32.WM_SYSKEYUP;

            if (isKeyDown || isKeyUp)
            {
                var info = Marshal.PtrToStructure<User32.KBDLLHOOKSTRUCT>(lParam);
                bool isInjected = (info.flags & (User32.LLKHF_INJECTED | User32.LLKHF_LOWER_IL_INJECTED)) != 0;
                if (!isInjected)
                {
                    uint currentMods = isKeyDown ? ReadCurrentAsyncModifiers() : 0;
                    if (TryMatchDirectSlotKey(
                            _directSlotModifiers,
                            currentMods,
                            info.vkCode,
                            isKeyDown,
                            _swallowedDirectSlotVks,
                            out int matchedSlot,
                            out bool shouldSwallow))
                    {
                        if (matchedSlot > 0)
                        {
                            var dispatcher = _dispatcher ?? Application.Current?.Dispatcher;
                            if (dispatcher != null)
                            {
                                _ = dispatcher.BeginInvoke(() =>
                                {
                                    if (!_disposed)
                                    {
                                        DirectSlotRequested?.Invoke(this, matchedSlot);
                                    }
                                });
                            }
                            else
                            {
                                DirectSlotRequested?.Invoke(this, matchedSlot);
                            }
                        }

                        if (shouldSwallow)
                        {
                            return (nint)1;
                        }
                    }
                }
            }
        }

        return User32.CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
    }

    private static uint ReadCurrentAsyncModifiers()
    {
        uint mods = 0;
        if ((User32.GetAsyncKeyState(User32.VK_CONTROL) & 0x8000) != 0) mods |= User32.MOD_CONTROL;
        if ((User32.GetAsyncKeyState(User32.VK_MENU) & 0x8000) != 0) mods |= User32.MOD_ALT;
        if ((User32.GetAsyncKeyState(User32.VK_SHIFT) & 0x8000) != 0) mods |= User32.MOD_SHIFT;
        if ((User32.GetAsyncKeyState(User32.VK_LWIN) & 0x8000) != 0 ||
            (User32.GetAsyncKeyState(User32.VK_RWIN) & 0x8000) != 0)
        {
            mods |= User32.MOD_WIN;
        }
        return mods;
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
            else if (id == CyclePreviousWindowHotkeyId)
            {
                handled = true;
                CyclePreviousWindowRequested?.Invoke(this, EventArgs.Empty);
            }
        }

        return nint.Zero;
    }

    private void UnregisterAll()
    {
        _directSlotsEnabled = false;
        _directSlotModifiers = 0;
        _swallowedDirectSlotVks.Clear();

        if (_keyboardHookHandle != nint.Zero)
        {
            try { User32.UnhookWindowsHookEx(_keyboardHookHandle); } catch { }
            _keyboardHookHandle = nint.Zero;
        }

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
