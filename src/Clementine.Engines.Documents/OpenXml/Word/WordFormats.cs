using DocumentFormat.OpenXml;
using static Clementine.Engines.Documents.OpenXml.Ooxml;

namespace Clementine.Engines.Documents.OpenXml.Word;

/// <summary>A border line: CSS style, width and colour, and its distance from the text.</summary>
internal sealed record WordBorder(string Style, double Width, string Color, double Space)
{
    public static readonly WordBorder None = new("none", 0, "#000000", 0);

    public bool IsVisible => Style != "none" && Width > 0;

    public string Css => IsVisible ? $"{Pt(Math.Max(Width, Style == "double" ? 2.25 : 0.5))} {Style} {Color}" : "none";

    /// <summary>A w:top / w:bottom / w:insideH … border element.</summary>
    public static WordBorder? From(OpenXmlElement? element, OfficeTheme theme)
    {
        if (element is null)
            return null;
        var value = element.Attr("val") ?? "single";
        if (value is "nil" or "none")
            return None;
        var style = value switch
        {
            "double" or "triple" or "thinThickSmallGap" or "thickThinSmallGap" or "thinThickMediumGap" or "thickThinMediumGap"
                or "thinThickLargeGap" or "thickThinLargeGap" or "thinThickThinSmallGap" or "thinThickThinMediumGap" or "thinThickThinLargeGap" => "double",
            "dotted" => "dotted",
            "dashed" or "dashSmallGap" or "dotDash" or "dotDotDash" or "dashDotStroked" => "dashed",
            "inset" => "inset",
            "outset" => "outset",
            "threeDEmboss" => "ridge",
            "threeDEngrave" => "groove",
            _ => "solid",
        };
        // Line widths are eighths of a point (2–96); art borders use points, but those are rare enough to ignore.
        var width = (Number(element.Attr("sz")) ?? 4) / 8.0;
        var color = WordColor(element.Attr("color"), element.Attr("themeColor"), element.Attr("themeTint"), element.Attr("themeShade"), theme) ?? "#000000";
        return new WordBorder(style, Math.Clamp(width, 0.25, 12), color, Number(element.Attr("space")) ?? 0);
    }

    /// <summary>A WordprocessingML colour: explicit hex wins; else the theme colour with its tint/shade; "auto" → null.</summary>
    public static string? WordColor(string? value, string? themeColor, string? themeTint, string? themeShade, OfficeTheme theme)
    {
        var hex = OfficeTheme.Hex(value);
        if (hex is null && themeColor is not null && theme.Color(themeColor) is { } scheme)
            hex = OfficeTheme.TintShade("#" + scheme, themeTint, themeShade);
        return hex;
    }
}

/// <summary>A tab stop: position from the text column's left edge (pt), alignment and leader.</summary>
internal readonly record struct WordTab(double Position, string Align, string Leader);

/// <summary>Effective character formatting, merged from document defaults, styles and direct formatting.</summary>
internal sealed class WordRunFormat
{
    public string? Font { get; set; }

    public string? EastAsiaFont { get; set; }

    public string? ComplexFont { get; set; }

    /// <summary>Points.</summary>
    public double Size { get; set; } = 10;

    public double ComplexSize { get; set; } = 10;

    public bool Bold { get; set; }

    public bool Italic { get; set; }

    public bool Caps { get; set; }

    public bool SmallCaps { get; set; }

    public bool Strike { get; set; }

    public bool DoubleStrike { get; set; }

    public bool Hidden { get; set; }

    /// <summary>Underline style ("single", "double", "wave"…) or null.</summary>
    public string? Underline { get; set; }

    public string? UnderlineColor { get; set; }

    /// <summary>"#rrggbb", or null for automatic (black).</summary>
    public string? Color { get; set; }

    public string? Highlight { get; set; }

    public string? Shading { get; set; }

    /// <summary>"superscript", "subscript" or null.</summary>
    public string? VerticalAlign { get; set; }

    /// <summary>Raised (+) or lowered (−) by this many points.</summary>
    public double Position { get; set; }

    /// <summary>Extra letter spacing in points.</summary>
    public double Spacing { get; set; }

    public bool RightToLeft { get; set; }

    public WordRunFormat Clone() => (WordRunFormat)MemberwiseClone();

