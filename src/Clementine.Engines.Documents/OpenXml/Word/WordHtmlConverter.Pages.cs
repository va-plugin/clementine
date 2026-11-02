using System.Globalization;
using System.Text;
using Clementine.Engines.Documents.Markup;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using static Clementine.Engines.Documents.OpenXml.Ooxml;

namespace Clementine.Engines.Documents.OpenXml.Word;

internal sealed partial class WordHtmlConverter
{
    /// <summary>A section's page: size, margins (pt), columns, break type and header/footer references.</summary>
    sealed class PageSetup
    {
        public double Width { get; init; }

        public double Height { get; init; }

        public double Top { get; init; }

        public double Right { get; init; }

        public double Bottom { get; init; }

        public double Left { get; init; }

        public double HeaderDistance { get; init; }

        public double FooterDistance { get; init; }

        public int Columns { get; init; } = 1;

        public double ColumnGap { get; init; } = 36;

        public string Type { get; init; } = "nextPage";

        public bool TitlePage { get; init; }

        public List<(string Kind, string Id)> HeaderReferences { get; } = [];

        public List<(string Kind, string Id)> FooterReferences { get; } = [];

        public double TextWidth => Math.Max(72, Width - Left - Right);

        public static PageSetup From(OpenXmlElement? section, PageSetup? previous)
        {
            var letter = HtmlPage.PaperSize == "letter";
            var size = section.Child("pgSz");
            var width = Twips(size.Attr("w")) ?? previous?.Width ?? (letter ? 612 : 595.3);
            var height = Twips(size.Attr("h")) ?? previous?.Height ?? (letter ? 792 : 841.9);
            if (size.Attr("orient") == "landscape" && width < height)
                (width, height) = (height, width);
            width = Math.Clamp(width, 72, 2000);
            height = Math.Clamp(height, 72, 2000);

            var margins = section.Child("pgMar");
            var gutter = Math.Max(0, Twips(margins.Attr("gutter")) ?? 0);
            double Margin(string name, double fallback, double limit) => Math.Clamp(Math.Abs(Twips(margins.Attr(name)) ?? fallback), 0, limit);
            var setup = new PageSetup
            {
                Width = width,
                Height = height,
                Top = Margin("top", previous?.Top ?? 72, height / 2 - 18),
                Bottom = Margin("bottom", previous?.Bottom ?? 72, height / 2 - 18),
                Left = Math.Min(Margin("left", previous?.Left ?? 72, width / 2 - 18) + gutter, width / 2 - 18),
                Right = Margin("right", previous?.Right ?? 72, width / 2 - 18),
                HeaderDistance = Margin("header", previous?.HeaderDistance ?? 36, height / 2),
                FooterDistance = Margin("footer", previous?.FooterDistance ?? 36, height / 2),
                Columns = Math.Clamp(section.Child("cols") is { } cols
                    ? (cols.Children("col").Count() is > 1 and var explicitCount ? explicitCount : Integer(cols.Attr("num")) ?? 1)
                    : 1, 1, 6),
                ColumnGap = Math.Clamp(Twips(section.Child("cols").Attr("space")) ?? 36, 0, 144),
                Type = section.Child("type").Attr("val") ?? "nextPage",
                TitlePage = IsOn(section.Child("titlePg")),
            };
            foreach (var reference in section.Children("headerReference"))
            {
                if (reference.Attr("id", R) is { } id)
                    setup.HeaderReferences.Add((reference.Attr("type") ?? "default", id));
            }
            foreach (var reference in section.Children("footerReference"))
            {
                if (reference.Attr("id", R) is { } id)
                    setup.FooterReferences.Add((reference.Attr("type") ?? "default", id));
            }
            return setup;
        }
    }

    static string PageRule(PageSetup setup, string boxes) =>
        $"@page @PAGE@ {{ size: {Pt(setup.Width)} {Pt(setup.Height)}; margin: {Pt(setup.Top)} {Pt(setup.Right)} {Pt(setup.Bottom)} {Pt(setup.Left)}; {boxes}}}\n";

    static readonly string[] AllBoxes = ["@top-left", "@top-center", "@top-right", "@bottom-left", "@bottom-center", "@bottom-right"];

