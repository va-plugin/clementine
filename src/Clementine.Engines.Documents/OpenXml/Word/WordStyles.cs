using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using static Clementine.Engines.Documents.OpenXml.Ooxml;

namespace Clementine.Engines.Documents.OpenXml.Word;

/// <summary>One w:style: its kind, base style and property containers (and, for table styles, conditional formats).</summary>
internal sealed class WordStyle
{
    public required string Id { get; init; }

    public required string Type { get; init; }

    public string? Name { get; init; }

    public string? BasedOn { get; init; }

    /// <summary>The w:style element itself (its pPr / rPr act as a layer for paragraphs in tables).</summary>
    public OpenXmlElement? Element { get; init; }

    public OpenXmlElement? ParagraphProperties { get; init; }

    public OpenXmlElement? RunProperties { get; init; }

    public OpenXmlElement? TableProperties { get; init; }

    public OpenXmlElement? RowProperties { get; init; }

    public OpenXmlElement? CellProperties { get; init; }

    /// <summary>w:tblStylePr by type (firstRow, band1Horz, nwCell…).</summary>
    public IReadOnlyDictionary<string, OpenXmlElement> Conditional { get; init; } = new Dictionary<string, OpenXmlElement>();
}

/// <summary>
/// Resolves effective formatting the way Word layers it: document defaults → table style (with its conditional
/// formats) → paragraph style chain → numbering level → character style chain → direct formatting.
/// </summary>
internal sealed class WordStyles
{
    readonly Dictionary<string, WordStyle> _styles = new(StringComparer.Ordinal);
    readonly OpenXmlElement? _defaultRun;
    readonly OpenXmlElement? _defaultParagraph;
    readonly Dictionary<string, WordRunFormat> _runCache = new(StringComparer.Ordinal);

    public WordStyles(MainDocumentPart main, OfficeTheme theme)
    {
        Theme = theme;
        OpenXmlElement? root = null;
        try
        {
            root = main.StyleDefinitionsPart?.RootElement;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }
        var defaults = root.Child("docDefaults");
        _defaultRun = defaults.Child("rPrDefault").Child("rPr");
        _defaultParagraph = defaults.Child("pPrDefault").Child("pPr");

        foreach (var style in root.Children("style"))
        {
            var id = style.Attr("styleId");
            if (string.IsNullOrEmpty(id) || _styles.ContainsKey(id))
                continue;
            var type = style.Attr("type") ?? "paragraph";
            var conditional = new Dictionary<string, OpenXmlElement>(StringComparer.Ordinal);
            foreach (var part in style.Children("tblStylePr"))
            {
                if (part.Attr("type") is { } kind)
                    conditional[kind] = part;
            }
            _styles[id] = new WordStyle
            {
                Id = id,
                Type = type,
                Name = style.Child("name").Attr("val"),
                BasedOn = style.Child("basedOn").Attr("val"),
                Element = style,
                ParagraphProperties = style.Child("pPr"),
                RunProperties = style.Child("rPr"),
                TableProperties = style.Child("tblPr"),
                RowProperties = style.Child("trPr"),
                CellProperties = style.Child("tcPr"),
                Conditional = conditional,
            };
            if (IsTrue(style.Attr("default")))
            {
                switch (type)
                {
                    case "paragraph":
                        DefaultParagraphStyle ??= id;
                        break;
                    case "character":
                        DefaultCharacterStyle ??= id;
                        break;
                    case "table":
                        DefaultTableStyle ??= id;
                        break;
                }
            }
        }
    }

    public OfficeTheme Theme { get; }

    public string? DefaultParagraphStyle { get; }

    public string? DefaultCharacterStyle { get; }

    public string? DefaultTableStyle { get; }

    public WordStyle? Get(string? id) => id is not null && _styles.TryGetValue(id, out var style) ? style : null;

