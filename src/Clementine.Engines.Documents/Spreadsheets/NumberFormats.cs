using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Clementine.Engines.Documents.Spreadsheets;

/// <summary>A cell value as Excel displays it, and the colour its format asks for ([Red]…), if any.</summary>
internal readonly record struct DisplayValue(string Text, string? Color);

/// <summary>
/// Renders numbers, dates and text through Excel number format codes ("#,##0.00", "0%", "d mmm yyyy", "[h]:mm",
/// "$#,##0_);[Red]($#,##0)", "# ?/?", "0.00E+00", "@"…) the way Excel shows them, in a given culture.
/// </summary>
internal static class NumberFormats
{
    static readonly ConcurrentDictionary<string, Section[]> Cache = new(StringComparer.Ordinal);

    /// <summary>The format code of a built-in number format id, or null for ids that aren't built in.</summary>
    public static string? BuiltIn(int id, CultureInfo culture)
    {
        var currency = culture.NumberFormat.CurrencySymbol;
        return id switch
        {
            0 => "General",
            1 => "0",
            2 => "0.00",
            3 => "#,##0",
            4 => "#,##0.00",
            5 => $"\"{currency}\"#,##0_);(\"{currency}\"#,##0)",
            6 => $"\"{currency}\"#,##0_);[Red](\"{currency}\"#,##0)",
            7 => $"\"{currency}\"#,##0.00_);(\"{currency}\"#,##0.00)",
            8 => $"\"{currency}\"#,##0.00_);[Red](\"{currency}\"#,##0.00)",
            9 => "0%",
            10 => "0.00%",
            11 => "0.00E+00",
            12 => "# ?/?",
            13 => "# ??/??",
            14 or (>= 27 and <= 31) or (>= 50 and <= 58) or 36 => ShortDatePattern(culture),
            15 => "d-mmm-yy",
            16 => "d-mmm",
            17 => "mmm-yy",
            18 => "h:mm AM/PM",
            19 => "h:mm:ss AM/PM",
            20 => "h:mm",
            21 => "h:mm:ss",
            22 => ShortDatePattern(culture) + " h:mm",
            32 or 33 or 34 or 35 => "h:mm:ss",
            37 => "#,##0 ;(#,##0)",
            38 => "#,##0 ;[Red](#,##0)",
            39 => "#,##0.00;(#,##0.00)",
            40 => "#,##0.00;[Red](#,##0.00)",
            41 => "_(* #,##0_);_(* (#,##0);_(* \"-\"_);_(@_)",
            42 => $"_(\"{currency}\"* #,##0_);_(\"{currency}\"* (#,##0);_(\"{currency}\"* \"-\"_);_(@_)",
            43 => "_(* #,##0.00_);_(* (#,##0.00);_(* \"-\"??_);_(@_)",
            44 => $"_(\"{currency}\"* #,##0.00_);_(\"{currency}\"* (#,##0.00);_(\"{currency}\"* \"-\"??_);_(@_)",
            45 => "mm:ss",
            46 => "[h]:mm:ss",
            47 => "mm:ss.0",
            48 => "##0.0E+0",
            49 => "@",
            _ => null,
        };
    }

    /// <summary>The culture's short date ("M/d/yyyy") as an Excel format code ("m/d/yyyy").</summary>
    static string ShortDatePattern(CultureInfo culture)
    {
        var pattern = culture.DateTimeFormat.ShortDatePattern;
        var result = new StringBuilder();
        foreach (var c in pattern)
        {
            result.Append(c switch
            {
                'M' => 'm',
                'y' or 'd' => c,
                '/' or '-' or '.' or ' ' => c,
                _ => c,
            });
        }
        return result.ToString().Replace("'", "", StringComparison.Ordinal);
    }

    /// <summary>True when the format shows dates or times.</summary>
    public static bool IsDate(string code) => Parse(code).Any(s => s.IsDate);

    /// <summary>A number as the format shows it.</summary>
    public static DisplayValue Format(double value, string code, CultureInfo culture, bool date1904 = false)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return new DisplayValue("#NUM!", null);
        var sections = Parse(code);
        var (section, index) = Choose(sections, value);
        if (section is null)
            return new DisplayValue(General(value, culture), null);
        if (section.IsGeneral && !section.Tokens.Any(t => t.Kind is Kind.Literal))
            return new DisplayValue(General(index > 0 && value < 0 && !section.HasCondition ? Math.Abs(value) : value, culture), section.Color);

