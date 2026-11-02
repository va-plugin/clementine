using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using static Clementine.Engines.Documents.OpenXml.Ooxml;

namespace Clementine.Engines.Documents.OpenXml.Word;

/// <summary>One list level: how its number looks and how the paragraph is indented.</summary>
internal sealed class WordListLevel
{
    public int Start { get; init; } = 1;

    public string Format { get; init; } = "decimal";

    public string Text { get; init; } = "%1.";

    public string Justification { get; init; } = "left";

    /// <summary>What follows the number: "tab", "space" or "nothing".</summary>
    public string Suffix { get; init; } = "tab";

    public bool IsLegal { get; init; }

    /// <summary>lvlRestart: null = restart after any higher level, 0 = never.</summary>
    public int? Restart { get; init; }

    public OpenXmlElement? ParagraphProperties { get; init; }

    public OpenXmlElement? RunProperties { get; init; }

    public string? ParagraphStyle { get; init; }

    public bool IsBullet => Format == "bullet";
}

/// <summary>A computed list label for one paragraph.</summary>
internal sealed record WordListLabel(string Text, WordListLevel Level, int AbstractId, int Depth);

/// <summary>
/// Word list numbering: abstract definitions, instances with overrides, style links, and running counters per abstract
/// list (a new instance without overrides continues numbering; "restart numbering" instances carry a startOverride).
/// </summary>
internal sealed class WordNumbering
{
    const int Unset = int.MinValue;

    /// <summary>Word's own limit for a list's start value (w:start, w:startOverride).</summary>
    const int MaxStart = 32_767;

    readonly Dictionary<int, OpenXmlElement> _abstracts = [];
    readonly Dictionary<int, OpenXmlElement> _instances = [];
    readonly Dictionary<int, int[]> _counters = [];
    readonly HashSet<int> _startedInstances = [];
    readonly Dictionary<(int, int), WordListLevel?> _levels = [];
    readonly WordStyles _styles;

    public WordNumbering(MainDocumentPart main, WordStyles styles)
    {
        _styles = styles;
        OpenXmlElement? root = null;
        try
        {
            root = main.NumberingDefinitionsPart?.RootElement;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }
        foreach (var abstractNum in root.Children("abstractNum"))
        {
            if (Integer(abstractNum.Attr("abstractNumId")) is { } id)
                _abstracts.TryAdd(id, abstractNum);
        }
        foreach (var num in root.Children("num"))
        {
            if (Integer(num.Attr("numId")) is { } id)
                _instances.TryAdd(id, num);
        }
    }

    public bool IsEmpty => _instances.Count == 0;

    /// <summary>The level definition for (numId, ilvl), honouring instance overrides and numbering-style links.</summary>
    public WordListLevel? Level(int numId, int level)
    {
        if (_levels.TryGetValue((numId, level), out var cached))
            return cached;
        WordListLevel? result = null;
        if (_instances.TryGetValue(numId, out var instance))
        {
            var overrideLevel = instance.Children("lvlOverride").FirstOrDefault(o => Integer(o.Attr("ilvl")) == level)?.Child("lvl");
            var definition = overrideLevel ?? AbstractFor(instance)?.Children("lvl").FirstOrDefault(l => (Integer(l.Attr("ilvl")) ?? 0) == level);
            if (definition is not null)
                result = Parse(definition);
        }
        _levels[(numId, level)] = result;
        return result;
    }

    /// <summary>The level whose pStyle names <paramref name="styleId"/> (style-linked lists such as numbered headings).</summary>
    public int LevelForStyle(int numId, string? styleId)
    {
        if (styleId is null || !_instances.TryGetValue(numId, out var instance))
            return 0;
        var level = AbstractFor(instance)?.Children("lvl").FirstOrDefault(l => l.Child("pStyle").Attr("val") == styleId);
        return Integer(level?.Attr("ilvl")) ?? 0;
    }

