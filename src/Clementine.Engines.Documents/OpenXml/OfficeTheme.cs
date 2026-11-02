using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;

namespace Clementine.Engines.Documents.OpenXml;

/// <summary>A document theme's colour scheme and major/minor fonts, plus Office's colour arithmetic (tint, shade).</summary>
internal sealed class OfficeTheme
{
    static readonly string[] SchemeNames = ["dk1", "lt1", "dk2", "lt2", "accent1", "accent2", "accent3", "accent4", "accent5", "accent6", "hlink", "folHlink"];

    /// <summary>Office 2013–2022 defaults, used when a file has no theme part.</summary>
    static readonly Dictionary<string, string> DefaultColors = new(StringComparer.Ordinal)
    {
        ["dk1"] = "000000", ["lt1"] = "FFFFFF", ["dk2"] = "44546A", ["lt2"] = "E7E6E6",
        ["accent1"] = "4472C4", ["accent2"] = "ED7D31", ["accent3"] = "A5A5A5", ["accent4"] = "FFC000",
        ["accent5"] = "5B9BD5", ["accent6"] = "70AD47", ["hlink"] = "0563C1", ["folHlink"] = "954F72",
    };

    readonly Dictionary<string, string> _colors;

    OfficeTheme(Dictionary<string, string> colors, string majorLatin, string minorLatin, string? majorEastAsia, string? minorEastAsia)
    {
        _colors = colors;
        MajorLatin = majorLatin;
        MinorLatin = minorLatin;
        MajorEastAsia = majorEastAsia;
        MinorEastAsia = minorEastAsia;
    }

    public string MajorLatin { get; }

    public string MinorLatin { get; }

    public string? MajorEastAsia { get; }

    public string? MinorEastAsia { get; }

    public static OfficeTheme Default { get; } = new(new Dictionary<string, string>(DefaultColors), "Calibri Light", "Calibri", null, null);

    public static OfficeTheme From(ThemePart? part)
    {
        OpenXmlElement? root;
        try
        {
            root = part?.RootElement;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            root = null;
        }
        if (root is null)
            return Default;

        var elements = root.Child("themeElements");
        var colors = new Dictionary<string, string>(DefaultColors);
        if (elements.Child("clrScheme") is { } scheme)
        {
            foreach (var name in SchemeNames)
            {
                var entry = scheme.Child(name);
                var value = entry.Child("srgbClr").Attr("val") ?? entry.Child("sysClr").Attr("lastClr");
                // Only real hex: tint and shade arithmetic parses these later ("zzzzzz" keeps the default).
                if (value is { Length: 6 } && Hex(value) is not null)
                    colors[name] = value;
            }
        }
        var fonts = elements.Child("fontScheme");
        var major = fonts.Child("majorFont");
        var minor = fonts.Child("minorFont");
        return new OfficeTheme(colors,
            NonEmpty(major.Child("latin").Attr("typeface")) ?? "Calibri Light",
            NonEmpty(minor.Child("latin").Attr("typeface")) ?? "Calibri",
            NonEmpty(major.Child("ea").Attr("typeface")),
            NonEmpty(minor.Child("ea").Attr("typeface")));
    }

    static string? NonEmpty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

    /// <summary>
    /// A scheme colour as "RRGGBB". Accepts DrawingML names (dk1, accent1, hlink), WordprocessingML names (dark1,
    /// text1, background1, hyperlink) and SpreadsheetML indexes (0 = lt1, 1 = dk1, 2 = lt2, 3 = dk2, 4–9 accents).
    /// </summary>
    public string? Color(string name)
    {
        var key = name switch
        {
            "dark1" or "text1" or "tx1" => "dk1",
            "light1" or "background1" or "bg1" => "lt1",
            "dark2" or "text2" or "tx2" => "dk2",
            "light2" or "background2" or "bg2" => "lt2",
            "hyperlink" => "hlink",
            "followedHyperlink" => "folHlink",
            _ => name,
        };
        return _colors.TryGetValue(key, out var value) ? value : null;
    }

