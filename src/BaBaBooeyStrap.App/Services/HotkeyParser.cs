using System.Windows.Input;
using BaBaBooeyStrap.Core.Interop;

namespace BaBaBooeyStrap.App.Services;

public sealed record ParsedHotkey(uint Modifiers, uint VirtualKey, string NormalizedText);

public static class HotkeyParser
{
    private static readonly Dictionary<string, uint> NamedVirtualKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = 0x20,
        ["Enter"] = 0x0D,
        ["Return"] = 0x0D,
        ["Tab"] = 0x09,
        ["Back"] = 0x08,
        ["Backspace"] = 0x08,
        ["Insert"] = 0x2D,
        ["Ins"] = 0x2D,
        ["Delete"] = 0x2E,
        ["Del"] = 0x2E,
        ["Home"] = 0x24,
        ["End"] = 0x23,
        ["PageUp"] = 0x21,
        ["PgUp"] = 0x21,
        ["Prior"] = 0x21,
        ["PageDown"] = 0x22,
        ["PgDn"] = 0x22,
        ["Next"] = 0x22,
        ["Left"] = 0x25,
        ["Up"] = 0x26,
        ["Right"] = 0x27,
        ["Down"] = 0x28,
        ["~"] = 0xC0,
        ["`"] = 0xC0,
        ["OemTilde"] = 0xC0,
        ["Oem3"] = 0xC0,
        ["-"] = 0xBD,
        ["OemMinus"] = 0xBD,
        ["="] = 0xBB,
        ["OemPlus"] = 0xBB,
        ["["] = 0xDB,
        ["OemOpenBrackets"] = 0xDB,
        ["]"] = 0xDD,
        ["OemCloseBrackets"] = 0xDD,
        ["\\"] = 0xDC,
        ["OemPipe"] = 0xDC,
        [";"] = 0xBA,
        ["OemSemicolon"] = 0xBA,
        ["'"] = 0xDE,
        ["OemQuotes"] = 0xDE,
        [","] = 0xBC,
        ["OemComma"] = 0xBC,
        ["."] = 0xBE,
        ["OemPeriod"] = 0xBE,
        ["/"] = 0xBF,
        ["OemQuestion"] = 0xBF,
        ["+"] = 0xBB,
        ["Escape"] = 0x1B,
        ["Esc"] = 0x1B,
        ["Add"] = 0x6B,
        ["Numpad+"] = 0x6B,
        ["Subtract"] = 0x6D,
        ["Numpad-"] = 0x6D,
        ["Multiply"] = 0x6A,
        ["Numpad*"] = 0x6A,
        ["Divide"] = 0x6F,
        ["Numpad/"] = 0x6F,
        ["Decimal"] = 0x6E,
        ["Numpad."] = 0x6E,
        ["PrintScreen"] = 0x2C,
        ["Snapshot"] = 0x2C,
        ["Pause"] = 0x13,
        ["CapsLock"] = 0x14,
        ["Capital"] = 0x14,
        ["NumLock"] = 0x90,
        ["Scroll"] = 0x91,
        ["ScrollLock"] = 0x91,
        ["Spacebar"] = 0x20
    };

    private static readonly Dictionary<uint, string> VirtualKeyDisplayNames = new()
    {
        [0x20] = "Space",
        [0x0D] = "Enter",
        [0x09] = "Tab",
        [0x08] = "Backspace",
        [0x2D] = "Insert",
        [0x2E] = "Delete",
        [0x24] = "Home",
        [0x23] = "End",
        [0x21] = "PageUp",
        [0x22] = "PageDown",
        [0x25] = "Left",
        [0x26] = "Up",
        [0x27] = "Right",
        [0x28] = "Down",
        [0xC0] = "~",
        [0xBD] = "-",
        [0xBB] = "=",
        [0xDB] = "[",
        [0xDD] = "]",
        [0xDC] = "\\",
        [0xBA] = ";",
        [0xDE] = "'",
        [0xBC] = ",",
        [0xBE] = ".",
        [0xBF] = "/"
    };

    public static bool TryParse(string? text, out ParsedHotkey result)
    {
        result = default!;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (!TrySplitHotkeyTokens(text, out List<string> parts))
        {
            return false;
        }

        uint modifiers = 0;
        uint? vk = null;

        foreach (string token in parts)
        {
            if (TryParseModifierToken(token, out uint mod))
            {
                modifiers |= mod;
                continue;
            }

            if (vk.HasValue || !TryParseKeyToken(token, out uint parsedVk))
            {
                return false;
            }

            vk = parsedVk;
        }

        if (modifiers == 0 || !vk.HasValue)
        {
            return false;
        }

        string normalized = FormatNormalized(modifiers, vk.Value);
        result = new ParsedHotkey(modifiers | User32.MOD_NOREPEAT, vk.Value, normalized);
        return true;
    }

    private static bool TrySplitHotkeyTokens(string text, out List<string> tokens)
    {
        tokens = new List<string>();
        string trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        bool trailingPlusKey = false;
        if (trimmed.EndsWith('+'))
        {
            string beforeLast = trimmed[..^1].TrimEnd();
            if (!beforeLast.EndsWith('+'))
            {
                return false;
            }

            trimmed = beforeLast[..^1];
            trailingPlusKey = true;
        }

        foreach (string rawPart in trimmed.Split('+', StringSplitOptions.TrimEntries))
        {
            if (string.IsNullOrEmpty(rawPart))
            {
                return false;
            }

            tokens.Add(rawPart);
        }

        if (trailingPlusKey)
        {
            tokens.Add("+");
        }

        return tokens.Count >= 2;
    }

    public static bool TryParseModifiersOnly(string? text, out uint modifiers, out string normalizedText)
    {
        modifiers = 0;
        normalizedText = string.Empty;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string[] parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        uint rawMods = 0;
        foreach (string token in parts)
        {
            if (!TryParseModifierToken(token, out uint mod))
            {
                return false;
            }
            rawMods |= mod;
        }

        if (rawMods == 0)
        {
            return false;
        }

        modifiers = rawMods | User32.MOD_NOREPEAT;
        normalizedText = FormatModifiers(rawMods);
        return true;
    }

    public static string? FormatKeyGesture(ModifierKeys modifiers, Key key)
    {
        Key actualKey = key == Key.System ? Key.None : key;
        if (actualKey == Key.None ||
            actualKey is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return null;
        }

        uint rawMods = 0;
        if (modifiers.HasFlag(ModifierKeys.Control)) rawMods |= User32.MOD_CONTROL;
        if (modifiers.HasFlag(ModifierKeys.Alt)) rawMods |= User32.MOD_ALT;
        if (modifiers.HasFlag(ModifierKeys.Shift)) rawMods |= User32.MOD_SHIFT;
        if (modifiers.HasFlag(ModifierKeys.Windows)) rawMods |= User32.MOD_WIN;

        if (rawMods == 0)
        {
            return null;
        }

        int vk = KeyInterop.VirtualKeyFromKey(actualKey);
        if (vk <= 0)
        {
            return null;
        }

        return FormatNormalized(rawMods, (uint)vk);
    }

    public static string FormatModifiersOnly(ModifierKeys modifiers)
    {
        uint rawMods = 0;
        if (modifiers.HasFlag(ModifierKeys.Control)) rawMods |= User32.MOD_CONTROL;
        if (modifiers.HasFlag(ModifierKeys.Alt)) rawMods |= User32.MOD_ALT;
        if (modifiers.HasFlag(ModifierKeys.Shift)) rawMods |= User32.MOD_SHIFT;
        if (modifiers.HasFlag(ModifierKeys.Windows)) rawMods |= User32.MOD_WIN;

        return rawMods == 0 ? string.Empty : FormatModifiers(rawMods);
    }

    private static bool TryParseModifierToken(string token, out uint modifier)
    {
        switch (token.Trim().ToUpperInvariant())
        {
            case "CTRL":
            case "CONTROL":
                modifier = User32.MOD_CONTROL;
                return true;
            case "ALT":
            case "MENU":
                modifier = User32.MOD_ALT;
                return true;
            case "SHIFT":
                modifier = User32.MOD_SHIFT;
                return true;
            case "WIN":
            case "WINDOWS":
                modifier = User32.MOD_WIN;
                return true;
            default:
                modifier = 0;
                return false;
        }
    }

    private static bool TryParseKeyToken(string token, out uint virtualKey)
    {
        if (NamedVirtualKeys.TryGetValue(token, out virtualKey))
        {
            return true;
        }

        if (token.Length == 1)
        {
            char c = char.ToUpperInvariant(token[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                virtualKey = c;
                return true;
            }
        }

        if (token.Length == 2 && (token[0] == 'D' || token[0] == 'd') && char.IsDigit(token[1]))
        {
            virtualKey = (uint)token[1];
            return true;
        }

        if ((token.StartsWith("F", StringComparison.OrdinalIgnoreCase) &&
             int.TryParse(token.AsSpan(1), out int fNum) &&
             fNum is >= 1 and <= 24))
        {
            virtualKey = (uint)(0x70 + fNum - 1);
            return true;
        }

        if (token.StartsWith("Numpad", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(token.AsSpan(6), out int numNum) &&
            numNum is >= 0 and <= 9)
        {
            virtualKey = (uint)(0x60 + numNum);
            return true;
        }

        if (!int.TryParse(token, out _) &&
            Enum.TryParse<Key>(token, ignoreCase: true, out Key wpfKey) &&
            wpfKey is not (Key.None or Key.DeadCharProcessed or Key.ImeProcessed
                or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System))
        {
            int mappedVk = KeyInterop.VirtualKeyFromKey(wpfKey);
            if (mappedVk > 0)
            {
                virtualKey = (uint)mappedVk;
                return true;
            }
        }

        virtualKey = 0;
        return false;
    }

    private static string FormatModifiers(uint rawMods)
    {
        var parts = new List<string>(4);
        if ((rawMods & User32.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((rawMods & User32.MOD_ALT) != 0) parts.Add("Alt");
        if ((rawMods & User32.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((rawMods & User32.MOD_WIN) != 0) parts.Add("Win");
        return string.Join("+", parts);
    }

    private static string FormatNormalized(uint rawMods, uint vk)
    {
        string modPrefix = FormatModifiers(rawMods);
        string keyName;
        if (VirtualKeyDisplayNames.TryGetValue(vk, out string? mapped))
        {
            keyName = mapped;
        }
        else if (vk is >= 0x41 and <= 0x5A or >= 0x30 and <= 0x39)
        {
            keyName = ((char)vk).ToString();
        }
        else if (vk is >= 0x70 and <= 0x87)
        {
            keyName = $"F{vk - 0x70 + 1}";
        }
        else if (vk is >= 0x60 and <= 0x69)
        {
            keyName = $"Numpad{vk - 0x60}";
        }
        else
        {
            keyName = KeyInterop.KeyFromVirtualKey((int)vk).ToString();
        }

        return string.IsNullOrEmpty(modPrefix) ? keyName : $"{modPrefix}+{keyName}";
    }
}