    string MarginBoxes(Dictionary<string, string> references, PageSetup setup) =>
        Boxes(references.GetValueOrDefault("h:default"), top: true, setup) + Boxes(references.GetValueOrDefault("f:default"), top: false, setup);

    /// <summary>The first page of a "different first page" section: its own header/footer, or none.</summary>
    string FirstPageBoxes(Dictionary<string, string> references, PageSetup setup)
    {
        var css = new StringBuilder();
        foreach (var box in AllBoxes)
            css.Append(box).Append(" { content: none; } ");
        css.Append(Boxes(references.GetValueOrDefault("h:first"), top: true, setup));
        css.Append(Boxes(references.GetValueOrDefault("f:first"), top: false, setup));
        return css.ToString();
    }

    OpenXmlPart? HeaderPart(string? id)
    {
        if (id is null)
            return null;
        try
        {
            return _main.GetPartById(id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>On screen (the HTML file) the header and footer sit above and below the text; print uses the page margins.</summary>
    void AppendScreenHeaderFooter(StringBuilder html, string? id, string cssClass, double width)
    {
        if (HeaderPart(id) is not { } part)
            return;
        OpenXmlElement? root;
        try
        {
            root = part.RootElement;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return;
        }
        if (root is null || !root.Descendants().Any(d => d.LocalName is "t" or "drawing" or "pict"))
            return;
        var blocks = _blockCount;
        html.Append("<div class=\"").Append(cssClass).Append(" screen-only\">");
        RenderBlocks(root.ChildElements, html, new Scope(new Story(part), width));
        html.Append("</div>");
        _blockCount = blocks;
    }

    enum PageField
    {
        Page,
        Pages,
    }

    /// <summary>One header line split at its tabs; each part is text and page-number fields.</summary>
    sealed record HeaderLine(List<List<object>> Segments, string Align, List<string> TabAligns, WordRunFormat Format, List<WordRunFormat?> SegmentFormats);

    /// <summary>
    /// A header or footer as CSS margin boxes (left / centre / right, decided by alignment and tab stops). Margin boxes
    /// hold text and page counters only, so pictures in headers are left out of the print (with a warning for a PDF).
    /// </summary>
    string Boxes(string? id, bool top, PageSetup setup)
    {
        if (HeaderPart(id) is not { } part)
            return "";
        OpenXmlElement? root;
        try
        {
            root = part.RootElement;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return "";
        }
        if (root is null)
            return "";
        // (The HTML file shows them, above and below the text on screen: only the printed margins can't.)
        if (_forPrint && root.Descendants().Any(d => d.LocalName is "drawing" or "pict"))
            Warn("Pictures in headers and footers were left out of the PDF.");

        var lines = new List<HeaderLine>();
        foreach (var block in Flatten(root.ChildElements))
        {
            if (block.LocalName == "p")
            {
                lines.Add(HeaderParagraph(block));
            }
            else if (block.LocalName == "tbl")
            {
                foreach (var row in block.Descendants().Where(d => d.LocalName == "tr"))
                {
                    var cells = row.Children("tc").ToList();
                    var segments = new List<List<object>>();
                    var segmentFormats = new List<WordRunFormat?>();
                    WordRunFormat? format = null;
                    var align = "left";
                    foreach (var cell in cells)
                    {
                        var parts = new List<object>();
                        WordRunFormat? cellFormat = null;
                        foreach (var paragraph in cell.Descendants().Where(d => d.LocalName == "p"))
                        {
                            var line = HeaderParagraph(paragraph);
                            format ??= line.Format;
                            cellFormat ??= line.SegmentFormats.FirstOrDefault(f => f is not null);
                            if (cells.Count == 1)
                                align = line.Align;
                            if (parts.Count > 0)
                                parts.Add("\n");
                            parts.AddRange(line.Segments.SelectMany((s, i) => i == 0 ? s : s.Prepend("  ")));
                        }
                        segments.Add(parts);
                        segmentFormats.Add(cellFormat);
                    }
                    if (segments.Count > 3)
                    {
                        segments = [segments[0], [.. segments.Skip(1).Take(segments.Count - 2).SelectMany((s, i) => i == 0 ? s : s.Prepend("  "))], segments[^1]];
                        segmentFormats = [segmentFormats[0], segmentFormats[1], segmentFormats[^1]];
                    }
                    var aligns = segments.Count switch
                    {
                        2 => new List<string> { "right" },
                        3 => ["center", "right"],
                        _ => [],
                    };
                    lines.Add(new HeaderLine(segments, align, aligns, format ?? _styles.RunBase(_styles.DefaultParagraphStyle, [], ""), segmentFormats));
                }
            }
        }

        // Trim empty lines at either end; keep the ones in between.
        static bool IsEmpty(HeaderLine line) => line.Segments.All(s => s.All(t => t is string text && string.IsNullOrWhiteSpace(text)));
        while (lines.Count > 0 && IsEmpty(lines[0]))
            lines.RemoveAt(0);
        while (lines.Count > 0 && IsEmpty(lines[^1]))
            lines.RemoveAt(lines.Count - 1);
        if (lines.Count == 0)
            return "";

        var positions = new Dictionary<string, (List<List<object>> Lines, WordRunFormat Format)>(StringComparer.Ordinal);
        void Put(string position, List<object> content, WordRunFormat format)
        {
            if (!positions.TryGetValue(position, out var box))
            {
                box = ([], format);
                positions[position] = box;
            }
            box.Lines.Add(content);
        }
        foreach (var line in lines)
        {
            if (IsEmpty(line))
            {
                foreach (var box in positions.Values)
                    box.Lines.Add([]);
                continue;
            }
            for (var i = 0; i < line.Segments.Count; i++)
            {
                var segment = line.Segments[i];
                if (segment.All(t => t is string text && text.Length == 0))
                    continue;
                var position = i == 0
                    ? (line.Segments.Count == 1 ? line.Align switch { "center" => "center", "right" or "end" => "right", _ => "left" } : "left")
                    : (i - 1 < line.TabAligns.Count ? line.TabAligns[i - 1] switch { "center" => "center", "right" or "end" => "right", _ => "left" } : "right");
                Put(position, segment, (i < line.SegmentFormats.Count ? line.SegmentFormats[i] : null) ?? line.Format);
            }
        }

        var css = new StringBuilder();
        var edge = top ? "top" : "bottom";
        var available = top ? setup.Top : setup.Bottom;
        var distance = top ? setup.HeaderDistance : setup.FooterDistance;
        foreach (var (position, (boxLines, format)) in positions)
        {
            var content = new StringBuilder();
            for (var l = 0; l < boxLines.Count; l++)
            {
                if (l > 0)
                    content.Append(" \"\\A\" ");
                foreach (var token in boxLines[l])
                {
                    content.Append(' ').Append(token switch
                    {
                        PageField.Page => "counter(page)",
                        PageField.Pages => "counter(pages)",
                        _ => CssString(token.ToString() ?? ""),
                    });
                }
            }
            if (content.Length == 0)
                continue;
            var lineHeight = format.Size * 1.2 * boxLines.Count;
            var padding = Math.Clamp(distance, 0, Math.Max(0, available - lineHeight));
            css.Append('@').Append(edge).Append('-').Append(position).Append(" { content:").Append(content).Append("; ")
                .Append("font-family: ").Append(FontStack(format)).Append("; font-size: ").Append(Pt(format.Size)).Append("; ")
                .Append("font-weight: ").Append(format.Bold ? 700 : 400).Append("; font-style: ").Append(format.Italic ? "italic" : "normal").Append("; ")
                .Append("color: ").Append(format.Color ?? "#000000").Append("; white-space: pre; line-height: 1.2; ")
                .Append("vertical-align: ").Append(edge).Append("; padding-").Append(edge).Append(": ").Append(Pt(padding)).Append("; } ");
        }
        return css.ToString();
    }

    /// <summary>A header paragraph's text as tab-separated segments, with PAGE / NUMPAGES fields as counters.</summary>
    HeaderLine HeaderParagraph(OpenXmlElement paragraph)
    {
        var properties = paragraph.Child("pPr");
        var styleId = _styles.ParagraphStyleId(properties.Child("pStyle").Attr("val"));
        var format = _styles.ParagraphBase(styleId, []);
        format.Apply(properties, _theme);
        var runBase = _styles.RunBase(styleId, [], "");
        WordRunFormat? first = null;
        var segments = new List<List<object>> { new() };
        var segmentFormats = new List<WordRunFormat?> { null };
        var fields = new List<(StringBuilder Instruction, bool Result, bool Skip)>();

        void AddText(string text)
        {
            if (text.Length == 0)
                return;
            if (segments[^1].Count > 0 && segments[^1][^1] is string previous)
                segments[^1][^1] = previous + text;
            else
                segments[^1].Add(text);
        }

        PageField? FieldKind(string instruction)
        {
            var word = instruction.Trim().Split(' ', 2)[0].ToUpperInvariant();
            return word switch
            {
                "PAGE" => PageField.Page,
                "NUMPAGES" or "SECTIONPAGES" => PageField.Pages,
                _ => null,
            };
        }

        void Walk(OpenXmlElement element)
        {
            foreach (var child in element.ChildElements)
            {
                switch (child.LocalName)
                {
                    case "r":
                        var runFormat = _styles.Run(runBase, child.Child("rPr"));
                        foreach (var item in child.ChildElements)
                        {
                            var hidden = fields.Any(f => !f.Result || f.Skip) || runFormat.Hidden;
                            switch (item.LocalName)
                            {
                                case "fldChar":
                                    switch (item.Attr("fldCharType"))
                                    {
                                        case "begin":
                                            fields.Add((new StringBuilder(), false, false));
                                            break;
                                        case "separate" when fields.Count > 0:
                                            var kind = FieldKind(fields[^1].Instruction.ToString());
                                            if (kind is not null && !fields.Take(fields.Count - 1).Any(f => !f.Result || f.Skip))
                                                segments[^1].Add(kind.Value);
                                            fields[^1] = (fields[^1].Instruction, true, kind is not null);
                                            break;
                                        case "end" when fields.Count > 0:
                                            // A field without a separate (no cached result) still shows its value.
                                            if (!fields[^1].Result && FieldKind(fields[^1].Instruction.ToString()) is { } pending
                                                && !fields.Take(fields.Count - 1).Any(f => !f.Result || f.Skip))
                                                segments[^1].Add(pending);
                                            fields.RemoveAt(fields.Count - 1);
                                            break;
                                    }
                                    break;
                                case "instrText" when fields.Count > 0 && !fields[^1].Result:
                                    fields[^1].Instruction.Append(item.InnerText);
                                    break;
                                case "t" when !hidden:
                                    first ??= runFormat;
                                    if (item.InnerText.Trim().Length > 0)
                                        segmentFormats[^1] ??= runFormat;
                                    AddText(item.InnerText);
                                    break;
                                case "tab" or "ptab" when !hidden:
                                    segments.Add([]);
                                    segmentFormats.Add(null);
                                    break;
                                case "br" or "cr" when !hidden:
                                    AddText("\n");
                                    break;
                                case "noBreakHyphen" when !hidden:
                                    AddText("\u2011");
                                    break;
                                case "sym" when !hidden:
                                    if (item.Attr("char") is { } code && int.TryParse(code, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value) && value is > 0 and < 0xFFFF)
                                        AddText(WordNumbering.SymbolCharacter((char)value, item.Attr("font")));
                                    break;
                            }
                        }
                        break;
                    case "fldSimple":
                        if (FieldKind(child.Attr("instr") ?? "") is { } simple)
                        {
                            first ??= _styles.Run(runBase, child.Find("rPr"));
                            segments[^1].Add(simple);
                        }
                        else
                        {
                            Walk(child);
                        }
                        break;
                    case "hyperlink" or "smartTag" or "ins" or "customXml" or "moveTo" or "dir" or "bdo":
                        Walk(child);
                        break;
                    case "sdt":
                        if (child.Child("sdtContent") is { } content)
                            Walk(content);
                        break;
                }
            }
        }

        Walk(paragraph);
        return new HeaderLine(segments, format.Align, format.Tabs.Select(t => t.Align).ToList(), first ?? runBase, segmentFormats);
    }
}