    public string? SpreadsheetColor(int index) => index switch
    {
        0 => Color("lt1"),
        1 => Color("dk1"),
        2 => Color("lt2"),
        3 => Color("dk2"),
        >= 4 and <= 9 => Color($"accent{index - 3}"),
        10 => Color("hlink"),
        11 => Color("folHlink"),
        _ => null,
    };

    /// <summary>"#rrggbb" for a 6-digit (RGB) or 8-digit (ARGB) hex value; null for "auto" and anything else.</summary>
    public static string? Hex(string? value)
    {
        if (value is null)
            return null;
        var text = value.Trim();
        if (text.Length == 8)
            text = text[2..];
        if (text.Length != 6 || !int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
            return null;
        return "#" + text.ToLowerInvariant();
    }

    /// <summary>SpreadsheetML tint (−1…1): negative darkens, positive lightens, in HSL luminance.</summary>
    public static string Tint(string hex, double tint)
    {
        if (tint == 0)
            return hex;
        var (h, s, l) = ToHsl(hex);
        l = tint < 0 ? l * (1 + tint) : l * (1 - tint) + tint;
        return FromHsl(h, s, Math.Clamp(l, 0, 1));
    }

    /// <summary>WordprocessingML themeTint / themeShade (hex bytes 00–FF).</summary>
    public static string TintShade(string hex, string? themeTint, string? themeShade)
    {
        var result = hex;
        if (themeTint is not null && int.TryParse(themeTint, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var tint) && tint is > 0 and < 255)
        {
            var (h, s, l) = ToHsl(result);
            result = FromHsl(h, s, l * tint / 255.0 + (1 - tint / 255.0));
        }
        if (themeShade is not null && int.TryParse(themeShade, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var shade) && shade is > 0 and < 255)
        {
            var (h, s, l) = ToHsl(result);
            result = FromHsl(h, s, l * shade / 255.0);
        }
        return result;
    }

    /// <summary>Mixes two "#rrggbb" colours: <paramref name="amount"/> of <paramref name="front"/> over <paramref name="back"/>.</summary>
    public static string Mix(string front, string back, double amount)
    {
        var (r1, g1, b1) = Rgb(front);
        var (r2, g2, b2) = Rgb(back);
        int Blend(int a, int b) => (int)Math.Round(a * amount + b * (1 - amount));
        return $"#{Blend(r1, r2):x2}{Blend(g1, g2):x2}{Blend(b1, b2):x2}";
    }

    static (int R, int G, int B) Rgb(string hex)
    {
        var text = hex.TrimStart('#');
        return (int.Parse(text[0..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(text[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(text[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    static (double H, double S, double L) ToHsl(string hex)
    {
        var (ri, gi, bi) = Rgb(hex);
        double r = ri / 255.0, g = gi / 255.0, b = bi / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        if (max == min)
            return (0, 0, l);
        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h;
        if (max == r)
            h = (g - b) / d + (g < b ? 6 : 0);
        else if (max == g)
            h = (b - r) / d + 2;
        else
            h = (r - g) / d + 4;
        return (h / 6, s, l);
    }

    static string FromHsl(double h, double s, double l)
    {
        double r, g, b;
        if (s == 0)
        {
            r = g = b = l;
        }
        else
        {
            var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            var p = 2 * l - q;
            r = Hue(p, q, h + 1.0 / 3);
            g = Hue(p, q, h);
            b = Hue(p, q, h - 1.0 / 3);
        }
        return $"#{(int)Math.Round(r * 255):x2}{(int)Math.Round(g * 255):x2}{(int)Math.Round(b * 255):x2}";
    }

    static double Hue(double p, double q, double t)
    {
        if (t < 0)
            t += 1;
        if (t > 1)
            t -= 1;
        if (t < 1.0 / 6)
            return p + (q - p) * 6 * t;
        if (t < 1.0 / 2)
            return q;
        if (t < 2.0 / 3)
            return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }
}
