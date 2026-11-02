using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using static Clementine.Engines.Documents.OpenXml.Ooxml;

namespace Clementine.Engines.Documents.OpenXml.Word;

/// <summary>A Word document as one self-contained HTML page (pictures embedded), ready to print or to open.</summary>
internal sealed record WordHtml(string Html, string Title, IReadOnlyList<string> Warnings);

/// <summary>
/// Clementine's built-in Word → HTML converter, written for printing with Edge: page size, margins, headers and footers
/// become CSS paged media (@page with margin boxes); paragraphs, lists, tables and pictures keep Word's formatting as
/// closely as HTML allows. It never fetches anything: linked (external) pictures are skipped and every picture is
/// embedded as a data: URI. Complex layouts (floating shapes, text wrapping, charts) are approximations.
/// </summary>
internal sealed partial class WordHtmlConverter
{
    /// <summary>Stop embedding pictures past this much image data (the page must stay printable).</summary>
    const long MaxImageBytes = 120L * 1024 * 1024;

    /// <summary>Footnotes (and endnotes) listed at most: a crafted file can't make the list grow without end.</summary>
    const int MaxNotes = 10_000;

    readonly WordprocessingDocument _document;
    readonly MainDocumentPart _main;
    readonly bool _forPrint;
    readonly OfficeTheme _theme;
    readonly WordStyles _styles;
    readonly WordNumbering _numbering;
    readonly CssClasses _css = new();
    readonly List<string> _warnings = [];
    readonly CancellationToken _cancellationToken;
    readonly Dictionary<string, string> _fontFamilies = new(StringComparer.OrdinalIgnoreCase);
    readonly List<(int Number, OpenXmlElement Note)> _footnotes = [];
    readonly List<(int Number, OpenXmlElement Note)> _endnotes = [];
    /// <summary>Note id → its number in the list, so a note referenced again (or by itself) is listed once.</summary>
    readonly Dictionary<int, int> _footnoteNumbers = [];
    readonly Dictionary<int, int> _endnoteNumbers = [];
    readonly HashSet<string> _anchors = new(StringComparer.Ordinal);
    readonly double _defaultTab = 36;
    int _blockCount;