    /// <summary>Advances the counters for a numbered paragraph and returns its label (null when the paragraph isn't numbered).</summary>
    public WordListLabel? Next(int numId, int level)
    {
        if (numId <= 0 || !_instances.TryGetValue(numId, out var instance) || Level(numId, level) is not { } definition)
            return null;
        var abstractNum = AbstractFor(instance);
        var abstractId = Integer(abstractNum?.Attr("abstractNumId")) ?? -numId;
        if (!_counters.TryGetValue(abstractId, out var counters))
        {
            counters = Enumerable.Repeat(Unset, 9).ToArray();
            _counters[abstractId] = counters;
        }
        if (_startedInstances.Add(numId))
        {
            foreach (var levelOverride in instance.Children("lvlOverride"))
            {
                if (Integer(levelOverride.Attr("ilvl")) is { } overridden and >= 0 and <= 8 && Integer(levelOverride.Child("startOverride").Attr("val")) is { } start)
                {
                    counters[overridden] = Math.Clamp(start, 0, MaxStart) - 1;
                    for (var deeper = overridden + 1; deeper < 9; deeper++)
                        counters[deeper] = Unset;
                }
            }
        }

        counters[level] = (counters[level] == Unset ? definition.Start - 1 : counters[level]) + 1;
        for (var deeper = level + 1; deeper < 9; deeper++)
        {
            var restart = Level(numId, deeper)?.Restart;
            if (restart is null || (restart > 0 && level + 1 <= restart))
                counters[deeper] = Unset;
        }

        var text = new StringBuilder();
        var pattern = definition.Text;
        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == '%' && i + 1 < pattern.Length && pattern[i + 1] is >= '1' and <= '9')
            {
                var referenced = pattern[i + 1] - '1';
                var referencedLevel = referenced == level ? definition : Level(numId, referenced);
                var value = counters[referenced] == Unset ? referencedLevel?.Start ?? 1 : counters[referenced];
                var format = definition.IsLegal && referenced != level ? "decimal" : referencedLevel?.Format ?? "decimal";
                if (definition.IsLegal && referenced == level && format is not ("bullet" or "none"))
                    format = "decimal";
                text.Append(FormatNumber(value, format));
                i++;
            }
            else
            {
                text.Append(pattern[i]);
            }
        }
        var label = definition.Format == "none" ? "" : text.ToString();
        if (definition.IsBullet)
            label = BulletText(definition.Text, FontOf(definition.RunProperties));
        return new WordListLabel(label, definition, abstractId, level);
    }

    OpenXmlElement? AbstractFor(OpenXmlElement instance)
    {
        var id = Integer(instance.Child("abstractNumId").Attr("val"));
        if (id is null || !_abstracts.TryGetValue(id.Value, out var abstractNum))
            return null;
        // A list that only points at a numbering style ("numStyleLink"): follow the style to the real definition.
        for (var hops = 0; hops < 4 && abstractNum.Child("numStyleLink").Attr("val") is { } link && !abstractNum.Children("lvl").Any(); hops++)
        {
            var linkedNum = Integer(_styles.Get(link)?.ParagraphProperties.Child("numPr").Child("numId").Attr("val"));
            if (linkedNum is null || !_instances.TryGetValue(linkedNum.Value, out var linkedInstance)
                || Integer(linkedInstance.Child("abstractNumId").Attr("val")) is not { } linkedAbstract
                || !_abstracts.TryGetValue(linkedAbstract, out var resolved))
                break;
            abstractNum = resolved;
        }
        return abstractNum;
    }

    static WordListLevel Parse(OpenXmlElement level)
    {
        var format = level.Child("numFmt").Attr("val");
        if (format is null && level.Child("AlternateContent") is { } alternate)
            format = alternate.Child("Fallback").Child("numFmt").Attr("val");
        return new WordListLevel
        {
            Start = Math.Clamp(Integer(level.Child("start").Attr("val")) ?? 1, 0, MaxStart),
            Format = format ?? "decimal",
            Text = level.Child("lvlText").Attr("val") ?? "",
            Justification = level.Child("lvlJc").Attr("val") ?? "left",
            Suffix = level.Child("suff").Attr("val") ?? "tab",
            IsLegal = IsOn(level.Child("isLgl")),
            Restart = Integer(level.Child("lvlRestart").Attr("val")),
            ParagraphProperties = level.Child("pPr"),
            RunProperties = level.Child("rPr"),
            ParagraphStyle = level.Child("pStyle").Attr("val"),
        };
    }

    static string? FontOf(OpenXmlElement? runProperties) => runProperties.Child("rFonts") is { } fonts ? fonts.Attr("ascii") ?? fonts.Attr("hAnsi") : null;

    /// <summary>True for symbol fonts whose private-use characters need mapping to Unicode.</summary>
    public static bool IsSymbolFont(string? font) =>
        font is not null && (font.StartsWith("Wingdings", StringComparison.OrdinalIgnoreCase) || font.Equals("Symbol", StringComparison.OrdinalIgnoreCase)
            || font.StartsWith("Webdings", StringComparison.OrdinalIgnoreCase));

    /// <summary>A bullet's character, with Symbol/Wingdings private-use characters mapped to their Unicode look-alikes.</summary>
    public static string BulletText(string text, string? font)
    {
        if (text.Length == 0)
            return "";
        var builder = new StringBuilder();
        foreach (var c in text)
            builder.Append(SymbolCharacter(c, font));
        return builder.ToString();
    }

    /// <summary>Maps a character set in Symbol / Wingdings (often as U+F0xx) to a Unicode character that looks the same.</summary>
    public static string SymbolCharacter(char c, string? font)
    {
        var code = c >= 0xF000 && c <= 0xF0FF ? c - 0xF000 : c;
        var wingdings = font?.StartsWith("Wingdings", StringComparison.OrdinalIgnoreCase) == true;
        var symbol = font?.Equals("Symbol", StringComparison.OrdinalIgnoreCase) == true;
        if (!wingdings && !symbol && c < 0xF000)
            return c.ToString();
        if (wingdings)
        {
            return code switch
            {
                0xA7 => "▪",
                0xA8 => "◻",
                0x6C => "●",
                0x6E => "■",
                0x6F => "□",
                0x71 => "❑",
                0x75 => "◆",
                0x76 => "❖",
                0x77 => "⬥",
                0xD8 => "➢",
                0xE0 => "→",
                0xE8 => "➔",
                0xF0 => "⇨",
                0xFC => "✓",
                0xFB => "✗",
                0xFE => "☑",
                0x9F => "•",
                0x81 => "①",
                0x4A => "☺",
                _ => "•",
            };
        }
        return code switch
        {
            0xB7 => "•",
            0x2D => "−",
            0xA8 => "♦",
            0xAA => "♠",
            0xA7 => "♣",
            0xA9 => "♥",
            0xD6 => "√",
            0xAE => "→",
            0xB0 => "°",
            0xB4 => "×",
            0xB1 => "±",
            0xA3 => "≤",
            0xB3 => "≥",
            0xB9 => "≠",
            0xA5 => "∞",
            >= 0x41 and <= 0x5A or >= 0x61 and <= 0x7A => Greek((char)code),
            _ => c < 0xF000 ? c.ToString() : "•",
        };
    }

    static string Greek(char latin)
    {
        const string from = "ABGDEZHQIKLMNXOPRSTUFCYWabgdezhqiklmnxoprstufcyw";
        const string to = "ΑΒΓΔΕΖΗΘΙΚΛΜΝΞΟΠΡΣΤΥΦΧΨΩαβγδεζηθικλμνξοπρστυφχψω";
        var index = from.IndexOf(latin, StringComparison.Ordinal);
        return index >= 0 ? to[index].ToString() : latin.ToString();
    }

    /// <summary>A counter value in one of Word's number formats.</summary>
    public static string FormatNumber(int value, string format)
    {
        switch (format)
        {
            case "none":
            case "bullet":
                return "";
            case "decimalZero":
                return value is >= 0 and < 10 ? "0" + value.ToString(CultureInfo.InvariantCulture) : value.ToString(CultureInfo.InvariantCulture);
            case "upperRoman":
                return Roman(value);
            case "lowerRoman":
                return Roman(value).ToLowerInvariant();
            case "upperLetter":
                return Letters(value);
            case "lowerLetter":
                return Letters(value).ToLowerInvariant();
            case "ordinal":
                return value.ToString(CultureInfo.InvariantCulture) + OrdinalSuffix(value);
            case "cardinalText":
                return Capitalize(Words(value));
            case "ordinalText":
                return Capitalize(OrdinalWords(value));
            case "decimalEnclosedCircle":
                return value is >= 1 and <= 20 ? ((char)(0x2460 + value - 1)).ToString() : value.ToString(CultureInfo.InvariantCulture);
            case "decimalEnclosedParen":
                return value is >= 1 and <= 20 ? ((char)(0x2474 + value - 1)).ToString() : $"({value})";
            case "decimalFullWidth":
            case "decimalFullWidth2":
                return string.Concat(value.ToString(CultureInfo.InvariantCulture).Select(d => (char)(d - '0' + 0xFF10)));
            default:
                return value.ToString(CultureInfo.InvariantCulture);
        }
    }

    static string Roman(int n)
    {
        if (n <= 0 || n >= 4000)
            return n.ToString(CultureInfo.InvariantCulture);
        (int Value, string Symbol)[] table =
            [(1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"), (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I")];
        var result = new StringBuilder();
        foreach (var (value, symbol) in table)
        {
            while (n >= value)
            {
                result.Append(symbol);
                n -= value;
            }
        }
        return result.ToString();
    }

    /// <summary>
    /// Word repeats the letter after Z: A…Z, AA, BB… ZZ, AAA. The repeat is capped (no real list gets near item 1,300)
    /// so a crafted list number can't turn one label into megabytes.
    /// </summary>
    static string Letters(int n)
    {
        if (n <= 0)
            return n.ToString(CultureInfo.InvariantCulture);
        var letter = (char)('A' + (n - 1) % 26);
        return new string(letter, Math.Min((n - 1) / 26 + 1, MaxLetterRepeat));
    }

    const int MaxLetterRepeat = 50;

    static string OrdinalSuffix(int n) => (n % 100) is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };

    static readonly string[] Ones =
        ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve", "thirteen", "fourteen",
         "fifteen", "sixteen", "seventeen", "eighteen", "nineteen"];

    static readonly string[] Tens = ["", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"];

    static string Words(int n)
    {
        if (n < 0 || n >= 1000)
            return n.ToString(CultureInfo.InvariantCulture);
        if (n < 20)
            return Ones[n];
        if (n < 100)
            return Tens[n / 10] + (n % 10 == 0 ? "" : "-" + Ones[n % 10]);
        return Ones[n / 100] + " hundred" + (n % 100 == 0 ? "" : " " + Words(n % 100));
    }

    static string OrdinalWords(int n)
    {
        var words = Words(n);
        if (words.EndsWith("one", StringComparison.Ordinal))
            return words[..^3] + "first";
        if (words.EndsWith("two", StringComparison.Ordinal))
            return words[..^3] + "second";
        if (words.EndsWith("three", StringComparison.Ordinal))
            return words[..^5] + "third";
        if (words.EndsWith("five", StringComparison.Ordinal))
            return words[..^4] + "fifth";
        if (words.EndsWith("eight", StringComparison.Ordinal))
            return words + "h";
        if (words.EndsWith("nine", StringComparison.Ordinal))
            return words[..^1] + "th";
        if (words.EndsWith("twelve", StringComparison.Ordinal))
            return words[..^2] + "fth";
        if (words.EndsWith('y'))
            return words[..^1] + "ieth";
        return words + "th";
    }

    static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