    /// <summary>Applies an rPr-like element (w:rPr in styles, runs, numbering levels, paragraph marks).</summary>
    public void Apply(OpenXmlElement? properties, OfficeTheme theme)
    {
        if (properties is null)
            return;
        foreach (var child in properties.ChildElements)
        {
            switch (child.LocalName)
            {
                case "rFonts":
                    Font = ThemeFont(child.Attr("asciiTheme"), theme) ?? child.Attr("ascii") ?? ThemeFont(child.Attr("hAnsiTheme"), theme) ?? child.Attr("hAnsi") ?? Font;
                    EastAsiaFont = ThemeFont(child.Attr("eastAsiaTheme"), theme) ?? child.Attr("eastAsia") ?? EastAsiaFont;
                    ComplexFont = ThemeFont(child.Attr("cstheme"), theme) ?? child.Attr("cs") ?? ComplexFont;
                    break;
                case "b":
                    Bold = IsOn(child);
                    break;
                case "i":
                    Italic = IsOn(child);
                    break;
                case "caps":
                    Caps = IsOn(child);
                    break;
                case "smallCaps":
                    SmallCaps = IsOn(child);
                    break;
                case "strike":
                    Strike = IsOn(child);
                    break;
                case "dstrike":
                    DoubleStrike = IsOn(child);
                    break;
                case "vanish":
                    Hidden = IsOn(child);
                    break;
                case "sz":
                    if (Number(child.Attr("val")) is { } size and > 0)
                        Size = Math.Clamp(size / 2, 1, 1638);
                    break;
                case "szCs":
                    if (Number(child.Attr("val")) is { } complex and > 0)
                        ComplexSize = Math.Clamp(complex / 2, 1, 1638);
                    break;
                case "color":
                    Color = child.Attr("val") == "auto" && child.Attr("themeColor") is null
                        ? null
                        : WordBorder.WordColor(child.Attr("val"), child.Attr("themeColor"), child.Attr("themeTint"), child.Attr("themeShade"), theme) ?? Color;
                    break;
                case "highlight":
                    Highlight = HighlightColor(child.Attr("val"));
                    break;
                case "shd":
                    Shading = ShadingColor(child, theme);
                    break;
                case "u":
                    var underline = child.Attr("val") ?? "single";
                    Underline = underline == "none" ? null : underline;
                    UnderlineColor = OfficeTheme.Hex(child.Attr("color"));
                    break;
                case "vertAlign":
                    var align = child.Attr("val");
                    VerticalAlign = align is "superscript" or "subscript" ? align : null;
                    break;
                case "position":
                    Position = (Number(child.Attr("val")) ?? 0) / 2;
                    break;
                case "spacing":
                    Spacing = Twips(child.Attr("val")) ?? 0;
                    break;
                case "rtl":
                    RightToLeft = IsOn(child);
                    break;
            }
        }
    }

    public static string? ThemeFont(string? name, OfficeTheme theme) => name switch
    {
        null => null,
        "majorHAnsi" or "majorAscii" => theme.MajorLatin,
        "minorHAnsi" or "minorAscii" => theme.MinorLatin,
        "majorEastAsia" => theme.MajorEastAsia,
        "minorEastAsia" => theme.MinorEastAsia,
        "majorBidi" => theme.MajorLatin,
        "minorBidi" => theme.MinorLatin,
        _ => null,
    };

    /// <summary>The fill of a w:shd element (solid fills and the common percentage patterns), or null.</summary>
    public static string? ShadingColor(OpenXmlElement shading, OfficeTheme theme)
    {
        var fill = WordBorder.WordColor(shading.Attr("fill"), shading.Attr("themeFill"), shading.Attr("themeFillTint"), shading.Attr("themeFillShade"), theme);
        var pattern = shading.Attr("val") ?? "clear";
        if (pattern is "clear" or "nil")
            return fill;
        var color = WordBorder.WordColor(shading.Attr("color"), shading.Attr("themeColor"), null, null, theme) ?? "#000000";
        if (pattern == "solid")
            return color;
        if (pattern.StartsWith("pct", StringComparison.Ordinal) && Number(pattern[3..]) is { } percent)
            return OfficeTheme.Mix(color, fill ?? "#ffffff", Math.Clamp(percent / 100, 0, 1));
        return fill;
    }

    static string? HighlightColor(string? name) => name switch
    {
        "yellow" => "#ffff00",
        "green" => "#00ff00",
        "cyan" => "#00ffff",
        "magenta" => "#ff00ff",
        "blue" => "#0000ff",
        "red" => "#ff0000",
        "darkBlue" => "#000080",
        "darkCyan" => "#008080",
        "darkGreen" => "#008000",
        "darkMagenta" => "#800080",
        "darkRed" => "#800000",
        "darkYellow" => "#808000",
        "darkGray" => "#808080",
        "lightGray" => "#c0c0c0",
        "black" => "#000000",
        "white" => "#ffffff",
        _ => null,
    };
}

/// <summary>Effective paragraph formatting. Lengths are points.</summary>
internal sealed class WordParagraphFormat
{
    public string Align { get; set; } = "left";

