using System.Globalization;

namespace Clementine.Wheel;

/// <summary>Modifier flags in the layout <c>RegisterHotKey</c> expects.</summary>
[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x1,
    Control = 0x2,
    Shift = 0x4,
    Windows = 0x8,
}

/// <summary>
/// A global hotkey such as "Ctrl+Shift+Space". Parsing is case-insensitive and tolerant of spaces; a gesture needs
/// Ctrl, Alt or Win (a plain or Shift-only key would steal normal typing) unless the key is F1–F24.
/// </summary>
public readonly record struct HotkeyGesture(HotkeyModifiers Modifiers, int VirtualKey)
{
    static readonly Dictionary<string, int> KeysByName = BuildKeyNames();
    static readonly Dictionary<int, string> NamesByKey = BuildDisplayNames();

    public bool IsFunctionKey => VirtualKey is >= 0x70 and <= 0x87;

    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var tokens = Tokenize(text.Trim());
        if (tokens is null || tokens.Count == 0)
            return false;

        var modifiers = HotkeyModifiers.None;
        int? key = null;
        foreach (var raw in tokens)
        {
            var token = raw.ToLowerInvariant();
            switch (token)
            {
                case "ctrl" or "control" or "ctl":
                    modifiers |= HotkeyModifiers.Control;
                    continue;
                case "alt" or "menu" or "option":
                    modifiers |= HotkeyModifiers.Alt;
                    continue;
                case "shift":
                    modifiers |= HotkeyModifiers.Shift;
                    continue;
                case "win" or "windows" or "meta" or "super" or "cmd":
                    modifiers |= HotkeyModifiers.Windows;
                    continue;
            }
            if (key is not null || !KeysByName.TryGetValue(token, out var vk))
                return false;
            key = vk;
        }

        if (key is null)
            return false;
        var candidate = new HotkeyGesture(modifiers, key.Value);
        var hasStrongModifier = (modifiers & (HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Windows)) != 0;
        if (!hasStrongModifier && !candidate.IsFunctionKey)
            return false;
        gesture = candidate;
        return true;
    }

    /// <summary>Normalised text, e.g. "Ctrl+Shift+Space".</summary>
    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Control))
            parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
            parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
            parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Windows))
            parts.Add("Win");
        parts.Add(NamesByKey.TryGetValue(VirtualKey, out var name) ? name : $"0x{VirtualKey:X2}");
        return string.Join("+", parts);
    }

    // "Ctrl++" means Ctrl and the plus key: a '+' directly after a separator is the key itself.
    static List<string>? Tokenize(string text)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '+' && current.ToString().Trim().Length > 0)
            {
                tokens.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }
            if (ch == '+' && current.ToString().Trim().Length == 0)
            {
                // Leading or doubled '+': it is the key name.
                if (i == text.Length - 1 || text[(i + 1)..].Trim().Length == 0 || text[i + 1] == '+')
                {
                    current.Append('+');
                    continue;
                }
                return null;
            }
            current.Append(ch);
        }
        var last = current.ToString().Trim();
        if (last.Length == 0)
            return null;
        tokens.Add(last);
        return tokens;
    }

    static Dictionary<string, int> BuildKeyNames()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var c = 'A'; c <= 'Z'; c++)
            map[c.ToString()] = c;
        for (var d = 0; d <= 9; d++)
        {
            map[d.ToString(CultureInfo.InvariantCulture)] = 0x30 + d;
            map[$"numpad{d}"] = 0x60 + d;
            map[$"num{d}"] = 0x60 + d;
        }
        for (var f = 1; f <= 24; f++)
            map[$"f{f}"] = 0x6F + f;

        void Add(int vk, params string[] names)
        {
            foreach (var name in names)
                map[name] = vk;
        }

        Add(0x20, "space", "spacebar");
        Add(0x0D, "enter", "return");
        Add(0x09, "tab");
        Add(0x1B, "esc", "escape");
        Add(0x08, "backspace", "back");
        Add(0x2D, "insert", "ins");
        Add(0x2E, "delete", "del");
        Add(0x24, "home");
        Add(0x23, "end");
        Add(0x21, "pageup", "pgup", "prior");
        Add(0x22, "pagedown", "pgdn", "next");
        Add(0x25, "left");
        Add(0x26, "up");
        Add(0x27, "right");
        Add(0x28, "down");
        Add(0x2C, "printscreen", "prtsc", "print");
        Add(0x13, "pause", "break");
        Add(0x6A, "multiply", "*");
        Add(0x6B, "add");
        Add(0x6D, "subtract");
        Add(0x6E, "decimal");
        Add(0x6F, "divide");
        Add(0xBA, ";", "semicolon");
        Add(0xBB, "=", "+", "plus", "equals");
        Add(0xBC, ",", "comma");
        Add(0xBD, "-", "minus");
        Add(0xBE, ".", "period");
        Add(0xBF, "/", "slash");
        Add(0xC0, "`", "backtick", "grave");
        Add(0xDB, "[", "openbracket");
        Add(0xDC, "\\", "backslash");
        Add(0xDD, "]", "closebracket");
        Add(0xDE, "'", "quote");
        return map;
    }

    static Dictionary<int, string> BuildDisplayNames()
    {
        var names = new Dictionary<int, string>();
        for (var c = 'A'; c <= 'Z'; c++)
            names[c] = c.ToString();
        for (var d = 0; d <= 9; d++)
        {
            names[0x30 + d] = d.ToString(CultureInfo.InvariantCulture);
            names[0x60 + d] = $"NumPad{d}";
        }
        for (var f = 1; f <= 24; f++)
            names[0x6F + f] = $"F{f}";
        names[0x20] = "Space";
        names[0x0D] = "Enter";
        names[0x09] = "Tab";
        names[0x1B] = "Esc";
        names[0x08] = "Backspace";
        names[0x2D] = "Insert";
        names[0x2E] = "Delete";
        names[0x24] = "Home";
        names[0x23] = "End";
        names[0x21] = "PageUp";
        names[0x22] = "PageDown";
        names[0x25] = "Left";
        names[0x26] = "Up";
        names[0x27] = "Right";
        names[0x28] = "Down";
        names[0x2C] = "PrintScreen";
        names[0x13] = "Pause";
        names[0x6A] = "Multiply";
        names[0x6B] = "Add";
        names[0x6D] = "Subtract";
        names[0x6E] = "Decimal";
        names[0x6F] = "Divide";
        names[0xBA] = ";";
        names[0xBB] = "Plus";
        names[0xBC] = ",";
        names[0xBD] = "-";
        names[0xBE] = ".";
        names[0xBF] = "/";
        names[0xC0] = "`";
        names[0xDB] = "[";
        names[0xDC] = "\\";
        names[0xDD] = "]";
        names[0xDE] = "'";
        return names;
    }
}