    WordHtmlConverter(WordprocessingDocument document, bool forPrint, CancellationToken cancellationToken)
    {
        _document = document;
        _main = document.MainDocumentPart ?? throw new OperationException("This Word document has no content.");
        _forPrint = forPrint;
        _cancellationToken = cancellationToken;
        _theme = OfficeTheme.From(_main.ThemePart);
        _styles = new WordStyles(_main, _theme);
        _numbering = new WordNumbering(_main, _styles);
        try
        {
            var settings = _main.DocumentSettingsPart?.RootElement;
            if (Twips(settings.Child("defaultTabStop").Attr("val")) is { } tab and > 4)
                _defaultTab = tab;
            foreach (var font in _main.FontTablePart?.RootElement?.Children("font") ?? [])
            {
                var name = font.Attr("name");
                var family = font.Child("family").Attr("val");
                if (name is not null && family is not null)
                {
                    _fontFamilies[name] = family switch
                    {
                        "roman" => "serif",
                        "modern" => "monospace",
                        "script" => "cursive",
                        _ => "sans-serif",
                    };
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Settings and the font table only refine the result.
        }
    }

    /// <summary>
    /// Converts the Word document at <paramref name="path"/>. <paramref name="forPrint"/>: the page is about to be printed
    /// to PDF (warnings then speak about the PDF, e.g. header pictures that only the printed margins can't show).
    /// </summary>
    public static WordHtml Convert(string path, string fallbackTitle, bool forPrint, CancellationToken cancellationToken)
    {
        using var document = OpenXmlFiles.OpenWord(path);
        try
        {
            return new WordHtmlConverter(document, forPrint, cancellationToken).Run(fallbackTitle);
        }
        catch (Exception ex) when (ex is not (OperationException or OperationCanceledException))
        {
            // A damaged part, a workbook renamed to .docx, a theme with nonsense colours: never the SDK's own message.
            throw new OperationException($"Couldn't read \"{Path.GetFileName(path)}\" — it's damaged or isn't really a Word document.", ex.ToString(), ex);
        }
    }

    WordHtml Run(string fallbackTitle)
    {
        OpenXmlElement body;
        try
        {
            body = _main.Document?.Body ?? throw new OperationException("This Word document has no body text.");
        }
        catch (OperationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new OperationException("This Word document is damaged and can't be read.", ex.ToString(), ex);
        }

        var title = _document.PackageProperties.Title is { Length: > 0 } documentTitle ? documentTitle : fallbackTitle;
        var sections = Sections(body);
        var bodyHtml = new StringBuilder();
        var pageCss = new StringBuilder();
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        string? previousPage = null;
        string? previousRule = null;
        PageSetup? previousSetup = null;
        for (var i = 0; i < sections.Count; i++)
        {
            var (sectionProperties, blocks) = sections[i];
            var setup = PageSetup.From(sectionProperties, previousSetup);
            foreach (var (kind, id) in setup.HeaderReferences)
                headers["h:" + kind] = id;
            foreach (var (kind, id) in setup.FooterReferences)
                headers["f:" + kind] = id;
            var pageRule = PageRule(setup, MarginBoxes(headers, setup));
            // A continuous section on the same page setup shares the previous section's pages (no page break).
            var pageName = setup.Type == "continuous" && previousPage is not null && pageRule == previousRule
                ? previousPage
                : "s" + (i + 1).ToString(CultureInfo.InvariantCulture);
            var sectionClass = _css.For("s", $"page: {pageName};");
            if (pageName != previousPage)
            {
                pageCss.Append(pageRule.Replace("@PAGE@", pageName, StringComparison.Ordinal));
                pageCss.Append("@media screen { section.").Append(sectionClass).Append(" { width: ").Append(Pt(setup.Width))
                    .Append("; min-height: ").Append(Pt(setup.Height)).Append("; padding: ").Append(Pt(setup.Top)).Append(' ')
                    .Append(Pt(setup.Right)).Append(' ').Append(Pt(setup.Bottom)).Append(' ').Append(Pt(setup.Left)).Append("; } }\n");
                if (setup.TitlePage)
                    pageCss.Append("@page ").Append(pageName).Append(":first { ").Append(FirstPageBoxes(headers, setup)).Append(" }\n");
            }
            if (i == 0)
                pageCss.Append(pageRule.Replace(" @PAGE@", "", StringComparison.Ordinal));

            var story = new Story(_main);
            bodyHtml.Append("<section class=\"page ").Append(sectionClass).Append("\">");
            AppendScreenHeaderFooter(bodyHtml, headers.GetValueOrDefault("h:default"), "doc-header", setup.TextWidth);
            var columns = setup.Columns > 1;
            if (columns)
                bodyHtml.Append("<div class=\"").Append(_css.For("c", $"columns: {setup.Columns}; column-gap: {Pt(setup.ColumnGap)};")).Append("\">");
            RenderBlocks(blocks, bodyHtml, new Scope(story, setup.TextWidth));
            if (columns)
                bodyHtml.Append("</div>");
            if (i == sections.Count - 1)
                AppendNotes(bodyHtml, setup.TextWidth);
            AppendScreenHeaderFooter(bodyHtml, headers.GetValueOrDefault("f:default"), "doc-footer", setup.TextWidth);
            bodyHtml.Append("</section>\n");
            previousPage = pageName;
            previousRule = pageRule;
            previousSetup = setup;
        }
        if (_blockCount == 0)
            throw new OperationException("This Word document is empty.");

        var defaults = _styles.RunBase(_styles.DefaultParagraphStyle, [], "");
        var html = new StringBuilder(bodyHtml.Length + 8000);
        html.Append("<!DOCTYPE html>\n<html>\n<head>\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        html.Append("<meta name=\"generator\" content=\"Clementine\">\n<title>").Append(Html(title)).Append("</title>\n<style>\n");
        html.Append(BaseCss.Replace("@FONT@", FontStack(defaults), StringComparison.Ordinal)
            .Replace("@SIZE@", Pt(defaults.Size), StringComparison.Ordinal)
            .Replace("@TAB@", Pt(_defaultTab), StringComparison.Ordinal));
        html.Append(pageCss);
        html.Append(_css.Rules);
        html.Append("</style>\n</head>\n<body>\n");
        html.Append(bodyHtml);
        html.Append("</body>\n</html>\n");
        return new WordHtml(html.ToString(), title, _warnings);
    }

    /// <summary>Body content split at each section's end (a paragraph whose pPr holds a sectPr, then the body's final sectPr).</summary>
    static List<(OpenXmlElement? Properties, List<OpenXmlElement> Blocks)> Sections(OpenXmlElement body)
    {
        var sections = new List<(OpenXmlElement?, List<OpenXmlElement>)>();
        var current = new List<OpenXmlElement>();
        OpenXmlElement? final = null;
        foreach (var child in body.ChildElements)
        {
            if (child.LocalName == "sectPr")
            {
                final = child;
                continue;
            }
            current.Add(child);
            if (child.LocalName == "p" && child.Child("pPr").Child("sectPr") is { } properties)
            {
                sections.Add((properties, current));
                current = [];
            }
        }
        if (current.Count > 0 || sections.Count == 0)
            sections.Add((final, current));
        else
            sections[^1] = (final ?? sections[^1].Item1, sections[^1].Item2);
        return sections;
    }

    void Warn(string message)
    {
        if (!_warnings.Contains(message))
            _warnings.Add(message);
    }

    // Blocks ----------------------------------------------------------------------------------------------------------

    /// <summary>Where blocks are being written: the part (for relationships), field state and the available width.</summary>
    sealed class Scope(Story story, double width)
    {
        public Story Story { get; } = story;

        /// <summary>Width of the text column (pt).</summary>
        public double Width { get; } = width;

        /// <summary>Table style layers (w:style and w:tblStylePr elements) whose pPr/rPr apply to paragraphs in a cell.</summary>
        public IReadOnlyList<OpenXmlElement> TableLayers { get; init; } = [];

        public string TableKey { get; init; } = "";
    }

    /// <summary>One text flow (body, a header, the footnotes) with its relationships and open fields.</summary>
    sealed class Story(OpenXmlPart part)
    {
        public OpenXmlPart Part { get; } = part;

        public List<FieldFrame> Fields { get; } = [];
    }

    sealed class FieldFrame
    {
        public StringBuilder Instruction { get; } = new();

        public bool InResult { get; set; }

        public string? Href { get; set; }

        public bool LinkOpen { get; set; }
    }

    /// <summary>Block-level content with sdt / customXml / tracked insertions unwrapped and deletions dropped.</summary>
    static IEnumerable<OpenXmlElement> Flatten(IEnumerable<OpenXmlElement> blocks)
    {
        foreach (var block in blocks)
        {
            switch (block.LocalName)
            {
                case "p" or "tbl" or "altChunk":
                    yield return block;
                    break;
                case "sdt":
                    foreach (var inner in Flatten(block.Child("sdtContent")?.ChildElements ?? (IEnumerable<OpenXmlElement>)[]))
                        yield return inner;
                    break;
                case "customXml" or "ins" or "moveTo" or "smartTag":
                    foreach (var inner in Flatten(block.ChildElements))
                        yield return inner;
                    break;
                case "AlternateContent":
                    if (block.Child("Fallback") is { } fallback)
                    {
                        foreach (var inner in Flatten(fallback.ChildElements))
                            yield return inner;
                    }
                    break;
            }
        }
    }

    void RenderBlocks(IEnumerable<OpenXmlElement> blocks, StringBuilder html, Scope scope)
    {
        var items = Flatten(blocks).ToList();
        var lists = new ListWriter(html);
        for (var i = 0; i < items.Count; i++)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var block = items[i];
            _blockCount++;
            switch (block.LocalName)
            {
                case "p":
                    RenderParagraph(block, i > 0 ? items[i - 1] : null, i + 1 < items.Count ? items[i + 1] : null, html, scope, lists);
                    break;
                case "tbl":
                    lists.CloseAll();
                    RenderTable(block, html, scope);
                    break;
                case "altChunk":
                    Warn("Parts of this document embedded from other files were left out.");
                    break;
            }
        }
        lists.CloseAll();
    }

    string? ParagraphStyleOf(OpenXmlElement? paragraph) =>
        paragraph?.LocalName == "p" ? _styles.ParagraphStyleId(paragraph.Child("pPr").Child("pStyle").Attr("val")) : null;

    void RenderParagraph(OpenXmlElement paragraph, OpenXmlElement? previous, OpenXmlElement? next, StringBuilder html, Scope scope, ListWriter lists)
    {
        var properties = paragraph.Child("pPr");
        var styleId = _styles.ParagraphStyleId(properties.Child("pStyle").Attr("val"));
        var format = _styles.ParagraphBase(styleId, scope.TableLayers);

        // Numbering: direct numPr wins over the style's; numId 0 switches numbering off.
        var directNumbering = properties.Child("numPr");
        var numId = Integer(directNumbering.Child("numId").Attr("val")) ?? format.NumId ?? 0;
        var level = Integer(directNumbering.Child("ilvl").Attr("val"))
            ?? (directNumbering.Child("numId") is null ? format.Level : null)
            ?? (numId > 0 ? _numbering.LevelForStyle(numId, styleId) : 0);
        level = Math.Clamp(level, 0, 8);
        var listLevel = numId > 0 ? _numbering.Level(numId, level) : null;
        if (listLevel is not null)
            format.Apply(listLevel.ParagraphProperties, _theme);
        format.Apply(properties, _theme);

        var runBase = _styles.RunBase(styleId, scope.TableLayers, scope.TableKey);
        var markFormat = _styles.Run(runBase, properties.Child("rPr"));
        if (markFormat.Hidden && !HasVisibleText(paragraph))
            return;

        var label = listLevel is not null ? _numbering.Next(numId, level) : null;
        var heading = _styles.HeadingLevel(styleId, format.OutlineLevel);

        // "Don't add space between paragraphs of the same style" (List Paragraph uses it).
        var before = format.BeforeAuto ? 14 : format.Before;
        var after = format.AfterAuto ? 14 : format.After;
        if (format.ContextualSpacing)
        {
            if (ParagraphStyleOf(previous) == styleId)
                before = 0;
            if (ParagraphStyleOf(next) == styleId)
                after = 0;
        }

        var writer = new InlineWriter { SplitTabs = format.Tabs.Count > 0 && label is null };
        OpenFieldLinks(writer, scope.Story);
        RenderInlines(paragraph.ChildElements, writer, scope, runBase);
        CloseFieldLinks(writer, scope.Story);
        DropUnfinishedFields(scope.Story);
        var segments = writer.Finish();

        var tag = heading > 0 ? $"h{heading}" : "p";
        var isListItem = label is not null && heading == 0;
        var css = ParagraphCss(format, runBase, before, after, isListItem, writer.NeedsPositioning);
        string content;
        var flexCss = "";
        var flex = segments.Count > 1 ? TabLayout(segments, format, scope, out flexCss) : null;
        if (flex is not null)
        {
            content = flex;
            css += flexCss;
        }
        else
        {
            content = string.Join("\t", segments);
        }
        if (!writer.HasContent)
            content += "<br>";

        var marker = label is null ? "" : MarkerHtml(label, format, runBase, properties.Child("rPr"));
        var direction = format.Bidi ? " dir=\"rtl\"" : "";

        if (isListItem)
        {
            if (writer.Before.Length > 0)
                html.Append(writer.Before);
            var tagName = label!.Level.IsBullet ? "ul" : "ol";
            var margin = lists.Item(label.Depth, tagName, format.Left);
            html.Append("<li class=\"").Append(_css.For("p", css)).Append('"').Append(direction)
                .Append(" style=\"margin-left:").Append(Pt(margin)).Append("\">")
                .Append(marker).Append(content);
            return;
        }

        lists.CloseAll();
        if (writer.Before.Length > 0)
            html.Append(writer.Before);
        html.Append('<').Append(tag).Append(" class=\"").Append(_css.For("p", css)).Append('"').Append(direction).Append('>')
            .Append(marker).Append(content).Append("</").Append(tag).Append(">\n");
    }

    static bool HasVisibleText(OpenXmlElement paragraph) =>
        paragraph.Descendants().Any(d => d.LocalName is "t" or "drawing" or "pict" && d.Ancestors().All(a => a.LocalName != "del"))
        && paragraph.Descendants().Where(d => d.LocalName == "r").Any(r => !IsOn(r.Child("rPr").Child("vanish")));

    string ParagraphCss(WordParagraphFormat format, WordRunFormat run, double before, double after, bool listItem, bool positioned)
    {
        var css = new StringBuilder();
        var align = format.Align switch
        {
            "center" => "center",
            "right" or "end" => format.Bidi ? "left" : "right",
            "both" or "distribute" or "lowKashida" or "mediumKashida" or "highKashida" or "thaiDistribute" => "justify",
            "start" or "left" => format.Bidi ? "right" : "left",
            _ => "left",
        };
        css.Append("text-align: ").Append(align).Append("; ");
        if (format.Align == "distribute")
            css.Append("text-align-last: justify; ");
        if (!listItem && format.Left != 0)
            css.Append("margin-left: ").Append(Pt(format.Left)).Append("; ");
        if (format.Right != 0)
            css.Append("margin-right: ").Append(Pt(format.Right)).Append("; ");
        if (format.FirstLine != 0)
            css.Append("text-indent: ").Append(Pt(format.FirstLine)).Append("; ");

        var boxed = format.Top?.IsVisible == true || format.Bottom?.IsVisible == true || format.LeftBorder?.IsVisible == true
            || format.RightBorder?.IsVisible == true || format.Shading is not null;
        var spacing = boxed ? "margin" : "padding";
        if (before > 0)
            css.Append(spacing).Append("-top: ").Append(Pt(before)).Append("; ");
        if (after > 0)
            css.Append(spacing).Append("-bottom: ").Append(Pt(after)).Append("; ");

        css.Append("line-height: ").Append(LineHeight(format, run)).Append("; ");
        css.Append(FontCss(run));
        if (format.Shading is not null)
            css.Append("background-color: ").Append(format.Shading).Append("; ");
        AppendBorder(css, "top", format.Top);
        AppendBorder(css, "bottom", format.Bottom);
        AppendBorder(css, "left", format.LeftBorder);
        AppendBorder(css, "right", format.RightBorder);
        if (format.PageBreakBefore)
            css.Append("break-before: page; ");
        if (format.KeepNext)
            css.Append("break-after: avoid; ");
        if (format.KeepLines)
            css.Append("break-inside: avoid; ");
        if (positioned)
            css.Append("position: relative; ");
        return css.ToString().TrimEnd();
    }

    static void AppendBorder(StringBuilder css, string side, WordBorder? border)
    {
        if (border is not { IsVisible: true })
            return;
        css.Append("border-").Append(side).Append(": ").Append(border.Css).Append("; ");
        css.Append("padding-").Append(side).Append(": ").Append(Pt(Math.Clamp(border.Space, 0, 31))).Append("; ");
    }

    static string LineHeight(WordParagraphFormat format, WordRunFormat run) => format.LineRule switch
    {
        "exact" => Pt(format.Line),
        "atLeast" => format.Line > run.Size * WordFonts.SingleLine(run.Font) ? Pt(format.Line) : Css(WordFonts.SingleLine(run.Font)),
        _ => Css(Math.Clamp(format.Line, 0.5, 10) * WordFonts.SingleLine(run.Font)),
    };

    /// <summary>The full font declaration of a paragraph (every property set, so nothing leaks in from a parent list item).</summary>
    string FontCss(WordRunFormat run) =>
        $"font-family: {FontStack(run)}; font-size: {Pt(run.Size)}; font-weight: {(run.Bold ? 700 : 400)}; font-style: {(run.Italic ? "italic" : "normal")}; color: {run.Color ?? "#000000"}; ";

    string FontStack(WordRunFormat run) =>
        WordFonts.Stack(run.Font, run.EastAsiaFont, run.Font is not null && _fontFamilies.TryGetValue(run.Font, out var generic) ? generic : null);

    /// <summary>The list number or bullet, in its own box as wide as the hanging indent.</summary>
    string MarkerHtml(WordListLabel label, WordParagraphFormat format, WordRunFormat runBase, OpenXmlElement? markProperties)
    {
        var markerFormat = _styles.Run(runBase, markProperties);
        markerFormat.Underline = null;
        markerFormat.Strike = false;
        markerFormat.Shading = null;
        markerFormat.Highlight = null;
        markerFormat.Apply(label.Level.RunProperties, _theme);
        if (label.Level.IsBullet && WordNumbering.IsSymbolFont(markerFormat.Font))
            markerFormat.Font = runBase.Font;
        var css = new StringBuilder(RunCss(markerFormat, runBase));
        double width;
        if (format.FirstLine < 0)
        {
            width = -format.FirstLine;
        }
        else
        {
            var start = format.Left + format.FirstLine;
            width = _defaultTab - start % _defaultTab;
            if (width < 6)
                width += _defaultTab;
        }
        var text = label.Text;
        switch (label.Level.Suffix)
        {
            case "space":
                text += " ";
                width = 0;
                break;
            case "nothing":
                width = 0;
                break;
        }
        if (width > 0)
            css.Append("min-width: ").Append(Pt(width)).Append("; ");
        if (label.Level.Justification is "right" or "end" && width > 0)
            css.Append("text-align: right; padding-right: 3pt; box-sizing: border-box; ");
        else if (label.Level.Justification == "center" && width > 0)
            css.Append("text-align: center; ");
        else
            css.Append("text-align: left; ");
        if (label.Level.IsBullet || label.Text.Length > 0 || width > 0)
            return $"<span class=\"mk {_css.For("m", css.ToString().TrimEnd())}\">{Html(text)}</span>";
        return "";
    }

    // Inline content --------------------------------------------------------------------------------------------------

    void RenderInlines(IEnumerable<OpenXmlElement> children, InlineWriter writer, Scope scope, WordRunFormat runBase)
    {
        foreach (var child in children)
        {
            switch (child.LocalName)
            {
                case "r":
                    RenderRun(child, writer, scope, runBase);
                    break;
                case "hyperlink":
                    var href = HyperlinkTarget(child, scope.Story.Part);
                    if (href is not null)
                        writer.Open($"<a href=\"{Html(href)}\">", "</a>");
                    RenderInlines(child.ChildElements, writer, scope, runBase);
                    if (href is not null)
                        writer.Close();
                    break;
                case "fldSimple":
                    var instruction = child.Attr("instr") ?? "";
                    var link = FieldHyperlink(instruction);
                    if (link is not null)
                        writer.Open($"<a href=\"{Html(link)}\">", "</a>");
                    RenderInlines(child.ChildElements, writer, scope, runBase);
                    if (link is not null)
                        writer.Close();
                    break;
                case "sdt":
                    RenderInlines(child.Child("sdtContent")?.ChildElements ?? (IEnumerable<OpenXmlElement>)[], writer, scope, runBase);
                    break;
                case "smartTag" or "customXml" or "ins" or "moveTo" or "dir" or "bdo":
                    RenderInlines(child.ChildElements, writer, scope, runBase);
                    break;
                case "bookmarkStart":
                    var name = child.Attr("name");
                    if (name is { Length: > 0 } && name != "_GoBack" && _anchors.Add(name))
                        writer.Raw($"<a id=\"{Html(name)}\"></a>", visible: false);
                    break;
                case "oMathPara" or "oMath":
                    var math = string.Concat(child.Descendants().Where(d => d.LocalName == "t").Select(t => t.InnerText));
                    if (math.Length > 0)
                    {
                        var css = RunCss(runBase, runBase) + "font-family: \"Cambria Math\", \"Cambria\", serif; font-style: italic;";
                        var open = $"<span class=\"{_css.For("r", css)}\">";
                        writer.Text(math, open, "</span>", open);
                    }
                    break;
                case "AlternateContent":
                    if (Ooxml.PickAlternate(child, c => c.Descendants().Any(d => d.LocalName is "pic" or "txbx" or "r")) is { } chosen)
                        RenderInlines(chosen.ChildElements, writer, scope, runBase);
                    break;
            }
        }
    }

    void RenderRun(OpenXmlElement run, InlineWriter writer, Scope scope, WordRunFormat runBase)
    {
        var format = _styles.Run(runBase, run.Child("rPr"));
        var fields = scope.Story.Fields;
        string? spanOpen = null;
        string spanClose = "";

        foreach (var child in run.ChildElements)
        {
            var inInstruction = fields.Count > 0 && fields.Any(f => !f.InResult);
            switch (child.LocalName)
            {
                case "fldChar":
                    FieldChar(child.Attr("fldCharType"), writer, scope.Story);
                    continue;
                case "instrText":
                    if (fields.Count > 0 && !fields[^1].InResult)
                        fields[^1].Instruction.Append(child.InnerText);
                    continue;
            }
            if (inInstruction || format.Hidden)
                continue;
            switch (child.LocalName)
            {
                case "t":
                    EnsureSpan();
                    writer.Text(child.InnerText, spanOpen!, spanClose, spanOpen!);
                    break;
                case "tab" or "ptab":
                    writer.Tab();
                    break;
                case "br":
                    var type = child.Attr("type");
                    writer.Raw(type switch
                    {
                        "page" => "<span class=\"pb\"></span>",
                        "column" => "<span class=\"cb\"></span>",
                        _ => "<br>",
                    }, visible: type is null or "textWrapping");
                    break;
                case "cr":
                    writer.Raw("<br>", visible: true);
                    break;
                case "noBreakHyphen":
                    EnsureSpan();
                    writer.Text("‑", spanOpen!, spanClose, spanOpen!);
                    break;
                case "softHyphen":
                    EnsureSpan();
                    writer.Text("­", spanOpen!, spanClose, spanOpen!);
                    break;
                case "sym":
                    if (child.Attr("char") is { } code && int.TryParse(code, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value) && value is > 0 and < 0xFFFF)
                    {
                        EnsureSpan();
                        writer.Text(WordNumbering.SymbolCharacter((char)value, child.Attr("font")), spanOpen!, spanClose, spanOpen!);
                    }
                    break;
                case "drawing":
                    RenderDrawing(child, writer, scope);
                    break;
                case "pict" or "object":
                    RenderVml(child, writer, scope);
                    break;
                case "AlternateContent":
                    if (Ooxml.PickAlternate(child, c => c.Descendants().Any(d => d.LocalName is "pic" or "txbx")) is { } chosen)
                    {
                        foreach (var inner in chosen.ChildElements)
                        {
                            if (inner.LocalName == "drawing")
                                RenderDrawing(inner, writer, scope);
                            else if (inner.LocalName is "pict" or "object")
                                RenderVml(inner, writer, scope);
                        }
                    }
                    break;
                case "footnoteReference" or "endnoteReference":
                    var footnote = child.LocalName == "footnoteReference";
                    var notes = footnote ? _footnotes : _endnotes;
                    var numbers = footnote ? _footnoteNumbers : _endnoteNumbers;
                    var prefix = footnote ? "fn" : "en";
                    if (Integer(child.Attr("id")) is not { } id)
                        break;
                    // A note referenced again — or from inside itself, or in a cycle of notes — is listed only once.
                    var again = numbers.TryGetValue(id, out var number);
                    if (!again)
                    {
                        if (notes.Count >= MaxNotes || FindNote(footnote, id) is not { } note)
                            break;
                        number = notes.Count + 1;
                        notes.Add((number, note));
                        numbers[id] = number;
                    }
                    var mark = prefix == "fn" ? number.ToString(CultureInfo.InvariantCulture) : WordNumbering.FormatNumber(number, "lowerRoman");
                    var noteFormat = format.Clone();
                    noteFormat.VerticalAlign = null;
                    var noteCss = RunCss(noteFormat, runBase);
                    var anchor = again ? "" : $" id=\"{prefix}ref{number}\"";
                    writer.Raw($"<sup class=\"nr\"><a href=\"#{prefix}{number}\"{anchor}{(noteCss.Length > 0 ? $" class=\"{_css.For("r", noteCss)}\"" : "")}>{mark}</a></sup>", visible: true);
                    break;
            }
        }

        void EnsureSpan()
        {
            if (spanOpen is not null)
                return;
            var css = RunCss(format, runBase);
            var classAttribute = css.Length > 0 ? $" class=\"{_css.For("r", css)}\"" : "";
            spanOpen = $"<span{classAttribute}>";
            spanClose = "</span>";
            if (format.VerticalAlign is "superscript" or "subscript")
            {
                var tag = format.VerticalAlign == "superscript" ? "sup" : "sub";
                spanOpen += $"<{tag}>";
                spanClose = $"</{tag}></span>";
            }
        }
    }