        // A section other than the first shows the magnitude (its own literals carry the sign: "(1,234)").
        var negativeSign = value < 0 && index == 0;
        var magnitude = index > 0 && !section.HasCondition ? Math.Abs(value) : value;
        if (section.IsGeneral)
            return new DisplayValue(Render(section, General(Math.Abs(magnitude), culture), culture, value < 0 && index == 0), section.Color);
        if (section.IsDate)
            return new DisplayValue(FormatDate(section, value, culture, date1904), section.Color);
        if (section.IsText)
            return new DisplayValue(Render(section, XlsxReader.FormatNumber(value), culture, false), section.Color);
        return new DisplayValue(FormatNumber(section, Math.Abs(magnitude), negativeSign, culture), section.Color);
    }

    /// <summary>A text value as the format shows it (its fourth section, or a single "@" section).</summary>
    public static DisplayValue FormatText(string text, string code, CultureInfo culture)
    {
        var sections = Parse(code);
        Section? section = sections.Length >= 4 ? sections[3] : sections.FirstOrDefault(s => s.IsText && sections.Length == 1);
        if (section is null)
            return new DisplayValue(text, null);
        return new DisplayValue(Render(section, text, culture, false), section.Color);
    }

    /// <summary>Excel's "General": up to 11 characters, scientific for very large or small numbers.</summary>
    public static string General(double value, CultureInfo culture)
    {
        if (value == 0)
            return "0";
        var absolute = Math.Abs(value);
        var separator = culture.NumberFormat.NumberDecimalSeparator;
        if (absolute >= 1e11 || absolute < 1e-9)
        {
            var exponent = (int)Math.Floor(Math.Log10(absolute));
            var mantissa = Math.Round(value / Math.Pow(10, exponent), 5, MidpointRounding.AwayFromZero);
            if (Math.Abs(mantissa) >= 10)
            {
                mantissa /= 10;
                exponent++;
            }
            var mantissaText = mantissa.ToString("0.#####", CultureInfo.InvariantCulture).Replace(".", separator, StringComparison.Ordinal);
            return $"{mantissaText}E{(exponent < 0 ? "-" : "+")}{Math.Abs(exponent):00}";
        }
        var digits = Math.Max(1, (int)Math.Floor(Math.Log10(absolute)) + 1);
        var decimals = Math.Clamp(10 - digits, 0, 10);
        var rounded = Math.Round((decimal)value, decimals, MidpointRounding.AwayFromZero);
        var text = rounded.ToString("0.##########", CultureInfo.InvariantCulture);
        return text.Replace(".", separator, StringComparison.Ordinal);
    }

    // Parsing ---------------------------------------------------------------------------------------------------------

    enum Kind
    {
        Literal,
        Zero,
        Hash,
        Question,
        Point,
        Comma,
        Percent,
        Exponent,
        Slash,
        Text,
        General,
        Year,
        Month,
        Day,
        Hour,
        Minute,
        Second,
        AmPm,
        ElapsedHours,
        ElapsedMinutes,
        ElapsedSeconds,
    }

    sealed record Token(Kind Kind, string Text, int Count = 1);

    sealed class Section
    {
        public List<Token> Tokens { get; } = [];

        public string? Color { get; set; }

        public (string Op, double Value)? Condition { get; set; }

        public bool HasCondition => Condition is not null;

        public bool IsDate { get; set; }

        public bool IsText { get; set; }

        public bool IsGeneral { get; set; }
    }

    static Section[] Parse(string code) => Cache.GetOrAdd(code, static c =>
    {
        // An empty code means General (";;;", with its empty sections, is the one that hides values).
        if (string.IsNullOrWhiteSpace(c))
            return [ParseSection("General")];
        var sections = SplitSections(c).Select(ParseSection).ToArray();
        return sections.Length == 0 ? [ParseSection("General")] : sections;
    });

    static IEnumerable<string> SplitSections(string code)
    {
        var current = new StringBuilder();
        var quoted = false;
        var bracket = false;
        for (var i = 0; i < code.Length; i++)
        {
            var c = code[i];
            if (c == '"' && !bracket)
                quoted = !quoted;
            else if (c == '[' && !quoted)
                bracket = true;
            else if (c == ']' && !quoted)
                bracket = false;
            else if (c == '\\' && !quoted && i + 1 < code.Length)
            {
                current.Append(c).Append(code[++i]);
                continue;
            }
            else if (c == ';' && !quoted && !bracket)
            {
                yield return current.ToString();
                current.Clear();
                continue;
            }
            current.Append(c);
        }
        yield return current.ToString();
    }

    static Section ParseSection(string text)
    {
        var section = new Section();
        var tokens = section.Tokens;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            switch (c)
            {
                case '"':
                    var end = text.IndexOf('"', i + 1);
                    if (end < 0)
                        end = text.Length;
                    tokens.Add(new Token(Kind.Literal, text[(i + 1)..end]));
                    i = end + 1;
                    continue;
                case '\\':
                    if (i + 1 < text.Length)
                        tokens.Add(new Token(Kind.Literal, text[i + 1].ToString()));
                    i += 2;
                    continue;
                case '_':
                    tokens.Add(new Token(Kind.Literal, " "));
                    i += 2;
                    continue;
                case '*':
                    i += 2;
                    continue;
                case '[':
                    var close = text.IndexOf(']', i + 1);
                    if (close < 0)
                        close = text.Length;
                    Bracket(section, text[(i + 1)..close]);
                    i = close + 1;
                    continue;
                case '0':
                    tokens.Add(new Token(Kind.Zero, "0"));
                    break;
                case '#':
                    tokens.Add(new Token(Kind.Hash, "#"));
                    break;
                case '?':
                    tokens.Add(new Token(Kind.Question, "?"));
                    break;
                case '.':
                    tokens.Add(new Token(Kind.Point, "."));
                    break;
                case ',':
                    tokens.Add(new Token(Kind.Comma, ","));
                    break;
                case '%':
                    tokens.Add(new Token(Kind.Percent, "%"));
                    break;
                case '/':
                    tokens.Add(new Token(Kind.Slash, "/"));
                    break;
                case '@':
                    tokens.Add(new Token(Kind.Text, "@"));
                    break;
                case 'E' or 'e' when i + 1 < text.Length && text[i + 1] is '+' or '-':
                    tokens.Add(new Token(Kind.Exponent, text[i + 1].ToString()));
                    i += 2;
                    continue;
                default:
                    if (string.Compare(text, i, "General", 0, 7, StringComparison.OrdinalIgnoreCase) == 0)
                    {
                        tokens.Add(new Token(Kind.General, "General"));
                        i += 7;
                        continue;
                    }
                    if (string.Compare(text, i, "AM/PM", 0, 5, StringComparison.OrdinalIgnoreCase) == 0)
                    {
                        tokens.Add(new Token(Kind.AmPm, text.Substring(i, 5)));
                        i += 5;
                        continue;
                    }
                    if (string.Compare(text, i, "A/P", 0, 3, StringComparison.OrdinalIgnoreCase) == 0)
                    {
                        tokens.Add(new Token(Kind.AmPm, text.Substring(i, 3)));
                        i += 3;
                        continue;
                    }
                    var lower = char.ToLowerInvariant(c);
                    if (lower is 'y' or 'm' or 'd' or 'h' or 's' or 'e')
                    {
                        var run = 1;
                        while (i + run < text.Length && char.ToLowerInvariant(text[i + run]) == lower)
                            run++;
                        var kind = lower switch
                        {
                            'y' or 'e' => Kind.Year,
                            'm' => Kind.Month,
                            'd' => Kind.Day,
                            'h' => Kind.Hour,
                            _ => Kind.Second,
                        };
                        tokens.Add(new Token(kind, text.Substring(i, run), lower == 'e' ? 4 : run));
                        i += run;
                        continue;
                    }
                    if (lower is 'b' or 'g' && i + 1 < text.Length && char.IsDigit(text[i + 1]))
                    {
                        i += 2;
                        continue;
                    }
                    tokens.Add(new Token(Kind.Literal, c.ToString()));
                    break;
            }
            i++;
        }

        section.IsDate = tokens.Any(t => t.Kind is Kind.Year or Kind.Month or Kind.Day or Kind.Hour or Kind.Second or Kind.AmPm
            or Kind.ElapsedHours or Kind.ElapsedMinutes or Kind.ElapsedSeconds);
        section.IsGeneral = tokens.Any(t => t.Kind == Kind.General);
        section.IsText = !section.IsDate && tokens.Any(t => t.Kind == Kind.Text) && !tokens.Any(t => t.Kind is Kind.Zero or Kind.Hash or Kind.Question);
        if (section.IsDate)
            ResolveMinutes(tokens);
        return section;
    }

    static void Bracket(Section section, string content)
    {
        var lower = content.ToLowerInvariant();
        var color = lower switch
        {
            "black" => "#000000",
            "blue" => "#0000ff",
            "cyan" => "#00ffff",
            "green" => "#00ff00",
            "magenta" => "#ff00ff",
            "red" => "#ff0000",
            "white" => "#ffffff",
            "yellow" => "#ffff00",
            _ => null,
        };
        if (color is not null)
        {
            section.Color = color;
            return;
        }
        if (lower.StartsWith('$'))
        {
            var dash = content.IndexOf('-', 1);
            var symbol = dash < 0 ? content[1..] : content[1..dash];
            if (symbol.Length > 0)
                section.Tokens.Add(new Token(Kind.Literal, symbol));
            return;
        }
        if (lower.Length > 0 && lower[0] is '<' or '>' or '=')
        {
            var op = lower.Length > 1 && lower[1] is '=' or '>' ? lower[..2] : lower[..1];
            if (double.TryParse(lower[op.Length..], NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold))
                section.Condition = (op, threshold);
            return;
        }
        if (lower.Length > 0 && lower.All(ch => ch == lower[0]) && lower[0] is 'h' or 'm' or 's')
        {
            section.Tokens.Add(new Token(lower[0] switch { 'h' => Kind.ElapsedHours, 'm' => Kind.ElapsedMinutes, _ => Kind.ElapsedSeconds }, content, lower.Length));
            section.IsDate = true;
        }
    }

    /// <summary>"m" is minutes right after an hour or right before a second, otherwise month.</summary>
    static void ResolveMinutes(List<Token> tokens)
    {
        var timeKinds = new[] { Kind.Year, Kind.Month, Kind.Day, Kind.Hour, Kind.Second, Kind.ElapsedHours, Kind.ElapsedSeconds, Kind.ElapsedMinutes };
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Kind != Kind.Month || tokens[i].Count > 2)
                continue;
            var previous = tokens.Take(i).LastOrDefault(t => timeKinds.Contains(t.Kind));
            var next = tokens.Skip(i + 1).FirstOrDefault(t => timeKinds.Contains(t.Kind));
            if (previous?.Kind is Kind.Hour or Kind.ElapsedHours || next?.Kind is Kind.Second or Kind.ElapsedSeconds)
                tokens[i] = tokens[i] with { Kind = Kind.Minute };
        }
    }

    static (Section? Section, int Index) Choose(Section[] sections, double value)
    {
        if (sections.Any(s => s.HasCondition))
        {
            for (var i = 0; i < sections.Length; i++)
            {
                if (sections[i].Condition is { } condition && Matches(value, condition))
                    return (sections[i], i);
            }
            for (var i = 0; i < sections.Length; i++)
            {
                if (!sections[i].HasCondition && !sections[i].IsText)
                    return (sections[i], i);
            }
            return (null, 0);
        }
        var numeric = sections.Where(s => !(s.IsText && s.Tokens.Any(t => t.Kind == Kind.Text))).ToList();
        if (numeric.Count == 0)
            return (null, 0);
        if (value < 0 && numeric.Count >= 2)
            return (numeric[1], 1);
        if (value == 0 && numeric.Count >= 3)
            return (numeric[2], 2);
        return (numeric[0], 0);
    }

    static bool Matches(double value, (string Op, double Value) condition) => condition.Op switch
    {
        "<" => value < condition.Value,
        "<=" => value <= condition.Value,
        ">" => value > condition.Value,
        ">=" => value >= condition.Value,
        "<>" => value != condition.Value,
        _ => value == condition.Value,
    };

    /// <summary>A section with its placeholders replaced by <paramref name="text"/> (for @ and General sections).</summary>
    static string Render(Section section, string text, CultureInfo culture, bool negative)
    {
        var result = new StringBuilder(negative ? "-" : "");
        foreach (var token in section.Tokens)
        {
            switch (token.Kind)
            {
                case Kind.Text or Kind.General:
                    result.Append(text);
                    break;
                case Kind.Literal:
                    result.Append(token.Text);
                    break;
                case Kind.Point:
                    result.Append(culture.NumberFormat.NumberDecimalSeparator);
                    break;
                case Kind.Comma or Kind.Slash or Kind.Percent:
                    result.Append(token.Text);
                    break;
            }
        }
        return result.ToString();
    }

    // Numbers ---------------------------------------------------------------------------------------------------------

    static bool IsDigit(Kind kind) => kind is Kind.Zero or Kind.Hash or Kind.Question;

    /// <summary>A comma used as thousands separator or scaling: it prints nothing itself.</summary>
    const Kind Consumed = (Kind)(-1);

    static string FormatNumber(Section section, double value, bool negative, CultureInfo culture)
    {
        var tokens = section.Tokens;
        var percent = tokens.Count(t => t.Kind == Kind.Percent);
        value *= Math.Pow(100, percent);

        // Commas: between integer digits = thousands separators; straight after the last digit = divide by 1,000 each.
        var lastDigit = tokens.FindLastIndex(t => IsDigit(t.Kind));
        var pointIndex = tokens.FindIndex(t => t.Kind == Kind.Point);
        var exponentIndex = tokens.FindIndex(t => t.Kind == Kind.Exponent);
        var slashIndex = tokens.FindIndex(t => t.Kind == Kind.Slash);
        var integerEnd = pointIndex >= 0 ? pointIndex : exponentIndex >= 0 ? exponentIndex : lastDigit + 1;
        var thousands = false;
        var scale = 0;
        var kinds = new Kind[tokens.Count];
        for (var i = 0; i < tokens.Count; i++)
        {
            kinds[i] = tokens[i].Kind;
            if (tokens[i].Kind != Kind.Comma)
                continue;
            var digitBefore = tokens.Take(i).Any(t => IsDigit(t.Kind));
            var digitAfterInInteger = Enumerable.Range(i + 1, Math.Max(0, integerEnd - i - 1)).Any(j => j < tokens.Count && IsDigit(tokens[j].Kind));
            if (digitBefore && digitAfterInInteger)
            {
                thousands = true;
                kinds[i] = Consumed;
            }
            else if (digitBefore && (i > lastDigit || (pointIndex >= 0 && i < pointIndex && !digitAfterInInteger)))
            {
                scale++;
                kinds[i] = Consumed;
            }
            else
            {
                kinds[i] = Kind.Literal;
            }
        }
        value /= Math.Pow(1000, scale);

        string body;
        if (slashIndex > 0 && tokens.Take(slashIndex).Any(t => IsDigit(t.Kind)))
            body = Fraction(tokens, kinds, slashIndex, value, culture);
        else if (exponentIndex >= 0)
            body = Scientific(tokens, kinds, exponentIndex, pointIndex, value, culture, thousands);
        else
            body = Fixed(tokens, kinds, pointIndex, value, culture, thousands);

        return negative ? "-" + body : body;
    }

    static string Fixed(List<Token> tokens, Kind[] kinds, int pointIndex, double value, CultureInfo culture, bool thousands)
    {
        var integerSlots = Enumerable.Range(0, pointIndex >= 0 ? pointIndex : tokens.Count).Where(i => IsDigit(kinds[i])).ToList();
        var fractionSlots = pointIndex >= 0 ? Enumerable.Range(pointIndex + 1, tokens.Count - pointIndex - 1).Where(i => IsDigit(kinds[i])).ToList() : [];
        var (integerDigits, fractionDigits) = Digits(value, fractionSlots.Count);
        if (integerDigits == "0" && integerSlots.Count > 0 && integerSlots.All(i => kinds[i] != Kind.Zero))
            integerDigits = "";
        // No integer placeholders: ".00" still shows the whole part in front of its point, but a section without any
        // digit placeholders ("\"Total\"", the empty sections of ";;;" or "0;;") shows only its own literals.
        if (integerSlots.Count == 0)
            integerDigits = integerDigits == "0" || pointIndex < 0 ? "" : integerDigits;

        var output = new string?[tokens.Count];
        FillInteger(tokens, kinds, integerSlots, integerDigits, output, culture, thousands);
        FillFraction(kinds, fractionSlots, fractionDigits, output);
        return Assemble(tokens, kinds, output, culture, integerSlots.Count == 0 ? integerDigits : null);
    }

    /// <summary>Integer and fraction digits of a non-negative value, rounded half away from zero at 15 significant digits like Excel.</summary>
    static (string Integer, string Fraction) Digits(double value, int decimals)
    {
        string text;
        if (value < 7.9e27)
        {
            var rounded = Math.Round((decimal)value, Math.Min(decimals, 28), MidpointRounding.AwayFromZero);
            text = rounded.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        }
        else
        {
            text = value.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        }
        var point = text.IndexOf('.');
        return point < 0 ? (text, "") : (text[..point], text[(point + 1)..]);
    }

    static void FillInteger(List<Token> tokens, Kind[] kinds, List<int> slots, string digits, string?[] output, CultureInfo culture, bool thousands)
    {
        if (slots.Count == 0)
            return;
        // Literals between the integer placeholders ("000-00-0000") mean positional filling, without grouping.
        var interleaved = slots[^1] - slots[0] + 1 != slots.Count && Enumerable.Range(slots[0], slots[^1] - slots[0] + 1).Any(i => kinds[i] == Kind.Literal);
        var minimum = slots.Count(i => kinds[i] == Kind.Zero);
        if (!interleaved)
        {
            var padded = digits.PadLeft(Math.Max(minimum, digits.Length), '0');
            // '?' placeholders beyond the digits become spaces.
            var spaces = Math.Max(0, slots.Count(i => kinds[i] is Kind.Zero or Kind.Question) - padded.Length);
            if (thousands && padded.Length > 3)
                padded = Group(padded, culture.NumberFormat.NumberGroupSeparator);
            output[slots[0]] = new string(' ', spaces) + padded;
            return;
        }
        var position = digits.Length;
        for (var s = slots.Count - 1; s >= 0; s--)
        {
            var slot = slots[s];
            if (s == 0)
            {
                var rest = position > 0 ? digits[..position] : "";
                output[slot] = rest.Length > 0 ? rest : kinds[slot] == Kind.Zero ? "0" : kinds[slot] == Kind.Question ? " " : "";
                break;
            }
            if (position > 0)
            {
                output[slot] = digits[position - 1].ToString();
                position--;
            }
            else
            {
                output[slot] = kinds[slot] == Kind.Zero ? "0" : kinds[slot] == Kind.Question ? " " : "";
            }
        }
    }

    static string Group(string digits, string separator)
    {
        var builder = new StringBuilder();
        var first = digits.Length % 3;
        if (first > 0)
            builder.Append(digits, 0, first);
        for (var i = first; i < digits.Length; i += 3)
        {
            if (builder.Length > 0)
                builder.Append(separator);
            builder.Append(digits, i, 3);
        }
        return builder.ToString();
    }

    static void FillFraction(Kind[] kinds, List<int> slots, string digits, string?[] output)
    {
        var characters = new string[slots.Count];
        for (var s = 0; s < slots.Count; s++)
            characters[s] = s < digits.Length ? digits[s].ToString() : "0";
        // Trailing zeros: '#' drops them, '?' turns them into spaces, '0' keeps them.
        for (var s = slots.Count - 1; s >= 0 && characters[s] == "0"; s--)
        {
            var kind = kinds[slots[s]];
            if (kind == Kind.Zero)
                break;
            characters[s] = kind == Kind.Question ? " " : "";
        }
        for (var s = 0; s < slots.Count; s++)
            output[slots[s]] = characters[s];
    }

    static string Assemble(List<Token> tokens, Kind[] kinds, string?[] output, CultureInfo culture, string? prefixDigits)
    {
        var result = new StringBuilder();
        if (!string.IsNullOrEmpty(prefixDigits))
            result.Append(prefixDigits);
        for (var i = 0; i < tokens.Count; i++)
        {
            if (output[i] is { } filled)
            {
                result.Append(filled);
                continue;
            }
            switch (kinds[i])
            {
                case Kind.Literal:
                    result.Append(tokens[i].Text);
                    break;
                case Kind.Point:
                    result.Append(culture.NumberFormat.NumberDecimalSeparator);
                    break;
                case Kind.Percent:
                    result.Append('%');
                    break;
                case Kind.Slash:
                    result.Append('/');
                    break;
            }
        }
        return result.ToString();
    }

    static string Scientific(List<Token> tokens, Kind[] kinds, int exponentIndex, int pointIndex, double value, CultureInfo culture, bool thousands)
    {
        var mantissaEnd = exponentIndex;
        var integerSlots = Enumerable.Range(0, pointIndex >= 0 && pointIndex < exponentIndex ? pointIndex : mantissaEnd).Where(i => IsDigit(kinds[i])).ToList();
        var fractionSlots = pointIndex >= 0 && pointIndex < exponentIndex
            ? Enumerable.Range(pointIndex + 1, exponentIndex - pointIndex - 1).Where(i => IsDigit(kinds[i])).ToList()
            : [];
        var exponentSlots = Enumerable.Range(exponentIndex + 1, tokens.Count - exponentIndex - 1).Where(i => IsDigit(kinds[i])).ToList();
        var integerCount = Math.Max(1, integerSlots.Count);

        var exponent = 0;
        var mantissa = value;
        if (value != 0)
        {
            exponent = (int)Math.Floor(Math.Log10(value));
            exponent = integerSlots.Any(i => kinds[i] == Kind.Hash) && integerCount > 1
                ? (int)Math.Floor(exponent / (double)integerCount) * integerCount
                : exponent - (integerCount - 1);
            mantissa = value / Math.Pow(10, exponent);
            var rounded = Math.Round(mantissa, fractionSlots.Count, MidpointRounding.AwayFromZero);
            if (rounded >= Math.Pow(10, integerCount))
            {
                exponent += integerSlots.Any(i => kinds[i] == Kind.Hash) && integerCount > 1 ? integerCount : 1;
                mantissa = value / Math.Pow(10, exponent);
            }
        }

        var output = new string?[tokens.Count];
        var (integerDigits, fractionDigits) = Digits(mantissa, fractionSlots.Count);
        FillInteger(tokens, kinds, integerSlots, integerDigits, output, culture, thousands);
        FillFraction(kinds, fractionSlots, fractionDigits, output);
        var sign = exponent < 0 ? "-" : tokens[exponentIndex].Text == "+" ? "+" : "";
        output[exponentIndex] = "E" + sign;
        var exponentText = Math.Abs(exponent).ToString(CultureInfo.InvariantCulture);
        if (exponentSlots.Count > 0)
        {
            output[exponentSlots[0]] = exponentText.PadLeft(exponentSlots.Count(i => kinds[i] == Kind.Zero), '0');
            foreach (var slot in exponentSlots.Skip(1))
                output[slot] = "";
        }
        else
        {
            output[exponentIndex] += exponentText;
        }
        return Assemble(tokens, kinds, output, culture, null);
    }

    static string Fraction(List<Token> tokens, Kind[] kinds, int slashIndex, double value, CultureInfo culture)
    {
        // Numerator placeholders end at the slash; an earlier group (before a literal) is the whole-number part.
        var numeratorStart = slashIndex;
        while (numeratorStart > 0 && IsDigit(kinds[numeratorStart - 1]))
            numeratorStart--;
        var wholeSlots = Enumerable.Range(0, numeratorStart).Where(i => IsDigit(kinds[i])).ToList();
        var numeratorSlots = Enumerable.Range(numeratorStart, slashIndex - numeratorStart).ToList();
        var denominatorSlots = new List<int>();
        var fixedDenominator = new StringBuilder();
        var after = slashIndex + 1;
        while (after < tokens.Count && (IsDigit(kinds[after]) || (kinds[after] == Kind.Literal && tokens[after].Text.All(char.IsDigit) && tokens[after].Text.Length > 0)))
        {
            if (IsDigit(kinds[after]) && fixedDenominator.Length == 0)
                denominatorSlots.Add(after);
            else
                fixedDenominator.Append(tokens[after].Text);
            after++;
        }

        var whole = wholeSlots.Count > 0 ? Math.Floor(value) : 0;
        var remainder = value - whole;
        int numerator, denominator;
        if (fixedDenominator.Length > 0 && int.TryParse(fixedDenominator.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var fixedValue) && fixedValue > 0)
        {
            denominator = fixedValue;
            numerator = (int)Math.Round(remainder * denominator, MidpointRounding.AwayFromZero);
        }
        else
        {
            var maxDenominator = (int)Math.Pow(10, Math.Clamp(denominatorSlots.Count, 1, 4)) - 1;
            (numerator, denominator) = Approximate(remainder, maxDenominator);
        }
        if (numerator == denominator && wholeSlots.Count > 0)
        {
            whole++;
            numerator = 0;
        }

        var result = new StringBuilder();
        for (var i = 0; i < numeratorStart; i++)
        {
            if (wholeSlots.Count > 0 && i == wholeSlots[0])
                result.Append(whole > 0 || numerator == 0 ? whole.ToString("0", CultureInfo.InvariantCulture) : "");
            else if (!IsDigit(kinds[i]))
                result.Append(kinds[i] == Kind.Literal ? (whole > 0 || numerator == 0 ? tokens[i].Text : "") : "");
        }
        if (numerator != 0 || wholeSlots.Count == 0)
        {
            var numeratorText = (wholeSlots.Count == 0 ? (long)Math.Round(value * denominator) : numerator).ToString(CultureInfo.InvariantCulture);
            result.Append(numeratorText.PadLeft(numeratorSlots.Count(i => kinds[i] is Kind.Zero or Kind.Question), ' '));
            result.Append('/');
            result.Append(denominator.ToString(CultureInfo.InvariantCulture).PadRight(Math.Max(denominatorSlots.Count, 1), ' '));
        }
        for (var i = after; i < tokens.Count; i++)
        {
            if (kinds[i] == Kind.Literal)
                result.Append(tokens[i].Text);
        }
        return result.ToString().TrimEnd();
    }

    static (int Numerator, int Denominator) Approximate(double value, int maxDenominator)
    {
        var bestNumerator = 0;
        var bestDenominator = 1;
        var bestError = double.MaxValue;
        for (var denominator = 1; denominator <= maxDenominator; denominator++)
        {
            var numerator = (int)Math.Round(value * denominator);
            var error = Math.Abs(value - (double)numerator / denominator);
            if (error < bestError - 1e-12)
            {
                bestError = error;
                bestNumerator = numerator;
                bestDenominator = denominator;
                if (error < 1e-12)
                    break;
            }
        }
        return (bestNumerator, bestDenominator);
    }

    // Dates -----------------------------------------------------------------------------------------------------------

    /// <summary>An Excel serial date (1900 system, with its Feb 29 1900 quirk; or the 1904 system) as a date and time.</summary>
    public static DateTime SerialToDate(double serial, bool date1904)
    {
        if (date1904)
            serial += 1462;
        // Excel counts a 29 February 1900 that never existed (serial 60): earlier serials are a day off from OLE dates.
        if (serial < 60)
            serial += 1;
        try
        {
            return DateTime.FromOADate(serial);
        }
        catch (ArgumentException)
        {
            return DateTime.MinValue;
        }
    }

    static string FormatDate(Section section, double serial, CultureInfo culture, bool date1904)
    {
        var tokens = section.Tokens;
        // Fractional seconds: "ss.00" (a point followed by zeros right after the seconds; Excel shows up to three).
        var fractionZeros = 0;
        var secondIndex = tokens.FindLastIndex(t => t.Kind is Kind.Second or Kind.ElapsedSeconds);
        if (secondIndex >= 0 && secondIndex + 1 < tokens.Count && tokens[secondIndex + 1].Kind == Kind.Point)
        {
            for (var i = secondIndex + 2; i < tokens.Count && tokens[i].Kind == Kind.Zero; i++)
                fractionZeros++;
        }
        var fractionDigits = Math.Min(fractionZeros, 3);
        if (serial < 0)
            return new string('#', 6);

        // Round to the shown precision once, then do everything in whole units: 09:30 is stored as 0.39583333…, a hair
        // under the minute, and any floating-point step after the rounding would show it as 09:29:59.99.
        var unitsPerSecond = fractionDigits switch { 0 => 1L, 1 => 10L, 2 => 100L, _ => 1000L };
        var unitsPerDay = 86_400L * unitsPerSecond;
        var scaled = serial * unitsPerDay;
        if (!(scaled < 4e15))
            return new string('#', 6); // far past year 9999 (Excel shows ####### too)
        var units = (long)Math.Round(scaled, MidpointRounding.AwayFromZero);
        var date = SerialToDate(units / unitsPerDay, date1904);
        var secondsOfDay = units % unitsPerDay / unitsPerSecond;
        var fraction = units % unitsPerSecond;
        var hours = (int)(secondsOfDay / 3600);
        var minutes = (int)(secondsOfDay / 60 % 60);
        var seconds = (int)(secondsOfDay % 60);
        var totalSeconds = units / unitsPerSecond;
        var hasAmPm = tokens.Any(t => t.Kind == Kind.AmPm);
        var names = culture.DateTimeFormat;

        void AppendFraction(StringBuilder text)
        {
            text.Append(culture.NumberFormat.NumberDecimalSeparator)
                .Append(fraction.ToString(new string('0', fractionDigits), CultureInfo.InvariantCulture))
                .Append('0', fractionZeros - fractionDigits);
        }

        var result = new StringBuilder();
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            switch (token.Kind)
            {
                case Kind.Year:
                    result.Append(token.Count <= 2 ? (date.Year % 100).ToString("00", CultureInfo.InvariantCulture) : date.Year.ToString("0000", CultureInfo.InvariantCulture));
                    break;
                case Kind.Month:
                    result.Append(token.Count switch
                    {
                        1 => date.Month.ToString(CultureInfo.InvariantCulture),
                        2 => date.Month.ToString("00", CultureInfo.InvariantCulture),
                        3 => names.GetAbbreviatedMonthName(date.Month).TrimEnd('.'),
                        5 => names.GetMonthName(date.Month)[..1],
                        _ => names.GetMonthName(date.Month),
                    });
                    break;
                case Kind.Day:
                    result.Append(token.Count switch
                    {
                        1 => date.Day.ToString(CultureInfo.InvariantCulture),
                        2 => date.Day.ToString("00", CultureInfo.InvariantCulture),
                        3 => names.GetAbbreviatedDayName(date.DayOfWeek),
                        _ => names.GetDayName(date.DayOfWeek),
                    });
                    break;
                case Kind.Hour:
                    var hour = hasAmPm ? (hours % 12 == 0 ? 12 : hours % 12) : hours;
                    result.Append(token.Count >= 2 ? hour.ToString("00", CultureInfo.InvariantCulture) : hour.ToString(CultureInfo.InvariantCulture));
                    break;
                case Kind.Minute:
                    result.Append(token.Count >= 2 ? minutes.ToString("00", CultureInfo.InvariantCulture) : minutes.ToString(CultureInfo.InvariantCulture));
                    break;
                case Kind.Second:
                    result.Append(token.Count >= 2 ? seconds.ToString("00", CultureInfo.InvariantCulture) : seconds.ToString(CultureInfo.InvariantCulture));
                    if (fractionZeros > 0 && i == secondIndex)
                    {
                        AppendFraction(result);
                        i += 1 + fractionZeros;
                    }
                    break;
                case Kind.AmPm:
                    var am = hours < 12;
                    var text = token.Text.Length == 3
                        ? (am ? token.Text[0] : token.Text[2]).ToString()
                        : am ? (char.IsLower(token.Text[0]) ? "am" : "AM") : (char.IsLower(token.Text[3]) ? "pm" : "PM");
                    result.Append(text);
                    break;
                case Kind.ElapsedHours:
                    result.Append((totalSeconds / 3600).ToString(new string('0', token.Count), CultureInfo.InvariantCulture));
                    break;
                case Kind.ElapsedMinutes:
                    result.Append((totalSeconds / 60).ToString(new string('0', token.Count), CultureInfo.InvariantCulture));
                    break;
                case Kind.ElapsedSeconds:
                    result.Append(totalSeconds.ToString(new string('0', token.Count), CultureInfo.InvariantCulture));
                    if (fractionZeros > 0 && i == secondIndex)
                    {
                        AppendFraction(result);
                        i += 1 + fractionZeros;
                    }
                    break;
                case Kind.Literal or Kind.Comma or Kind.Slash or Kind.Percent:
                    result.Append(token.Text);
                    break;
                case Kind.Point:
                    result.Append('.');
                    break;
                case Kind.Zero:
                    result.Append('0');
                    break;
            }
        }
        return result.ToString();
    }
}
