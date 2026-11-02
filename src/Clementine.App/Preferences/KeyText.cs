using System.Globalization;
using System.Windows.Input;
using Clementine.Core;

namespace Clementine.App.Preferences;

/// <summary>Formats and parses modifier combinations and hotkeys ("Ctrl+Shift+Space") the way the wheel's parser reads them.</summary>
public static class KeyText
{
    /// <summary>"Ctrl + Shift" style text for display.</summary>
    public static string Describe(TriggerModifiers modifiers)
    {
        var parts = Parts(modifiers).ToList();
        return parts.Count == 0 ? "None" : string.Join(" + ", parts);
    }

    public static IEnumerable<string> Parts(TriggerModifiers modifiers)
    {
        if (modifiers.HasFlag(TriggerModifiers.Control))
            yield return "Ctrl";
        if (modifiers.HasFlag(TriggerModifiers.Alt))
            yield return "Alt";
        if (modifiers.HasFlag(TriggerModifiers.Shift))
            yield return "Shift";
        if (modifiers.HasFlag(TriggerModifiers.Windows))
            yield return "Win";
    }

    /// <summary>
    /// Hotkey text for a key press in the recorder ("Ctrl+Shift+Space"), or null when the key is only a modifier
    /// or the combination has no modifier at all (a bare key would hijack normal typing).
    /// </summary>
    public static string? FormatHotkey(ModifierKeys modifiers, Key key)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.System or Key.None or Key.ImeProcessed or Key.DeadCharProcessed)
            return null;
        if (modifiers == ModifierKeys.None || modifiers == ModifierKeys.Shift)
            return null;
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control))
            parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt))
            parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift))
            parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows))
            parts.Add("Win");
        parts.Add(KeyName(key));
        return string.Join("+", parts);
    }

    /// <summary>Parses "Ctrl+Shift+Space" back into WPF values (round-trips <see cref="FormatHotkey"/>).</summary>
    public static bool TryParseHotkey(string? text, out ModifierKeys modifiers, out Key key)
    {
        modifiers = ModifierKeys.None;
        key = Key.None;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    modifiers |= ModifierKeys.Control;
                    break;
                case "alt":
                    modifiers |= ModifierKeys.Alt;
                    break;
                case "shift":
                    modifiers |= ModifierKeys.Shift;
                    break;
                case "win" or "windows":
                    modifiers |= ModifierKeys.Windows;
                    break;
                default:
                    if (key != Key.None || !TryParseKey(raw, out key))
                        return false;
                    break;
            }
        }
        return key != Key.None && modifiers != ModifierKeys.None;
    }

    static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(CultureInfo.InvariantCulture),
        >= Key.NumPad0 and <= Key.NumPad9 => "Num" + ((int)(key - Key.NumPad0)).ToString(CultureInfo.InvariantCulture),
        Key.Return => "Enter",
        Key.Escape => "Esc",
        Key.Prior => "PageUp",
        Key.Next => "PageDown",
        Key.OemPlus => "Plus",
        Key.OemMinus => "Minus",
        Key.OemComma => "Comma",
        Key.OemPeriod => "Period",
        _ => key.ToString(),
    };

    static bool TryParseKey(string text, out Key key)
    {
        key = Key.None;
        if (text.Length == 1 && char.IsDigit(text[0]))
        {
            key = Key.D0 + (text[0] - '0');
            return true;
        }
        if (text.StartsWith("Num", StringComparison.OrdinalIgnoreCase) && text.Length == 4 && char.IsDigit(text[3]))
        {
            key = Key.NumPad0 + (text[3] - '0');
            return true;
        }
        var mapped = text.ToLowerInvariant() switch
        {
            "enter" => "Return",
            "esc" => "Escape",
            "pageup" => "Prior",
            "pagedown" => "Next",
            "plus" => "OemPlus",
            "minus" => "OemMinus",
            "comma" => "OemComma",
            "period" => "OemPeriod",
            _ => text,
        };
        return Enum.TryParse(mapped, ignoreCase: true, out key) && key != Key.None;
    }
}