    OpenXmlElement? FindNote(bool footnote, int id)
    {
        try
        {
            OpenXmlElement? root = footnote ? _main.FootnotesPart?.RootElement : _main.EndnotesPart?.RootElement;
            return root?.Children(footnote ? "footnote" : "endnote").FirstOrDefault(n => Integer(n.Attr("id")) == id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Character formatting that differs from the paragraph's (the paragraph element carries the rest).</summary>
    string RunCss(WordRunFormat run, WordRunFormat paragraph)
    {
        var css = new StringBuilder();
        if (!string.Equals(run.Font, paragraph.Font, StringComparison.OrdinalIgnoreCase) || !string.Equals(run.EastAsiaFont, paragraph.EastAsiaFont, StringComparison.OrdinalIgnoreCase))
            css.Append("font-family: ").Append(FontStack(run)).Append("; ");
        var size = run.VerticalAlign is "superscript" or "subscript" ? run.Size * 0.65 : run.Size;
        if (Math.Abs(size - paragraph.Size) > 0.01)
            css.Append("font-size: ").Append(Pt(size)).Append("; ");
        if (run.Bold != paragraph.Bold)
            css.Append("font-weight: ").Append(run.Bold ? 700 : 400).Append("; ");
        if (run.Italic != paragraph.Italic)
            css.Append("font-style: ").Append(run.Italic ? "italic" : "normal").Append("; ");
        if (run.Color != paragraph.Color)
            css.Append("color: ").Append(run.Color ?? "#000000").Append("; ");
        var background = run.Highlight ?? run.Shading;
        if (background is not null)
            css.Append("background-color: ").Append(background).Append("; ");
        var lines = new List<string>();
        if (run.Underline is not null)
            lines.Add("underline");
        if (run.Strike || run.DoubleStrike)
            lines.Add("line-through");
        if (lines.Count > 0)
        {
            css.Append("text-decoration-line: ").Append(string.Join(' ', lines)).Append("; ");
            var style = run.DoubleStrike || run.Underline is "double" ? "double"
                : run.Underline is "wave" or "wavyHeavy" or "wavyDouble" ? "wavy"
                : run.Underline is "dotted" or "dottedHeavy" ? "dotted"
                : run.Underline is not null && run.Underline.StartsWith("dash", StringComparison.Ordinal) ? "dashed"
                : null;
            if (style is not null)
                css.Append("text-decoration-style: ").Append(style).Append("; ");
            if (run.UnderlineColor is not null)
                css.Append("text-decoration-color: ").Append(run.UnderlineColor).Append("; ");
            if (run.Underline is "thick" or "dottedHeavy" or "dashedHeavy" or "wavyHeavy")
                css.Append("text-decoration-thickness: 2px; ");
        }
        if (run.Caps)
            css.Append("text-transform: uppercase; ");
        else if (run.SmallCaps)
            css.Append("font-variant: small-caps; ");
        if (Math.Abs(run.Position) > 0.01 && run.VerticalAlign is null)
            css.Append("vertical-align: ").Append(Pt(run.Position)).Append("; ");
        if (Math.Abs(run.Spacing) > 0.01)
            css.Append("letter-spacing: ").Append(Pt(run.Spacing)).Append("; ");
        return css.ToString().TrimEnd();
    }

    // Fields ----------------------------------------------------------------------------------------------------------

    void FieldChar(string? type, InlineWriter writer, Story story)
    {
        var fields = story.Fields;
        switch (type)
        {
            case "begin":
                fields.Add(new FieldFrame());
                break;
            case "separate":
                if (fields.Count == 0)
                    break;
                var frame = fields[^1];
                frame.InResult = true;
                frame.Href = FieldHyperlink(frame.Instruction.ToString());
                if (frame.Href is not null && fields.All(f => f.InResult))
                {
                    writer.Open($"<a href=\"{Html(frame.Href)}\">", "</a>");
                    frame.LinkOpen = true;
                }
                break;
            case "end":
                if (fields.Count == 0)
                    break;
                if (fields[^1].LinkOpen)
                    writer.Close();
                fields.RemoveAt(fields.Count - 1);
                break;
        }
    }

    /// <summary>HYPERLINK fields that span paragraphs (a table of contents) reopen their link in each paragraph.</summary>
    static void OpenFieldLinks(InlineWriter writer, Story story)
    {
        if (story.Fields.Count == 0 || story.Fields.Any(f => !f.InResult))
            return;
        foreach (var frame in story.Fields.Where(f => f.Href is not null))
        {
            writer.Open($"<a href=\"{Html(frame.Href!)}\">", "</a>");
            frame.LinkOpen = true;
        }
    }

    static void CloseFieldLinks(InlineWriter writer, Story story)
    {
        foreach (var frame in story.Fields)
            frame.LinkOpen = false;
        while (writer.OpenCount > 0)
            writer.Close();
    }

    /// <summary>
    /// A field still in its instruction at the end of a paragraph never got its "separate" or "end" (buggy templating
    /// and mail-merge tools leave these behind). Instructions don't run across paragraphs, so it is dropped, with any
    /// fields nested in it — otherwise everything after it would stay hidden as "instruction". Fields showing their
    /// result (a table of contents) stay open across paragraphs.
    /// </summary>
    static void DropUnfinishedFields(Story story)
    {
        var first = story.Fields.FindIndex(f => !f.InResult);
        if (first >= 0)
            story.Fields.RemoveRange(first, story.Fields.Count - first);
    }

    /// <summary>The target of a HYPERLINK field instruction ("HYPERLINK \"https://…\"" or "HYPERLINK \l \"_Toc1\"").</summary>
    static string? FieldHyperlink(string instruction)
    {
        var text = instruction.Trim();
        if (!text.StartsWith("HYPERLINK", StringComparison.OrdinalIgnoreCase))
            return null;
        var arguments = FieldArguments(text[9..]);
        string? url = null;
        string? anchor = null;
        for (var i = 0; i < arguments.Count; i++)
        {
            if (arguments[i] == "\\l" && i + 1 < arguments.Count)
                anchor = arguments[++i];
            else if (arguments[i].StartsWith('\\'))
                i += arguments[i] is "\\o" or "\\t" && i + 1 < arguments.Count ? 1 : 0;
            else
                url ??= arguments[i];
        }
        return SafeHref(anchor is not null ? "#" + anchor : url);
    }

    static List<string> FieldArguments(string text)
    {
        var result = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
                continue;
            }
            if (text[i] == '"')
            {
                var end = text.IndexOf('"', i + 1);
                if (end < 0)
                    end = text.Length;
                result.Add(text[(i + 1)..end]);
                i = end + 1;
                continue;
            }
            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]))
                i++;
            result.Add(text[start..i]);
        }
        return result;
    }

    static string? HyperlinkTarget(OpenXmlElement hyperlink, OpenXmlPart part)
    {
        if (hyperlink.Attr("anchor") is { Length: > 0 } anchor)
            return SafeHref("#" + anchor);
        if (hyperlink.Attr("id", R) is not { } id)
            return null;
        try
        {
            var relationship = part.HyperlinkRelationships.FirstOrDefault(r => r.Id == id);
            var target = relationship?.Uri.OriginalString;
            if (target is not null && hyperlink.Attr("docLocation") is { Length: > 0 } location)
                target += "#" + location;
            return SafeHref(target);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    // Tab stops -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Lays out a paragraph split at its tabs on the paragraph's tab stops (left stops as fixed-width boxes, a final
    /// right or centre stop as a flexible gap with its leader), e.g. table of contents lines and "Name … Date" lines.
    /// Returns null when the tabs don't fit a pattern HTML can show; the tabs are then plain tab characters.
    /// </summary>
    string? TabLayout(IReadOnlyList<string> segments, WordParagraphFormat format, Scope scope, out string css)
    {
        css = "";
        var tabs = segments.Count - 1;
        var stops = format.Tabs.Where(t => t.Position > format.Left + Math.Max(0, format.FirstLine) + 0.5).ToList();
        if (tabs > stops.Count || tabs > 6)
            return null;
        var flexible = stops.Take(tabs).Select((s, i) => (s, i)).Where(x => x.s.Align is "right" or "center").ToList();
        // Supported: only left stops; or left stops then a right/centre stop for the last tab; or centre then right.
        if (flexible.Count > 2 || flexible.Any(f => f.i < tabs - flexible.Count))
            return null;

        var right = Math.Max(scope.Width - format.Right, format.Left + 1);
        var html = new StringBuilder();
        var position = format.Left + Math.Max(0, format.FirstLine);
        for (var i = 0; i < segments.Count; i++)
        {
            var stop = i < tabs ? stops[i] : (WordTab?)null;
            if (stop is { Align: "left" or "decimal" or "num" } left)
            {
                html.Append("<span style=\"flex: none; min-width: ").Append(Pt(Math.Max(0, left.Position - position))).Append("\">").Append(segments[i]).Append("</span>");
                position = left.Position;
                continue;
            }
            html.Append("<span style=\"flex: 0 1 auto\">").Append(segments[i]).Append("</span>");
            if (stop is { } flexStop)
            {
                var leader = flexStop.Leader is "dot" or "hyphen" or "underscore" or "heavy" or "middleDot" ? " " + flexStop.Leader : "";
                html.Append("<span class=\"ld").Append(leader).Append("\"></span>");
                // The text after a right tab ends at the stop, not at the right margin.
                if (i + 1 == tabs && flexStop.Align == "right" && right - flexStop.Position > 1)
                {
                    html.Append("<span style=\"flex: none; order: 1; width: ").Append(Pt(right - flexStop.Position)).Append("\"></span>");
                }
                else if (i + 1 == tabs && flexStop.Align == "center")
                {
                    var after = (right - flexStop.Position) - (flexStop.Position - position);
                    if (after > 1)
                        html.Append("<span style=\"flex: none; order: 1; width: ").Append(Pt(after)).Append("\"></span>");
                }
                position = flexStop.Position;
            }
        }
        css = " display: flex; flex-wrap: nowrap; align-items: baseline; text-indent: 0;"
              + (format.FirstLine > 0 ? $" padding-left: {Pt(format.FirstLine)};" : "");
        return html.ToString();
    }

    // Notes -----------------------------------------------------------------------------------------------------------

    void AppendNotes(StringBuilder html, double width)
    {
        AppendNoteList(html, _footnotes, _main.FootnotesPart, "fn", "notes", width);
        AppendNoteList(html, _endnotes, _main.EndnotesPart, "en", "notes end", width);
    }

    void AppendNoteList(StringBuilder html, List<(int Number, OpenXmlElement Note)> notes, OpenXmlPart? part, string prefix, string cssClass, double width)
    {
        if (notes.Count == 0 || part is null)
            return;
        html.Append("<section class=\"").Append(cssClass).Append("\"><hr><ol>");
        // Notes can reference further notes; render until the list stops growing.
        for (var i = 0; i < notes.Count; i++)
        {
            var (number, note) = notes[i];
            html.Append("<li id=\"").Append(prefix).Append(number.ToString(CultureInfo.InvariantCulture)).Append("\" value=\"")
                .Append(number.ToString(CultureInfo.InvariantCulture)).Append("\">");
            RenderBlocks(note.ChildElements, html, new Scope(new Story(part), width - 18));
            html.Append("</li>");
        }
        html.Append("</ol></section>\n");
    }

    const string BaseCss = """
        :root { color-scheme: light; font-variant-ligatures: none; }
        * { -webkit-print-color-adjust: exact; print-color-adjust: exact; }
        html { background: #ffffff; }
        body { margin: 0; color: #000000; background: #ffffff; font-family: @FONT@; font-size: @SIZE@; font-kerning: normal; }
        section.page { box-sizing: border-box; overflow-wrap: break-word; }
        p, h1, h2, h3, h4, h5, h6, li { margin: 0; padding: 0; font-size: inherit; font-weight: inherit; white-space: pre-wrap; tab-size: @TAB@; orphans: 2; widows: 2; }
        ol, ul { margin: 0; padding: 0; list-style: none; }
        .mk { display: inline-block; text-indent: 0; white-space: pre; }
        a { color: inherit; text-decoration: none; }
        table { border-collapse: collapse; border-spacing: 0; }
        td, th { padding: 0; vertical-align: top; text-align: left; font-weight: inherit; }
        img { border: 0; }
        sup, sub { font-size: inherit; line-height: 0; }
        sup.nr { font-size: 65%; vertical-align: super; }
        .pb { display: block; break-before: page; height: 0; overflow: hidden; }
        .cb { display: block; break-before: column; height: 0; overflow: hidden; }
        .ld { flex: 1 1 0; min-width: 6pt; align-self: baseline; height: 0.85em; margin: 0 2pt; }
        .ld.dot, .ld.middleDot { border-bottom: 1.5pt dotted currentColor; }
        .ld.hyphen { border-bottom: 1pt dashed currentColor; }
        .ld.underscore, .ld.heavy { border-bottom: 1pt solid currentColor; }
        .tbox { box-sizing: border-box; padding: 3.6pt 7.2pt; overflow-wrap: break-word; }
        .chart { box-sizing: border-box; border: 1px dashed #b0b0b0; background: #f6f6f6; display: inline-block; }
        section.notes { margin-top: 18pt; break-inside: auto; }
        section.notes > hr { width: 33%; margin: 0 0 6pt; border: 0; border-top: 0.75pt solid #000000; text-align: left; margin-left: 0; }
        section.notes > ol { list-style: decimal; padding-left: 18pt; }
        section.notes.end > ol { list-style: lower-roman; }
        section.notes > ol > li { white-space: normal; }
        .doc-header, .doc-footer { color: #555555; }
        @media screen {
          html, body { background: #e8e8e8; }
          body { padding: 24px 0; }
          section.page { background: #ffffff; margin: 0 auto 24px; box-shadow: 0 1px 4px rgba(0, 0, 0, 0.25); }
          .doc-header { margin-bottom: 18pt; } .doc-footer { margin-top: 18pt; }
        }
        @media print {
          .screen-only { display: none !important; }
          section.page { margin: 0; padding: 0 !important; width: auto !important; min-height: 0 !important; box-shadow: none; }
        }

        """;
}