    /// <summary>The style and its bases, base first. Unknown ids fall back to the default style of that kind.</summary>
    public IReadOnlyList<WordStyle> Chain(string? id, string type)
    {
        var chain = new List<WordStyle>();
        var current = Get(id) ?? Get(type switch
        {
            "paragraph" => DefaultParagraphStyle,
            "table" => DefaultTableStyle,
            _ => null,
        });
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (current is not null && seen.Add(current.Id) && chain.Count < 32)
        {
            chain.Add(current);
            current = Get(current.BasedOn);
        }
        chain.Reverse();
        return chain;
    }

    /// <summary>The paragraph style id a paragraph uses (the default style when it names none or an unknown one).</summary>
    public string? ParagraphStyleId(string? id) => Get(id) is { Type: "paragraph" } ? id : DefaultParagraphStyle;

    public WordParagraphFormat DefaultParagraphFormat()
    {
        var format = new WordParagraphFormat();
        format.Apply(_defaultParagraph, Theme);
        return format;
    }

    /// <summary>Paragraph formatting before numbering and direct formatting: defaults, table layers, style chain.</summary>
    public WordParagraphFormat ParagraphBase(string? styleId, IReadOnlyList<OpenXmlElement> tableLayers)
    {
        var format = DefaultParagraphFormat();
        foreach (var layer in tableLayers)
            format.Apply(layer.Child("pPr"), Theme);
        foreach (var style in Chain(styleId, "paragraph"))
            format.Apply(style.ParagraphProperties, Theme);
        return format;
    }

    /// <summary>
    /// Character formatting of a paragraph's text before character styles and direct formatting: defaults, table
    /// layers, paragraph style chain. Cached by style and table layers.
    /// </summary>
    public WordRunFormat RunBase(string? paragraphStyleId, IReadOnlyList<OpenXmlElement> tableLayers, string tableKey)
    {
        var key = (paragraphStyleId ?? "") + "|" + tableKey;
        if (_runCache.TryGetValue(key, out var cached))
            return cached;
        var format = new WordRunFormat { Font = Theme.MinorLatin, Size = 10 };
        // Word's built-in defaults when docDefaults say nothing: Times New Roman 10 pt.
        if (_defaultRun is null)
            format.Font = "Times New Roman";
        format.Apply(_defaultRun, Theme);
        format.ComplexSize = format.Size;
        foreach (var layer in tableLayers)
            format.Apply(layer.Child("rPr"), Theme);
        foreach (var style in Chain(paragraphStyleId, "paragraph"))
            format.Apply(style.RunProperties, Theme);
        _runCache[key] = format;
        return format;
    }

    /// <summary>A run's effective formatting: <paramref name="paragraphRun"/> plus the character style chain plus direct rPr.</summary>
    public WordRunFormat Run(WordRunFormat paragraphRun, OpenXmlElement? runProperties)
    {
        var format = paragraphRun.Clone();
        if (runProperties is null)
            return format;
        if (runProperties.Child("rStyle").Attr("val") is { } characterStyle)
        {
            foreach (var style in Chain(characterStyle, "character"))
            {
                if (style.Type == "character")
                    format.Apply(style.RunProperties, Theme);
            }
        }
        format.Apply(runProperties, Theme);
        return format;
    }

    /// <summary>Heading level 1–6 for a paragraph style (outline level, or a "heading N" / "Title" name), else 0.</summary>
    public int HeadingLevel(string? styleId, int? outlineLevel)
    {
        if (outlineLevel is >= 0 and <= 5)
            return outlineLevel.Value + 1;
        foreach (var style in Chain(styleId, "paragraph").Reverse())
        {
            var name = (style.Name ?? style.Id).ToLowerInvariant().Replace(" ", "", StringComparison.Ordinal);
            if (name == "title")
                return 1;
            if (name.StartsWith("heading", StringComparison.Ordinal) && name.Length == 8 && name[7] is >= '1' and <= '6')
                return name[7] - '0';
        }
        return 0;
    }
}
