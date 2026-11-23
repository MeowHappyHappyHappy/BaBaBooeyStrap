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
    private const long SwallowedKeyMaxAgeMs = 5000;

    private readonly ILogger _logger;
    private readonly object _hookLifecycleLock = new();
    private readonly List<int> _registeredIds = new();
    private readonly Dictionary<string, bool> _registrationResults = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<uint> _swallowedDirectSlotVks = new();
    private readonly Dictionary<uint, long> _swallowedDirectSlotDownTicks = new();
    private readonly List<uint> _expiredSwallowedScratch = new(4);
    private Dispatcher? _dispatcher;
    private HwndSource? _hwndSource;
    private Thread? _hookThread;
    private uint _hookThreadId;
    private CancellationTokenSource? _hookCts;
    private User32.LowLevelKeyboardProc? _keyboardHookProc;
    private GCHandle _keyboardHookDelegateHandle;
    private nint _keyboardHookHandle;
    private volatile uint _directSlotModifiers;
    private volatile bool _directSlotsEnabled;
    private volatile bool _disposed;

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
        RunOnDispatcher(InitializeOnDispatcher);
    }

    private void InitializeOnDispatcher()
    {
        if (_disposed) return;
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

        UninstallKeyboardHook();

        RunOnDispatcher(() =>
        {
            if (_disposed) return;
            InitializeOnDispatcher();
            UnregisterSystemHotkeysOnDispatcher();
            _registrationResults.Clear();

            if (_hwndSource == null || _hwndSource.Handle == nint.Zero)
            {
                return;
            }

            nint hwnd = _hwndSource.Handle;

            RegisterConfiguredHotkey(hwnd, settings.EnableQuickSwitcherHotkey, settings.QuickSwitcherHotkey, QuickSwitcherHotkeyId, KeyQuickSwitcher);
            RegisterConfiguredHotkey(hwnd, settings.EnableCycleWindowHotkey, settings.CycleWindowHotkey, CycleWindowHotkeyId, KeyCycleWindow);
            RegisterConfiguredHotkey(hwnd, settings.EnableCyclePreviousWindowHotkey, settings.CyclePreviousWindowHotkey, CyclePreviousWindowHotkeyId, KeyCyclePreviousWindow);
        });

        if (_disposed) return;

        if (settings.EnableDirectSlotHotkeys)
        {
            bool hookOk = false;
            if (HotkeyParser.TryParseModifiersOnly(settings.DirectSlotHotkeyModifier, out uint mods, out _))
            {
                hookOk = InstallKeyboardHook(mods & ~User32.MOD_NOREPEAT, settings.DirectSlotHotkeyModifier);
            }
            _registrationResults[KeyDirectSlots] = hookOk;
        }
        else
        {
            _registrationResults[KeyDirectSlots] = true;
        }
    }

    private void RegisterConfiguredHotkey(nint hwnd, bool enabled, string hotkeyText, int hotkeyId, string keyName)
    {
        if (!enabled)
        {
            _registrationResults[keyName] = true;
            return;
        }

        bool ok = false;
        if (HotkeyParser.TryParse(hotkeyText, out var parsed))
        {
            ok = User32.RegisterHotKey(hwnd, hotkeyId, parsed.Modifiers, parsed.VirtualKey);
            if (ok)
            {
                _registeredIds.Add(hotkeyId);
            }
            else
            {
                int err = Marshal.GetLastWin32Error();
                _logger.Warn("GlobalHotkeyService", $"Failed to register {keyName} hotkey '{hotkeyText}' (Win32 error {err}).");
            }
        }
        _registrationResults[keyName] = ok;
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
        nint hookHandle = Volatile.Read(ref _keyboardHookHandle);
        try
        {
            if (nCode >= 0 && _directSlotsEnabled && !_disposed && lParam != nint.Zero)
            {
                long msg = wParam.ToInt64();
                bool isKeyDown = msg is User32.WM_KEYDOWN or User32.WM_SYSKEYDOWN;
                bool isKeyUp = msg is User32.WM_KEYUP or User32.WM_SYSKEYUP;

                if (isKeyDown || isKeyUp)
                {
                    // KBDLLHOOKSTRUCT offsets: vkCode @ 0, flags @ 8 (avoids per-keystroke Marshal.PtrToStructure boxing).
                    uint vkCode = unchecked((uint)Marshal.ReadInt32(lParam, 0));
                    uint flags = unchecked((uint)Marshal.ReadInt32(lParam, 8));
                    bool isInjected = (flags & (User32.LLKHF_INJECTED | User32.LLKHF_LOWER_IL_INJECTED)) != 0;
                    if (!isInjected)
                    {
                        long nowTicks = Environment.TickCount64;
                        PruneExpiredSwallowedKeys(nowTicks);

                        uint currentMods = isKeyDown ? ReadCurrentAsyncModifiers() : 0;
                        if (TryMatchDirectSlotKey(
                                _directSlotModifiers,
                                currentMods,
                                vkCode,
                                isKeyDown,
                                _swallowedDirectSlotVks,
                                out int matchedSlot,
                                out bool shouldSwallow))
                        {
                            if (matchedSlot > 0)
                            {
                                _swallowedDirectSlotDownTicks[vkCode] = nowTicks;
                                DispatchDirectSlot(matchedSlot);
                            }
                            else if (!isKeyDown)
                            {
                                _swallowedDirectSlotDownTicks.Remove(vkCode);
                            }

                            if (shouldSwallow)
                            {
                                return (nint)1;
                            }
                        }
                        else if (isKeyDown)
                        {
                            // Stale keyDown entry without matching modifiers -> clear defensively.
                            _swallowedDirectSlotVks.Remove(vkCode);
                            _swallowedDirectSlotDownTicks.Remove(vkCode);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Debug("GlobalHotkeyService", $"Keyboard hook callback error: {ex.Message}");
        }

        return User32.CallNextHookEx(hookHandle, nCode, wParam, lParam);
    }

    private void PruneExpiredSwallowedKeys(long nowTicks)
    {
        if (_swallowedDirectSlotDownTicks.Count == 0)
        {
            return;
        }

        _expiredSwallowedScratch.Clear();
        foreach (var kvp in _swallowedDirectSlotDownTicks)
        {
            if (nowTicks - kvp.Value >= SwallowedKeyMaxAgeMs)
            {
                _expiredSwallowedScratch.Add(kvp.Key);
            }
        }

        for (int i = 0; i < _expiredSwallowedScratch.Count; i++)
        {
            uint expiredVk = _expiredSwallowedScratch[i];
            _swallowedDirectSlotDownTicks.Remove(expiredVk);
            _swallowedDirectSlotVks.Remove(expiredVk);
        }
    }

    private void DispatchDirectSlot(int slot)
    {
        var dispatcher = _dispatcher ?? Application.Current?.Dispatcher;
        if (dispatcher != null)
        {
            _ = dispatcher.BeginInvoke(() =>
            {
                if (!_disposed)
                {
                    RaiseSafe(DirectSlotRequested, slot);
                }
            });
        }
        else
        {
            RaiseSafe(DirectSlotRequested, slot);
        }
    }

    private bool InstallKeyboardHook(uint modifiers, string rawModifierText)
    {
        var readyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hookCts = new CancellationTokenSource();
        CancellationToken ct = hookCts.Token;
        User32.LowLevelKeyboardProc localDelegate = KeyboardHookCallback;
        GCHandle localHandle = GCHandle.Alloc(localDelegate, GCHandleType.Normal);

        var thread = new Thread(() =>
        {
            nint installedHook = nint.Zero;
            try
            {
                if (ct.IsCancellationRequested)
                {
                    readyTcs.TrySetResult(false);
                    return;
                }

                uint currentTid = Kernel32.GetCurrentThreadId();
                User32.PeekMessageW(out _, nint.Zero, 0, 0, User32.PM_NOREMOVE);

                bool shouldInstall;
                lock (_hookLifecycleLock)
                {
                    shouldInstall = !_disposed && !ct.IsCancellationRequested &&
                                    ReferenceEquals(_hookCts, hookCts);
                    if (shouldInstall)
                    {
                        _hookThreadId = currentTid;
                    }
                }

                if (!shouldInstall)
                {
                    readyTcs.TrySetResult(false);
                    return;
                }

                installedHook = User32.SetWindowsHookExW(User32.WH_KEYBOARD_LL, localDelegate, nint.Zero, 0);
                if (installedHook == nint.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    _logger.Warn("GlobalHotkeyService", $"Failed to install direct slot keyboard hook for '{rawModifierText}+1..9' (Win32 error {err}).");
                    readyTcs.TrySetResult(false);
                    return;
                }

                Volatile.Write(ref _keyboardHookHandle, installedHook);
                readyTcs.TrySetResult(true);

                while (!ct.IsCancellationRequested && User32.GetMessageW(out var msg, nint.Zero, 0, 0) > 0)
                {
                    User32.TranslateMessage(ref msg);
                    User32.DispatchMessageW(ref msg);
                }
            }
            catch (Exception ex)
            {
                _logger.Warn("GlobalHotkeyService", $"Direct slot hook thread error: {ex.Message}");
                readyTcs.TrySetResult(false);
            }
            finally
            {
                _swallowedDirectSlotVks.Clear();
                _swallowedDirectSlotDownTicks.Clear();

                nint toUnhook = Interlocked.Exchange(ref _keyboardHookHandle, nint.Zero);
                if (toUnhook == nint.Zero && installedHook != nint.Zero)
                {
                    toUnhook = installedHook;
                }
                if (toUnhook != nint.Zero)
                {
                    try { User32.UnhookWindowsHookEx(toUnhook); } catch { }
                }

                if (localHandle.IsAllocated)
                {
                    localHandle.Free();
                }

                lock (_hookLifecycleLock)
                {
                    if (ReferenceEquals(_keyboardHookProc, localDelegate)) _keyboardHookProc = null;
                    if (_keyboardHookDelegateHandle == localHandle) _keyboardHookDelegateHandle = default;
                    if (ReferenceEquals(_hookThread, Thread.CurrentThread))
                    {
                        _hookThread = null;
                        _hookThreadId = 0;
                    }
                    if (ReferenceEquals(_hookCts, hookCts)) _hookCts = null;
                }

                try { hookCts.Dispose(); } catch { }
            }
        })
        {
            IsBackground = true,
            Name = "BaBaBooeyStrap.DirectSlotHook"
        };

        lock (_hookLifecycleLock)
        {
            if (_disposed)
            {
                if (localHandle.IsAllocated) localHandle.Free();
                hookCts.Dispose();
                return false;
            }

            _directSlotModifiers = modifiers;
            _directSlotsEnabled = true;
            _keyboardHookProc = localDelegate;
            _keyboardHookDelegateHandle = localHandle;
            _hookCts = hookCts;
            _hookThread = thread;
        }

        thread.Start();

        bool hookOk = false;
        try
        {
            if (readyTcs.Task.Wait(TimeSpan.FromSeconds(2)))
            {
                hookOk = readyTcs.Task.Result;
            }
        }
        catch
        {
            hookOk = false;
        }

        if (!hookOk)
        {
            UninstallKeyboardHook();
        }

        return hookOk;
    }

    private void UninstallKeyboardHook()
    {
        _directSlotsEnabled = false;
        _directSlotModifiers = 0;

        Thread? thread;
        uint tid;
        CancellationTokenSource? cts;

        lock (_hookLifecycleLock)
        {
            thread = _hookThread;
            tid = _hookThreadId;
            cts = _hookCts;
            _hookThread = null;
            _hookThreadId = 0;
            _hookCts = null;
        }

        if (cts != null)
        {
            try { cts.Cancel(); } catch { }
        }

        if (tid != 0)
        {
            try { User32.PostThreadMessageW(tid, User32.WM_QUIT, nint.Zero, nint.Zero); } catch { }
        }

        if (thread != null && !ReferenceEquals(thread, Thread.CurrentThread) && thread.IsAlive)
        {
            try { thread.Join(TimeSpan.FromSeconds(1)); } catch { }
        }
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
                RaiseSafe(QuickSwitcherRequested, EventArgs.Empty);
            }
            else if (id == CycleWindowHotkeyId)
            {
                handled = true;
                RaiseSafe(CycleNextWindowRequested, EventArgs.Empty);
            }
            else if (id == CyclePreviousWindowHotkeyId)
            {
                handled = true;
                RaiseSafe(CyclePreviousWindowRequested, EventArgs.Empty);
            }
        }

        return nint.Zero;
    }

    private void RaiseSafe(EventHandler? handler, EventArgs args)
    {
        if (handler == null) return;
        foreach (EventHandler subscriber in handler.GetInvocationList())
        {
            try
            {
                subscriber(this, args);
            }
            catch (Exception ex)
            {
                _logger.Warn("GlobalHotkeyService", $"Hotkey subscriber threw an exception: {ex.Message}");
            }
        }
    }

    private void RaiseSafe<T>(EventHandler<T>? handler, T args)
    {
        if (handler == null) return;
        foreach (EventHandler<T> subscriber in handler.GetInvocationList())
        {
            try
            {
                subscriber(this, args);
            }
            catch (Exception ex)
            {
                _logger.Warn("GlobalHotkeyService", $"Hotkey subscriber threw an exception: {ex.Message}");
            }
        }
    }

    private void RunOnDispatcher(Action action)
    {
        var dispatcher = _dispatcher ?? Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        try
        {
            dispatcher.Invoke(action);
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException or InvalidOperationException)
        {
            _logger.Debug("GlobalHotkeyService", $"Dispatcher invocation skipped during shutdown: {ex.Message}");
        }
    }

    private void UnregisterSystemHotkeysOnDispatcher()
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

        UninstallKeyboardHook();

        RunOnDispatcher(() =>
        {
            UnregisterSystemHotkeysOnDispatcher();

            if (_hwndSource != null)
            {
                try { _hwndSource.RemoveHook(WndProc); } catch { }
                try { _hwndSource.Dispose(); } catch { }
                _hwndSource = null;
            }
        });

        GC.SuppressFinalize(this);
    }
}