    public double Left { get; set; }

    public double Right { get; set; }

    /// <summary>First-line indent; negative for a hanging indent.</summary>
    public double FirstLine { get; set; }

    public double Before { get; set; }

    public double After { get; set; }

    public bool BeforeAuto { get; set; }

    public bool AfterAuto { get; set; }

    /// <summary>Line spacing: a multiple (lineRule auto, 1 = single) or points (exact / atLeast).</summary>
    public double Line { get; set; } = 1;

    public string LineRule { get; set; } = "auto";

    public bool ContextualSpacing { get; set; }

    public bool KeepNext { get; set; }

    public bool KeepLines { get; set; }

    public bool PageBreakBefore { get; set; }

    public bool Bidi { get; set; }

    public WordBorder? Top { get; set; }

    public WordBorder? Bottom { get; set; }

    public WordBorder? LeftBorder { get; set; }

    public WordBorder? RightBorder { get; set; }

    public string? Shading { get; set; }

    public List<WordTab> Tabs { get; set; } = [];

    public int? OutlineLevel { get; set; }

    public int? NumId { get; set; }

    public int? Level { get; set; }

    public WordParagraphFormat Clone()
    {
        var copy = (WordParagraphFormat)MemberwiseClone();
        copy.Tabs = [.. Tabs];
        return copy;
    }

    /// <summary>Applies a pPr-like element.</summary>
    public void Apply(OpenXmlElement? properties, OfficeTheme theme)
    {
        if (properties is null)
            return;
        foreach (var child in properties.ChildElements)
        {
            switch (child.LocalName)
            {
                case "jc":
                    Align = child.Attr("val") ?? Align;
                    break;
                case "ind":
                    Left = Twips(child.Attr("start") ?? child.Attr("left")) ?? Left;
                    Right = Twips(child.Attr("end") ?? child.Attr("right")) ?? Right;
                    if (Twips(child.Attr("hanging")) is { } hanging)
                        FirstLine = -hanging;
                    else if (Twips(child.Attr("firstLine")) is { } first)
                        FirstLine = first;
                    break;
                case "spacing":
                    Before = Twips(child.Attr("before")) ?? Before;
                    After = Twips(child.Attr("after")) ?? After;
                    if (child.Attr("beforeAutospacing") is { } beforeAuto)
                        BeforeAuto = IsTrue(beforeAuto);
                    if (child.Attr("afterAutospacing") is { } afterAuto)
                        AfterAuto = IsTrue(afterAuto);
                    if (Number(child.Attr("line")) is { } line && line > 0)
                    {
                        LineRule = child.Attr("lineRule") ?? "auto";
                        Line = LineRule == "auto" ? line / 240 : line / 20;
                    }
                    break;
                case "contextualSpacing":
                    ContextualSpacing = IsOn(child);
                    break;
                case "keepNext":
                    KeepNext = IsOn(child);
                    break;
                case "keepLines":
                    KeepLines = IsOn(child);
                    break;
                case "pageBreakBefore":
                    PageBreakBefore = IsOn(child);
                    break;
                case "bidi":
                    Bidi = IsOn(child);
                    break;
                case "pBdr":
                    Top = WordBorder.From(child.Child("top"), theme) ?? Top;
                    Bottom = WordBorder.From(child.Child("bottom"), theme) ?? Bottom;
                    LeftBorder = WordBorder.From(child.Child("left") ?? child.Child("start"), theme) ?? LeftBorder;
                    RightBorder = WordBorder.From(child.Child("right") ?? child.Child("end"), theme) ?? RightBorder;
                    break;
                case "shd":
                    Shading = WordRunFormat.ShadingColor(child, theme);
                    break;
                case "tabs":
                    foreach (var tab in child.Children("tab"))
                    {
                        if (Twips(tab.Attr("pos")) is not { } position)
                            continue;
                        Tabs.RemoveAll(t => Math.Abs(t.Position - position) < 0.5);
                        var align = tab.Attr("val") ?? "left";
                        if (align is not ("clear" or "bar"))
                            Tabs.Add(new WordTab(position, align is "end" ? "right" : align is "start" ? "left" : align, tab.Attr("leader") ?? "none"));
                    }
                    Tabs.Sort((a, b) => a.Position.CompareTo(b.Position));
                    break;
                case "outlineLvl":
                    OutlineLevel = Integer(child.Attr("val"));
                    break;
                case "numPr":
                    if (Integer(child.Child("numId").Attr("val")) is { } numId)
                        NumId = numId;
                    if (Integer(child.Child("ilvl").Attr("val")) is { } level)
                        Level = Math.Clamp(level, 0, 8);
                    break;
            }
        }
    }
}
