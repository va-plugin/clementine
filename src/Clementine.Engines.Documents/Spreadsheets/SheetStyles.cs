using System.Globalization;
using System.Text;
using Clementine.Engines.Documents.OpenXml;
using Clementine.Engines.Documents.OpenXml.Word;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using static Clementine.Engines.Documents.OpenXml.Ooxml;

namespace Clementine.Engines.Documents.Spreadsheets;

/// <summary>A cell format (xf): number format, font, fill, borders and alignment, as CSS.</summary>
internal sealed record CellStyle(string NumberFormat, string Css, string Horizontal, bool Wrap, string? FontColor);

/// <summary>A workbook's cell formats (styles.xml) resolved to CSS, with theme and legacy indexed colours.</summary>
internal sealed class SheetStyles
{
    static readonly string[] DefaultPalette =
    [
        "000000", "FFFFFF", "FF0000", "00FF00", "0000FF", "FFFF00", "FF00FF", "00FFFF",
        "000000", "FFFFFF", "FF0000", "00FF00", "0000FF", "FFFF00", "FF00FF", "00FFFF",
        "800000", "008000", "000080", "808000", "800080", "008080", "C0C0C0", "808080",
        "9999FF", "993366", "FFFFCC", "CCFFFF", "660066", "FF8080", "0066CC", "CCCCFF",
        "000080", "FF00FF", "FFFF00", "00FFFF", "800080", "800000", "008080", "0000FF",
        "00CCFF", "CCFFFF", "CCFFCC", "FFFF99", "99CCFF", "FF99CC", "CC99FF", "FFCC99",
        "3366FF", "33CCCC", "99CC00", "FFCC00", "FF9900", "FF6600", "666699", "969696",
        "003366", "339966", "003300", "333300", "993300", "993366", "333399", "333333",
    ];

    readonly List<OpenXmlElement> _fonts = [];
    readonly List<OpenXmlElement> _fills = [];
    readonly List<OpenXmlElement> _borders = [];
    readonly List<OpenXmlElement> _formats = [];
    readonly Dictionary<int, string> _numberFormats = [];
    readonly string[] _palette;
    readonly OfficeTheme _theme;
    readonly CultureInfo _culture;
    readonly Dictionary<int, CellStyle> _cache = [];

    public SheetStyles(WorkbookPart workbook, CultureInfo culture)
    {
        _culture = culture;
        _theme = OfficeTheme.From(workbook.ThemePart);
        OpenXmlElement? root = null;
        try
        {
            root = workbook.WorkbookStylesPart?.RootElement;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }
        _fonts.AddRange(root.Child("fonts").Children("font"));
        _fills.AddRange(root.Child("fills").Children("fill"));
        _borders.AddRange(root.Child("borders").Children("border"));
        _formats.AddRange(root.Child("cellXfs").Children("xf"));
        foreach (var format in root.Child("numFmts").Children("numFmt"))
        {
            if (Integer(format.Attr("numFmtId")) is { } id && format.Attr("formatCode") is { } code)
                _numberFormats[id] = code;
        }
        var custom = root.Child("colors").Child("indexedColors").Children("rgbColor").Select(c => c.Attr("rgb") ?? "").ToList();
        _palette = custom.Count > 0 ? [.. custom] : DefaultPalette;

        var font = _fonts.FirstOrDefault();
        DefaultFont = font.Child("name").Attr("val") ?? "Calibri";
        DefaultSize = Number(font.Child("sz").Attr("val")) is { } size and > 0 ? size : 11;
    }

    public string DefaultFont { get; }

    public double DefaultSize { get; }

    public int Count => _formats.Count;

    public CellStyle Get(int index)
    {
        if (_cache.TryGetValue(index, out var cached))
            return cached;
        var xf = index >= 0 && index < _formats.Count ? _formats[index] : null;
        var numberFormatId = Integer(xf.Attr("numFmtId")) ?? 0;
        var code = _numberFormats.TryGetValue(numberFormatId, out var custom) ? custom : NumberFormats.BuiltIn(numberFormatId, _culture) ?? "General";

        var css = new StringBuilder();
        var font = Pick(_fonts, Integer(xf.Attr("fontId")));
        var fontColor = Color(font.Child("color"));
        AppendFont(css, font, fontColor, isDefault: Integer(xf.Attr("fontId")) is null or 0);

        var fill = Pick(_fills, Integer(xf.Attr("fillId")));
        if (FillCss(fill) is { } background)
            css.Append(background);

        var border = Pick(_borders, Integer(xf.Attr("borderId")));
        foreach (var side in new[] { "left", "right", "top", "bottom" })
        {
            if (BorderCss(border.Child(side)) is { } line)
                css.Append("border-").Append(side).Append(": ").Append(line).Append("; ");
        }

        var alignment = xf.Child("alignment");
        var horizontal = alignment.Attr("horizontal") ?? "general";
        var vertical = alignment.Attr("vertical") ?? "bottom";
        var wrap = IsTrue(alignment.Attr("wrapText")) || horizontal is "justify" or "distributed" || vertical is "justify" or "distributed";
        switch (horizontal)
        {
            case "left":
                css.Append("text-align: left; ");
                break;
            case "center" or "centerContinuous":
                css.Append("text-align: center; ");
                break;
            case "right":
                css.Append("text-align: right; ");
                break;
            case "justify" or "distributed":
                css.Append("text-align: justify; ");
                break;
        }
        css.Append("vertical-align: ").Append(vertical switch { "top" => "top", "center" => "middle", _ => "bottom" }).Append("; ");
        if (Integer(alignment.Attr("indent")) is > 0 and var indent)
            css.Append(horizontal == "right" ? "padding-right: " : "padding-left: ").Append(Pt(2.25 + indent * 7.5)).Append("; ");

        var style = new CellStyle(code, css.ToString().TrimEnd(), horizontal, wrap, fontColor);
        _cache[index] = style;
        return style;
    }

    static OpenXmlElement? Pick(List<OpenXmlElement> list, int? index) =>
        index is { } i && i >= 0 && i < list.Count ? list[i] : list.FirstOrDefault();

    /// <summary>Font CSS (a cell's font, or a rich-text run's rPr, which uses the same child elements).</summary>
    public void AppendFont(StringBuilder css, OpenXmlElement? font, string? color, bool isDefault)
    {
        if (font is null)
            return;
        var name = font.Child("name").Attr("val") ?? font.Child("rFont").Attr("val");
        if (name is not null && (!isDefault || !string.Equals(name, DefaultFont, StringComparison.OrdinalIgnoreCase)))
            css.Append("font-family: ").Append(WordFonts.Stack(name, null, null)).Append("; ");
        if (Number(font.Child("sz").Attr("val")) is { } size and > 0 && (!isDefault || Math.Abs(size - DefaultSize) > 0.01))
            css.Append("font-size: ").Append(Pt(size)).Append("; ");
        if (font.Child("b") is { } bold && IsOn(bold))
            css.Append("font-weight: 700; ");
        if (font.Child("i") is { } italic && IsOn(italic))
            css.Append("font-style: italic; ");
        var lines = new List<string>();
        var underline = font.Child("u");
        if (underline is not null && underline.Attr("val") != "none")
            lines.Add("underline");
        if (font.Child("strike") is { } strike && IsOn(strike))
            lines.Add("line-through");
        if (lines.Count > 0)
        {
            css.Append("text-decoration-line: ").Append(string.Join(' ', lines)).Append("; ");
            if (underline?.Attr("val") is "double" or "doubleAccounting")
                css.Append("text-decoration-style: double; ");
        }
        if (font.Child("vertAlign").Attr("val") is ("superscript" or "subscript") and var align)
            css.Append("vertical-align: ").Append(align == "superscript" ? "super" : "sub").Append("; font-size: 70%; ");
        if (color is not null)
            css.Append("color: ").Append(color).Append("; ");
    }

    /// <summary>A SpreadsheetML colour (rgb, theme + tint, indexed); null for automatic.</summary>
    public string? Color(OpenXmlElement? color)
    {
        if (color is null || IsTrue(color.Attr("auto")))
            return null;
        string? hex = null;
        if (color.Attr("rgb") is { } rgb)
            hex = OfficeTheme.Hex(rgb);
        else if (Integer(color.Attr("theme")) is { } theme && _theme.SpreadsheetColor(theme) is { } scheme)
            hex = "#" + scheme.ToLowerInvariant();
        else if (Integer(color.Attr("indexed")) is { } indexed and >= 0 and < 64 && indexed < _palette.Length)
            hex = OfficeTheme.Hex(_palette[indexed]);
        if (hex is not null && Number(color.Attr("tint")) is { } tint and not 0)
            hex = OfficeTheme.Tint(hex, Math.Clamp(tint, -1, 1));
        return hex;
    }

    string? FillCss(OpenXmlElement? fill)
    {
        if (fill is null)
            return null;
        if (fill.Child("gradientFill") is { } gradient)
        {
            var stops = gradient.Children("stop").Select(s => (Position: Number(s.Attr("position")) ?? 0, Color: Color(s.Child("color")) ?? "#ffffff")).ToList();
            if (stops.Count == 0)
                return null;
            if (stops.Count == 1)
                return $"background-color: {stops[0].Color}; ";
            var degree = Number(gradient.Attr("degree")) ?? 0;
            var parts = string.Join(", ", stops.Select(s => $"{s.Color} {Css(s.Position * 100)}%"));
            return $"background: linear-gradient({Css(degree + 90)}deg, {parts}); ";
        }
        var pattern = fill.Child("patternFill");
        var type = pattern.Attr("patternType") ?? (pattern?.Child("fgColor") is not null ? "solid" : "none");
        if (type == "none")
            return null;
        var foreground = Color(pattern.Child("fgColor")) ?? "#000000";
        var backgroundColor = Color(pattern.Child("bgColor")) ?? "#ffffff";
        if (type == "solid")
            return $"background-color: {foreground}; ";
        var amount = type switch
        {
            "darkGray" => 0.75,
            "mediumGray" => 0.5,
            "lightGray" => 0.25,
            "gray125" => 0.125,
            "gray0625" => 0.0625,
            _ => 0.5,
        };
        return $"background-color: {OfficeTheme.Mix(foreground, backgroundColor, amount)}; ";
    }

    string? BorderCss(OpenXmlElement? side)
    {
        var style = side.Attr("style");
        if (style is null or "none")
            return null;
        var color = Color(side.Child("color")) ?? "#000000";
        var line = style switch
        {
            "thin" => "1px solid",
            "hair" => "0.5px solid",
            "medium" => "2px solid",
            "thick" => "3px solid",
            "dashed" => "1px dashed",
            "dotted" => "1px dotted",
            "double" => "3px double",
            "mediumDashed" => "2px dashed",
            "dashDot" or "dashDotDot" => "1px dashed",
            "mediumDashDot" or "mediumDashDotDot" or "slantDashDot" => "2px dashed",
            _ => "1px solid",
        };
        return $"{line} {color}";
    }
}
