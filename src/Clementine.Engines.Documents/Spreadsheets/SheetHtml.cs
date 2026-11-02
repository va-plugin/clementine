using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Clementine.Engines.Documents.Markup;
using Clementine.Engines.Documents.OpenXml;
using Clementine.Engines.Documents.OpenXml.Word;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using static Clementine.Engines.Documents.OpenXml.Ooxml;

namespace Clementine.Engines.Documents.Spreadsheets;

/// <summary>A workbook (or CSV) as one HTML page: every sheet a styled table on its own printed pages.</summary>
internal sealed record SheetHtmlResult(string Html, int SheetCount, IReadOnlyList<string> Warnings)
{
    /// <summary>Table cells on the page (rows × columns, all sheets): what makes Edge's print slow.</summary>
    public int Cells { get; init; }
}

/// <summary>How a sheet prints: paper, orientation, margins (pt), scaling and header/footer content (CSS).</summary>
internal sealed record SheetPrint
{
    public double PaperWidth { get; init; } = HtmlPage.PaperSize == "letter" ? 612 : 595.3;

    public double PaperHeight { get; init; } = HtmlPage.PaperSize == "letter" ? 792 : 841.9;

    /// <summary>Null when the sheet doesn't say (it is then chosen by width).</summary>
    public bool? Landscape { get; init; }

    public double Top { get; init; } = 54;

    public double Bottom { get; init; } = 54;

    public double Left { get; init; } = 50.4;

    public double Right { get; init; } = 50.4;

    public double HeaderMargin { get; init; } = 21.6;

    public double FooterMargin { get; init; } = 21.6;

    /// <summary>pageSetup scale (percent).</summary>
    public double? Scale { get; init; }

    /// <summary>"Fit to N pages wide / M tall" (0 = automatic).</summary>
    public int FitWidth { get; init; }

    public int FitHeight { get; init; }

    public bool CenterHorizontally { get; init; }

    public bool Gridlines { get; init; }

    public string? Header { get; init; }

    public string? Footer { get; init; }
}

/// <summary>Assembles sheets into one page with CSS paged media (one named page per sheet).</summary>
internal sealed class SheetHtmlWriter(string defaultFont, double defaultSize)
{
    readonly StringBuilder _body = new();
    readonly StringBuilder _pages = new();
    public CssClasses Css { get; } = new();
    int _count;

    public int Count => _count;

    /// <summary>Adds one sheet: its table, its natural size (pt) and how it prints.</summary>
    public void Add(string? heading, string table, double tableWidth, double tableHeight, SheetPrint print, string fileName, string sheetName)
    {
        _count++;
        var name = "sh" + _count.ToString(CultureInfo.InvariantCulture);
        var (portraitWidth, portraitHeight) = print.PaperWidth <= print.PaperHeight ? (print.PaperWidth, print.PaperHeight) : (print.PaperHeight, print.PaperWidth);
        var headingHeight = heading is null ? 0 : 30;
        double Printable(bool landscape) => (landscape ? portraitHeight : portraitWidth) - print.Left - print.Right;
        var landscape = print.Landscape ?? tableWidth > Printable(false) + 1;
        var width = landscape ? portraitHeight : portraitWidth;
        var height = landscape ? portraitWidth : portraitHeight;
        var printableWidth = Math.Max(72, width - print.Left - print.Right);
        var printableHeight = Math.Max(72, height - print.Top - print.Bottom);

        var zoom = print.Scale is { } scale and >= 10 and <= 400 ? scale / 100 : 1.0;
        if (print.FitWidth > 0 || print.FitHeight > 0)
        {
            zoom = 1;
            if (print.FitWidth > 0 && tableWidth > 0)
                zoom = Math.Min(zoom, printableWidth * print.FitWidth / tableWidth);
            if (print.FitHeight > 0 && tableHeight > 0)
                zoom = Math.Min(zoom, (printableHeight * print.FitHeight - headingHeight) / tableHeight);
        }
        // Edge can't spread one table across pages side by side: whatever is too wide is scaled to the page width.
        if (tableWidth * zoom > printableWidth)
            zoom = printableWidth / tableWidth;
        zoom = Math.Clamp(zoom, 0.05, 4);

        _pages.Append("@page ").Append(name).Append(" { size: ").Append(Pt(width)).Append(' ').Append(Pt(height))
            .Append("; margin: ").Append(Pt(print.Top)).Append(' ').Append(Pt(print.Right)).Append(' ').Append(Pt(print.Bottom)).Append(' ').Append(Pt(print.Left)).Append("; ")
            .Append(Boxes(print.Header, top: true, print, fileName, sheetName))
            .Append(Boxes(print.Footer, top: false, print, fileName, sheetName))
            .Append("}\n");
        _pages.Append("section.").Append(name).Append(" { page: ").Append(name).Append("; }\n");
        if (Math.Abs(zoom - 1) > 0.001)
            _pages.Append("@media print { section.").Append(name).Append(" > .fit { zoom: ").Append(Css(Math.Round(zoom, 4))).Append("; } }\n");

        _body.Append("<section class=\"sheet ").Append(name).Append(print.Gridlines ? " grid" : "").Append("\">");
        if (heading is not null)
            _body.Append("<h2 class=\"sheet-name\">").Append(Html(heading)).Append("</h2>");
        _body.Append("<div class=\"fit\"").Append(print.CenterHorizontally ? " style=\"display: flex; justify-content: center;\"" : "").Append('>')
            .Append(table).Append("</div></section>\n");
    }

    public string Build(string title)
    {
        var html = new StringBuilder(_body.Length + 6000);
        html.Append("<!DOCTYPE html>\n<html>\n<head>\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        html.Append("<meta name=\"generator\" content=\"Clementine\">\n<title>").Append(Html(title)).Append("</title>\n<style>\n");
        html.Append(BaseCss.Replace("@FONT@", WordFonts.Stack(defaultFont, null, null), StringComparison.Ordinal)
            .Replace("@SIZE@", Pt(defaultSize), StringComparison.Ordinal));
        html.Append(_pages);
        html.Append(Css.Rules);
        html.Append("</style>\n</head>\n<body>\n").Append(_body).Append("</body>\n</html>\n");
        return html.ToString();
    }

    /// <summary>An Excel header/footer ("&amp;L&amp;D&amp;CPage &amp;P of &amp;N&amp;R&amp;A") as margin boxes.</summary>
    string Boxes(string? code, bool top, SheetPrint print, string fileName, string sheetName)
    {
        if (string.IsNullOrEmpty(code))
            return "";
        var sections = new Dictionary<char, StringBuilder> { ['L'] = new(), ['C'] = new(), ['R'] = new() };
        var current = 'C';
        var text = new StringBuilder();
        void Flush()
        {
            if (text.Length > 0)
            {
                sections[current].Append(' ').Append(CssString(text.ToString()));
                text.Clear();
            }
        }
        for (var i = 0; i < code.Length; i++)
        {
            var c = code[i];
            if (c != '&' || i + 1 >= code.Length)
            {
                text.Append(c);
                continue;
            }
            var next = code[++i];
            switch (char.ToUpperInvariant(next))
            {
                case '&':
                    text.Append('&');
                    break;
                case 'L' or 'C' or 'R':
                    Flush();
                    current = char.ToUpperInvariant(next);
                    break;
                case 'P':
                    Flush();
                    sections[current].Append(" counter(page)");
                    while (i + 1 < code.Length && (code[i + 1] is '+' or '-' || char.IsDigit(code[i + 1])))
                        i++;
                    break;
                case 'N':
                    Flush();
                    sections[current].Append(" counter(pages)");
                    break;
                case 'D':
                    text.Append(DateTime.Now.ToString("d", CultureInfo.CurrentCulture));
                    break;
                case 'T':
                    text.Append(DateTime.Now.ToString("t", CultureInfo.CurrentCulture));
                    break;
                case 'F':
                    text.Append(fileName);
                    break;
                case 'A':
                    text.Append(sheetName);
                    break;
                case '"':
                    var end = code.IndexOf('"', i + 1);
                    i = end < 0 ? code.Length : end;
                    break;
                case 'K':
                    i = Math.Min(code.Length - 1, i + 6);
                    break;
                default:
                    if (char.IsDigit(next))
                    {
                        while (i + 1 < code.Length && char.IsDigit(code[i + 1]))
                            i++;
                    }
                    break;
            }
        }
        Flush();

        var edge = top ? "top" : "bottom";
        var css = new StringBuilder();
        foreach (var (key, content) in sections)
        {
            if (content.Length == 0)
                continue;
            var position = key switch { 'L' => "left", 'R' => "right", _ => "center" };
            var padding = Math.Clamp(top ? print.HeaderMargin : print.FooterMargin, 0, Math.Max(0, (top ? print.Top : print.Bottom) - 14));
            css.Append('@').Append(edge).Append('-').Append(position).Append(" { content:").Append(content)
                .Append("; font-family: ").Append(WordFonts.Stack(defaultFont, null, null)).Append("; font-size: 9pt; white-space: pre; vertical-align: ").Append(edge)
                .Append("; padding-").Append(edge).Append(": ").Append(Pt(padding)).Append("; } ");
        }
        return css.ToString();
    }

    const string BaseCss = """
        :root { color-scheme: light; font-variant-ligatures: none; }
        * { -webkit-print-color-adjust: exact; print-color-adjust: exact; }
        html, body { margin: 0; background: #ffffff; color: #000000; }
        body { font-family: @FONT@; font-size: @SIZE@; }
        h2.sheet-name { font: 600 13pt "Segoe UI", system-ui, sans-serif; margin: 0 0 9pt; color: #1f2328; }
        .sheet table { border-collapse: collapse; table-layout: fixed; }
        .sheet td { padding: 0 2.25pt; overflow: hidden; white-space: nowrap; vertical-align: bottom; line-height: 1.2; text-overflow: clip; }
        .sheet td.wr { white-space: pre-wrap; overflow-wrap: break-word; }
        .sheet td.ov { overflow: visible; }
        .sheet td.an { text-align: right; }
        .sheet td.ac { text-align: center; }
        .sheet a { color: inherit; text-decoration: none; }
        .sheet tr { break-inside: avoid; }
        @media screen {
          html, body { background: #e8e8e8; }
          body { padding: 24px; }
          section.sheet { background: #ffffff; margin: 0 auto 24px; padding: 24px; width: max-content; max-width: 100%; box-sizing: border-box; overflow-x: auto; box-shadow: 0 1px 4px rgba(0, 0, 0, 0.25); }
          :where(.sheet) td { border: 1px solid #e2e2e2; }
        }
        @media print {
          :where(.sheet.grid) td { border: 0.5pt solid #b4b4b4; }
        }

        """;
}

/// <summary>
/// Reads an .xlsx/.xlsm for display and printing: cell values through their number formats, fonts, fills, borders,
/// alignment, column widths, row heights, merged cells, hidden rows/columns, print areas, print titles, page setup and
/// headers/footers. Charts, pictures and conditional formatting are not drawn.
/// </summary>
internal static partial class XlsxHtml
{
    public const int MaxRows = 20_000;

    /// <summary>Cells per sheet in a web page (sheet.html).</summary>
    public const int MaxCells = 250_000;

    /// <summary>
    /// Cells per sheet in a page Edge prints to PDF. Edge's time grows faster than the cell count: on a 2018 laptop
    /// (i7-8750H) 90,000 cells print in 32–40 s, 180,000 in nearly two minutes (its limit) and 240,000 fail outright.
    /// Splitting the sheet into several tables doesn't help (measured), so the printed sheet is capped instead.
    /// </summary>
    public const int MaxPrintCells = 100_000;

    const string DrawingsWarning = "Charts, pictures and shapes on the sheets aren't included.";
    public const int MaxColumns = 256;

    sealed class CellData
    {
        public int Style { get; init; }

        public string? Type { get; init; }

        public string? Value { get; init; }

        public OpenXmlElement? Inline { get; init; }
    }

    sealed class SheetContent
    {
        public SortedDictionary<int, SortedDictionary<int, CellData>> Cells { get; } = [];

        public Dictionary<int, (double? Height, bool Hidden, int? Style)> Rows { get; } = [];

        public List<(int Min, int Max, double? Width, bool Hidden, int? Style)> Columns { get; } = [];

        public List<(int Row1, int Column1, int Row2, int Column2)> Merges { get; } = [];

        public Dictionary<(int Row, int Column), string> Links { get; } = [];

        public OpenXmlElement? PageSetup { get; set; }

        public OpenXmlElement? PageMargins { get; set; }

        public OpenXmlElement? PrintOptions { get; set; }

        public OpenXmlElement? HeaderFooter { get; set; }

        public OpenXmlElement? SheetProperties { get; set; }

        public double? DefaultRowHeight { get; set; }

        public double? DefaultColumnWidth { get; set; }

        public double? BaseColumnWidth { get; set; }

        public bool Truncated { get; set; }
    }

    /// <summary>
    /// The workbook at <paramref name="path"/> as one page. A web page keeps up to <see cref="MaxCells"/> cells per
    /// sheet; a page for printing (<paramref name="forPrint"/>) up to <see cref="MaxPrintCells"/> for all its sheets
    /// together (each sheet still shows its first 500 rows). Cut sheets get a warning.
    /// </summary>
    public static SheetHtmlResult Convert(string path, string title, CultureInfo culture, CancellationToken cancellationToken, bool forPrint = false)
    {
        using var document = OpenXmlFiles.OpenSpreadsheet(path);
        try
        {
            return Convert(document, path, title, culture, forPrint, cancellationToken);
        }
        catch (Exception ex) when (ex is not (OperationException or OperationCanceledException))
        {
            // A damaged part, a Word file renamed to .xlsx…: never the SDK's own message.
            throw new OperationException($"Couldn't read \"{Path.GetFileName(path)}\" — it's damaged or isn't really an Excel workbook.", ex.ToString(), ex);
        }
    }

    static SheetHtmlResult Convert(SpreadsheetDocument document, string path, string title, CultureInfo culture, bool forPrint, CancellationToken cancellationToken)
    {
        var workbook = document.WorkbookPart ?? throw new OperationException("This workbook has no sheets.");
        var workbookRoot = workbook.RootElement;
        var date1904 = IsTrue(workbookRoot.Child("workbookPr").Attr("date1904"));
        var styles = new SheetStyles(workbook, culture);
        List<OpenXmlElement> sharedStrings;
        try
        {
            sharedStrings = workbook.SharedStringTablePart?.RootElement?.Children("si").ToList() ?? [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sharedStrings = [];
        }

        var sheets = workbookRoot.Child("sheets").Children("sheet").ToList();
        var definedNames = workbookRoot.Child("definedNames").Children("definedName").ToList();
        var writer = new SheetHtmlWriter(styles.DefaultFont, styles.DefaultSize);
        var warnings = new List<string>();
        var visible = sheets.Where(s => (s.Attr("state") ?? "visible") == "visible").ToList();
        var fileName = Path.GetFileName(path);
        var totalCells = 0;

        for (var index = 0; index < sheets.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sheet = sheets[index];
            if ((sheet.Attr("state") ?? "visible") != "visible" || sheet.Attr("id", R) is not { } id)
                continue;
            WorksheetPart? part;
            try
            {
                part = workbook.GetPartById(id) as WorksheetPart;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                part = null;
            }
            if (part is null)
                continue;
            var name = sheet.Attr("name") ?? $"Sheet{index + 1}";
            var printArea = DefinedRange(definedNames, "_xlnm.Print_Area", index);
            var titles = DefinedRange(definedNames, "_xlnm.Print_Titles", index);
            var content = Read(part, printArea, cancellationToken);
            if (part.DrawingsPart is not null && !warnings.Contains(DrawingsWarning))
                warnings.Add(DrawingsWarning);

            var maxCells = forPrint ? Math.Max(0, MaxPrintCells - totalCells) : MaxCells;
            var table = RenderTable(content, styles, sharedStrings, culture, date1904, printArea, titles, writer.Css, maxCells, out var width, out var height, out var cells);
            if (content.Truncated)
                warnings.Add($"\"{name}\" is very large, so only its first rows and columns were included.");
            if (table is null)
                continue;
            totalCells += cells;
            var showHeading = visible.Count > 1 || !DefaultSheetName().IsMatch(name);
            writer.Add(showHeading ? name : null, table, width, height, PrintSetup(content, styles), fileName, name);
        }
        if (writer.Count == 0)
            throw new OperationException("This workbook is empty.");
        return new SheetHtmlResult(writer.Build(title), writer.Count, warnings) { Cells = totalCells };
    }

    [GeneratedRegex(@"^\p{L}+ ?\d{1,3}$")]
    private static partial Regex DefaultSheetName();

    /// <summary>A sheet-local defined name ("Sheet1!$A$1:$F$40", "'My sheet'!$1:$2") as (row1, col1, row2, col2); 0 = open-ended.</summary>
    static (int Row1, int Column1, int Row2, int Column2)? DefinedRange(List<OpenXmlElement> names, string name, int sheetIndex)
    {
        var definition = names.FirstOrDefault(n => n.Attr("name") == name && Integer(n.Attr("localSheetId")) == sheetIndex);
        if (definition is null)
            return null;
        var text = definition.InnerText;
        (int, int, int, int)? result = null;
        foreach (var area in text.Split(','))
        {
            var reference = area.Contains('!') ? area[(area.LastIndexOf('!') + 1)..] : area;
            reference = reference.Replace("$", "", StringComparison.Ordinal).Trim();
            var parts = reference.Split(':');
            if (parts.Length != 2)
                continue;
            var (r1, c1) = CellReference(parts[0]);
            var (r2, c2) = CellReference(parts[1]);
            var range = (Math.Min(r1, r2), Math.Min(c1, c2), Math.Max(r1, r2), Math.Max(c1, c2));
            if (result is null)
            {
                result = range;
            }
            else
            {
                // Print titles: "$1:$2" (rows) and "$A:$A" (columns) — keep the rows.
                if (range.Item1 > 0 && result.Value.Item1 == 0)
                    result = range;
            }
        }
        return result;
    }

    /// <summary>"B12" → (12, 2); "B" → (0, 2); "12" → (12, 0).</summary>
    static (int Row, int Column) CellReference(string reference)
    {
        var column = 0;
        var i = 0;
        for (; i < reference.Length && char.IsAsciiLetter(reference[i]); i++)
            column = Math.Min(column * 26 + (char.ToUpperInvariant(reference[i]) - 'A' + 1), 16384);
        var row = int.TryParse(reference.AsSpan(i), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? Math.Clamp(parsed, 1, 1_048_576) : 0;
        return (row, column);
    }

    static SheetContent Read(WorksheetPart part, (int Row1, int Column1, int Row2, int Column2)? printArea, CancellationToken cancellationToken)
    {
        var content = new SheetContent();
        var firstRow = printArea is { Row1: > 0 } area ? area.Row1 : 1;
        var nextRow = 1;
        var kept = 0;
        using var reader = OpenXmlReader.Create(part);
        while (reader.Read())
        {
            if (!reader.IsStartElement)
                continue;
            switch (reader.LocalName)
            {
                case "row":
                    var row = reader.LoadCurrentElement()!;
                    var rowIndex = Integer(row.Attr("r")) is { } declared ? Math.Clamp(declared, 1, 1_048_576) : nextRow;
                    nextRow = rowIndex + 1;
                    if (rowIndex < firstRow)
                        continue;
                    if (kept >= MaxRows || (printArea is { Row2: > 0 } limit && rowIndex > limit.Row2))
                    {
                        content.Truncated |= kept >= MaxRows;
                        continue;
                    }
                    if (++kept % 2000 == 0)
                        cancellationToken.ThrowIfCancellationRequested();
                    var height = IsTrue(row.Attr("customHeight")) || row.Attr("ht") is not null ? Number(row.Attr("ht")) : null;
                    var rowStyle = IsTrue(row.Attr("customFormat")) ? Integer(row.Attr("s")) : null;
                    content.Rows[rowIndex] = (height, IsTrue(row.Attr("hidden")), rowStyle);
                    var nextColumn = 1;
                    foreach (var cell in row.Children("c"))
                    {
                        var column = cell.Attr("r") is { } reference ? CellReference(reference).Column : nextColumn;
                        if (column == 0)
                            column = nextColumn;
                        nextColumn = column + 1;
                        if (column > MaxColumns)
                        {
                            content.Truncated = true;
                            continue;
                        }
                        if (!content.Cells.TryGetValue(rowIndex, out var cells))
                        {
                            cells = [];
                            content.Cells[rowIndex] = cells;
                        }
                        cells[column] = new CellData
                        {
                            Style = Integer(cell.Attr("s")) ?? rowStyle ?? 0,
                            Type = cell.Attr("t"),
                            Value = cell.Child("v")?.InnerText,
                            Inline = cell.Child("is"),
                        };
                    }
                    break;
                case "cols":
                    foreach (var column in reader.LoadCurrentElement()!.Children("col"))
                    {
                        var min = Integer(column.Attr("min")) ?? 1;
                        var max = Math.Min(Integer(column.Attr("max")) ?? min, 16384);
                        content.Columns.Add((min, max, Number(column.Attr("width")), IsTrue(column.Attr("hidden")), Integer(column.Attr("style"))));
                    }
                    break;
                case "mergeCells":
                    foreach (var merge in reader.LoadCurrentElement()!.Children("mergeCell"))
                    {
                        var parts = (merge.Attr("ref") ?? "").Split(':');
                        if (parts.Length != 2)
                            continue;
                        var (r1, c1) = CellReference(parts[0]);
                        var (r2, c2) = CellReference(parts[1]);
                        if (r1 > 0 && c1 > 0 && r2 >= r1 && c2 >= c1)
                            content.Merges.Add((r1, c1, r2, c2));
                    }
                    break;
                case "hyperlinks":
                    foreach (var link in reader.LoadCurrentElement()!.Children("hyperlink"))
                    {
                        if (link.Attr("id", R) is not { } relationship)
                            continue;
                        var target = part.HyperlinkRelationships.FirstOrDefault(r => r.Id == relationship)?.Uri.OriginalString;
                        if (SafeHref(target) is { } href && link.Attr("ref") is { } where)
                        {
                            var (row1, column1) = CellReference(where.Split(':')[0]);
                            content.Links[(row1, column1)] = href;
                        }
                    }
                    break;
                case "sheetFormatPr":
                    var format = reader.LoadCurrentElement()!;
                    content.DefaultRowHeight = Number(format.Attr("defaultRowHeight"));
                    content.DefaultColumnWidth = Number(format.Attr("defaultColWidth"));
                    content.BaseColumnWidth = Number(format.Attr("baseColWidth"));
                    break;
                case "pageSetup":
                    content.PageSetup = reader.LoadCurrentElement();
                    break;
                case "pageMargins":
                    content.PageMargins = reader.LoadCurrentElement();
                    break;
                case "printOptions":
                    content.PrintOptions = reader.LoadCurrentElement();
                    break;
                case "headerFooter":
                    content.HeaderFooter = reader.LoadCurrentElement();
                    break;
                case "sheetPr":
                    content.SheetProperties = reader.LoadCurrentElement();
                    break;
            }
        }
        return content;
    }

    static SheetPrint PrintSetup(SheetContent content, SheetStyles styles)
    {
        var setup = content.PageSetup;
        var margins = content.PageMargins;
        double Inches(string name, double fallback) => Math.Clamp((Number(margins.Attr(name)) ?? fallback) * 72, 0, 288);
        var paper = Integer(setup.Attr("paperSize")) switch
        {
            1 => (612.0, 792.0),
            3 => (792.0, 1224.0),
            5 => (612.0, 1008.0),
            7 => (522.0, 756.0),
            8 => (841.9, 1190.6),
            9 => (595.3, 841.9),
            11 => (419.5, 595.3),
            13 => (515.9, 728.5),
            _ => HtmlPage.PaperSize == "letter" ? (612.0, 792.0) : (595.3, 841.9),
        };
        var fitToPage = IsTrue(content.SheetProperties.Child("pageSetUpPr").Attr("fitToPage"));
        var orientation = setup.Attr("orientation");
        return new SheetPrint
        {
            PaperWidth = paper.Item1,
            PaperHeight = paper.Item2,
            Landscape = orientation switch { "landscape" => true, "portrait" => false, _ => null },
            Top = Inches("top", 0.75),
            Bottom = Inches("bottom", 0.75),
            Left = Inches("left", 0.7),
            Right = Inches("right", 0.7),
            HeaderMargin = Inches("header", 0.3),
            FooterMargin = Inches("footer", 0.3),
            Scale = fitToPage ? null : Number(setup.Attr("scale")),
            FitWidth = fitToPage ? Integer(setup.Attr("fitToWidth")) ?? 1 : 0,
            FitHeight = fitToPage ? Integer(setup.Attr("fitToHeight")) ?? 1 : 0,
            CenterHorizontally = IsTrue(content.PrintOptions.Attr("horizontalCentered")),
            Gridlines = IsTrue(content.PrintOptions.Attr("gridLines")),
            Header = content.HeaderFooter.Child("oddHeader")?.InnerText,
            Footer = content.HeaderFooter.Child("oddFooter")?.InnerText,
        };
    }

    /// <summary>The sheet's table HTML, its natural width and height (pt); null when there is nothing to print.</summary>
    static string? RenderTable(SheetContent content, SheetStyles styles, List<OpenXmlElement> sharedStrings, CultureInfo culture, bool date1904,
        (int Row1, int Column1, int Row2, int Column2)? printArea, (int Row1, int Column1, int Row2, int Column2)? titles, CssClasses css,
        int maxCells, out double tableWidth, out double tableHeight, out int cellCount)
    {
        tableWidth = 0;
        tableHeight = 0;
        cellCount = 0;

        // The printed range: the print area, else every cell with a value or a visible format, plus merged areas.
        int row1 = int.MaxValue, column1 = int.MaxValue, row2 = 0, column2 = 0;
        foreach (var (rowIndex, cells) in content.Cells)
        {
            foreach (var (columnIndex, cell) in cells)
            {
                if (!HasValue(cell) && !Visible(styles.Get(cell.Style)))
                    continue;
                row1 = Math.Min(row1, rowIndex);
                row2 = Math.Max(row2, rowIndex);
                column1 = Math.Min(column1, columnIndex);
                column2 = Math.Max(column2, columnIndex);
            }
        }
        foreach (var merge in content.Merges)
        {
            if (row2 == 0)
                break;
            row1 = Math.Min(row1, merge.Row1);
            row2 = Math.Max(row2, Math.Min(merge.Row2, row1 + MaxRows));
            column1 = Math.Min(column1, merge.Column1);
            column2 = Math.Max(column2, Math.Min(merge.Column2, MaxColumns));
        }
        if (printArea is { } area)
        {
            if (area.Row1 > 0)
            {
                row1 = area.Row1;
                row2 = Math.Min(area.Row2, row1 + MaxRows - 1);
            }
            if (area.Column1 > 0)
            {
                column1 = area.Column1;
                column2 = Math.Min(area.Column2, MaxColumns);
            }
        }
        if (row2 == 0 || column2 == 0 || row1 > row2 || column1 > column2)
            return null;

        // Column widths in points (Excel widths are in "characters" of the default font's digit width).
        var digit = Math.Max(5, Math.Round(styles.DefaultSize * 7 / 11));
        double Pixels(double characters) => Math.Truncate((256 * characters + Math.Truncate(128 / digit)) / 256 * digit);
        var defaultWidth = content.DefaultColumnWidth is { } explicitDefault
            ? Pixels(explicitDefault)
            : Math.Ceiling(((content.BaseColumnWidth ?? 8) * digit + 5) / 8) * 8;
        var columns = new List<(int Index, double Width, int? Style)>();
        for (var c = column1; c <= column2; c++)
        {
            var definition = content.Columns.LastOrDefault(d => d.Min <= c && c <= d.Max);
            if (definition.Max > 0 && (definition.Hidden || definition.Width is 0))
                continue;
            var pixels = definition.Max > 0 && definition.Width is { } width ? Pixels(width) : defaultWidth;
            columns.Add((c, Math.Max(2, pixels * 0.75), definition.Max > 0 ? definition.Style : null));
        }
        if (columns.Count == 0)
            return null;
        tableWidth = columns.Sum(c => c.Width);

        var rows = new List<int>();
        for (var r = row1; r <= row2; r++)
        {
            if (content.Rows.TryGetValue(r, out var info) && (info.Hidden || info.Height is 0))
                continue;
            rows.Add(r);
        }
        if (rows.Count == 0)
            return null;
        // (Very wide sheets still show their first 500 rows.)
        var rowLimit = Math.Max(500, maxCells / columns.Count);
        if (rows.Count > rowLimit)
        {
            rows = rows.Take(rowLimit).ToList();
            content.Truncated = true;
        }
        cellCount = rows.Count * columns.Count;

        // Merged areas: the top-left cell spans the visible rows/columns of the area; the rest are covered.
        var spans = new Dictionary<(int, int), (int Rows, int Columns, int Row2, int Column2)>();
        var covered = new HashSet<(int, int)>();
        foreach (var merge in content.Merges)
        {
            var mergeRows = rows.Where(r => r >= merge.Row1 && r <= merge.Row2).ToList();
            var mergeColumns = columns.Where(c => c.Index >= merge.Column1 && c.Index <= merge.Column2).Select(c => c.Index).ToList();
            if (mergeRows.Count == 0 || mergeColumns.Count == 0 || (mergeRows.Count == 1 && mergeColumns.Count == 1))
                continue;
            spans[(mergeRows[0], mergeColumns[0])] = (mergeRows.Count, mergeColumns.Count, merge.Row2, merge.Column2);
            foreach (var r in mergeRows)
            {
                foreach (var c in mergeColumns)
                {
                    if (r != mergeRows[0] || c != mergeColumns[0])
                        covered.Add((r, c));
                }
            }
        }

        var defaultHeight = content.DefaultRowHeight is > 0 and var fallbackHeight ? fallbackHeight : Math.Round(styles.DefaultSize * 15 / 11, 2);
        var titleRows = titles is { Row1: > 0 } t ? rows.Count(r => r >= t.Row1 && r <= t.Row2) : 0;
        if (titleRows >= rows.Count)
            titleRows = 0;

        var html = new StringBuilder();
        html.Append("<table style=\"width: ").Append(Pt(tableWidth)).Append("\"><colgroup>");
        foreach (var column in columns)
            html.Append("<col style=\"width: ").Append(Pt(column.Width)).Append("\">");
        html.Append("</colgroup>");
        html.Append(titleRows > 0 ? "<thead>" : "<tbody>");
        for (var i = 0; i < rows.Count; i++)
        {
            if (i == titleRows && titleRows > 0)
                html.Append("</thead><tbody>");
            var r = rows[i];
            content.Rows.TryGetValue(r, out var rowInfo);
            var height = rowInfo.Height ?? defaultHeight;
            tableHeight += height;
            content.Cells.TryGetValue(r, out var rowCells);
            html.Append("<tr style=\"height: ").Append(Pt(height)).Append("\">");
            for (var ci = 0; ci < columns.Count; ci++)
            {
                var (c, _, columnStyle) = columns[ci];
                if (covered.Contains((r, c)))
                    continue;
                CellData? cell = null;
                rowCells?.TryGetValue(c, out cell);
                var styleIndex = cell?.Style ?? rowInfo.Style ?? columnStyle ?? 0;
                var style = styles.Get(styleIndex);
                var (text, rich, color, kind) = Display(cell, style, styles, sharedStrings, culture, date1904);
                var classes = new List<string> { css.For("x", style.Css.Length > 0 ? style.Css : "") };
                var merged = spans.TryGetValue((r, c), out var span);
                if (style.Horizontal == "general" && text.Length > 0)
                {
                    if (kind == 'n')
                        classes.Add("an");
                    else if (kind is 'b' or 'e')
                        classes.Add("ac");
                }
                if (style.Wrap)
                {
                    classes.Add("wr");
                }
                else if (merged)
                {
                    // A merged area clips at its own edges.
                }
                else if (text.Length > 0 && kind != 'n')
                {
                    // Text spills into empty neighbours on the right, as in Excel.
                    var nextIndex = ci + 1 < columns.Count ? columns[ci + 1].Index : -1;
                    CellData? neighbour = null;
                    rowCells?.TryGetValue(nextIndex, out neighbour);
                    if (nextIndex < 0 || neighbour is null || !HasValue(neighbour))
                        classes.Add("ov");
                }
                else if (kind == 'n')
                {
                    classes.Add("ov");
                }

                html.Append("<td class=\"").Append(string.Join(' ', classes)).Append('"');
                var extra = new StringBuilder();
                if (merged)
                {
                    if (span.Columns > 1)
                        html.Append(" colspan=\"").Append(span.Columns.ToString(CultureInfo.InvariantCulture)).Append('"');
                    if (span.Rows > 1)
                        html.Append(" rowspan=\"").Append(span.Rows.ToString(CultureInfo.InvariantCulture)).Append('"');
                    // A merged area's right and bottom borders are stored on its right-most / bottom-most cells.
                    var rightCell = CellAt(content, r, span.Column2);
                    var bottomCell = CellAt(content, span.Row2, c);
                    if (rightCell is not null && Border(styles.Get(rightCell.Style).Css, "right") is { } right)
                        extra.Append("border-right: ").Append(right).Append("; ");
                    if (bottomCell is not null && Border(styles.Get(bottomCell.Style).Css, "bottom") is { } bottom)
                        extra.Append("border-bottom: ").Append(bottom).Append("; ");
                }
                if (color is not null)
                    extra.Append("color: ").Append(color).Append("; ");
                if (extra.Length > 0)
                    html.Append(" style=\"").Append(extra.ToString().TrimEnd()).Append('"');
                html.Append('>');
                var body = rich ?? Html(text);
                if (body.Length > 0 && content.Links.TryGetValue((r, c), out var href))
                    body = $"<a href=\"{Html(href)}\">{body}</a>";
                html.Append(body).Append("</td>");
            }
            html.Append("</tr>\n");
        }
        html.Append("</tbody></table>");
        return html.ToString();
    }

    static CellData? CellAt(SheetContent content, int row, int column) =>
        content.Cells.TryGetValue(row, out var cells) && cells.TryGetValue(column, out var cell) ? cell : null;

    static string? Border(string css, string side)
    {
        var marker = $"border-{side}: ";
        var start = css.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            return null;
        start += marker.Length;
        var end = css.IndexOf(';', start);
        return end < 0 ? css[start..] : css[start..end];
    }

    static bool HasValue(CellData cell) => cell.Value is { Length: > 0 } || cell.Inline is not null;

    /// <summary>A format that shows even in an empty cell (a fill or a border).</summary>
    static bool Visible(CellStyle style) => style.Css.Contains("background", StringComparison.Ordinal) || style.Css.Contains("border-", StringComparison.Ordinal);

    /// <summary>The cell's text as displayed, rich-text HTML (or null), the format's colour, and its kind (n, s, b, e).</summary>
    static (string Text, string? Rich, string? Color, char Kind) Display(CellData? cell, CellStyle style, SheetStyles styles, List<OpenXmlElement> sharedStrings,
        CultureInfo culture, bool date1904)
    {
        if (cell is null)
            return ("", null, null, 's');
        switch (cell.Type)
        {
            case "s":
                if (Integer(cell.Value) is { } index && index >= 0 && index < sharedStrings.Count)
                {
                    var item = sharedStrings[index];
                    var plain = StringText(item);
                    var formatted = NumberFormats.FormatText(plain, style.NumberFormat, culture);
                    return (formatted.Text, formatted.Text == plain ? RichText(item, styles) : null, formatted.Color, 's');
                }
                return ("", null, null, 's');
            case "inlineStr":
                if (cell.Inline is { } inline)
                {
                    // Through the format's text section like a shared string (";;;" hides it, "\"Note: \"@" decorates it).
                    var plain = StringText(inline);
                    var formatted = NumberFormats.FormatText(plain, style.NumberFormat, culture);
                    return (formatted.Text, formatted.Text == plain ? RichText(inline, styles) : null, formatted.Color, 's');
                }
                return ("", null, null, 's');
            case "b":
                return (cell.Value == "1" ? "TRUE" : "FALSE", null, null, 'b');
            case "e":
                return (cell.Value ?? "", null, null, 'e');
            case "str":
                var text = cell.Value ?? "";
                var shown = NumberFormats.FormatText(text, style.NumberFormat, culture);
                return (shown.Text, null, shown.Color, 's');
            case "d":
                if (DateTime.TryParse(cell.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var date))
                {
                    var serial = date.ToOADate();
                    var code = NumberFormats.IsDate(style.NumberFormat) ? style.NumberFormat : "yyyy-mm-dd";
                    var asDate = NumberFormats.Format(serial, code, culture, false);
                    return (asDate.Text, null, asDate.Color, 'n');
                }
                return (cell.Value ?? "", null, null, 's');
        }
        if (cell.Value is null || !double.TryParse(cell.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return (cell.Value ?? "", null, null, 's');
        var value = NumberFormats.Format(number, style.NumberFormat, culture, date1904);
        return (value.Text, null, value.Color, 'n');
    }

    /// <summary>Text of a shared or inline string (its runs; phonetic guides excluded).</summary>
    static string StringText(OpenXmlElement item)
    {
        if (item.Child("t") is { } plain && !item.Children("r").Any())
            return plain.InnerText;
        return string.Concat(item.Children("r").Select(r => r.Child("t")?.InnerText ?? ""));
    }

    /// <summary>Rich text (runs with their own fonts) as HTML spans; null for plain strings.</summary>
    static string? RichText(OpenXmlElement item, SheetStyles styles)
    {
        var runs = item.Children("r").ToList();
        if (runs.Count == 0)
            return null;
        var html = new StringBuilder();
        foreach (var run in runs)
        {
            var text = run.Child("t")?.InnerText ?? "";
            if (text.Length == 0)
                continue;
            var properties = run.Child("rPr");
            if (properties is null)
            {
                html.Append(Html(text));
                continue;
            }
            var css = new StringBuilder();
            styles.AppendFont(css, properties, styles.Color(properties.Child("color")), isDefault: false);
            html.Append("<span style=\"").Append(Html(css.ToString().TrimEnd())).Append("\">").Append(Html(text)).Append("</span>");
        }
        return html.ToString();
    }
}

/// <summary>CSV / TSV as a printable table: header row emphasised, numbers right-aligned, gridlines on.</summary>
internal static class CsvHtml
{
    /// <summary>The rows as one page (capped like a workbook sheet: <paramref name="forPrint"/> for one Edge prints).</summary>
    public static SheetHtmlResult Convert(IReadOnlyList<IReadOnlyList<string>> rows, string title, bool forPrint = false)
    {
        if (rows.Count == 0 || rows.All(r => r.All(string.IsNullOrEmpty)))
            throw new OperationException("This file is empty.");
        var warnings = new List<string>();
        var columnCount = Math.Min(XlsxHtml.MaxColumns, rows.Max(r => r.Count));
        if (rows.Max(r => r.Count) > XlsxHtml.MaxColumns)
            warnings.Add($"Only the first {XlsxHtml.MaxColumns} columns were included.");
        var maxCells = forPrint ? XlsxHtml.MaxPrintCells : XlsxHtml.MaxCells;
        var rowLimit = Math.Min(XlsxHtml.MaxRows, Math.Max(500, maxCells / Math.Max(1, columnCount)));
        var limited = rows;
        if (rows.Count > rowLimit)
        {
            limited = rows.Take(rowLimit).ToList();
            warnings.Add($"Only the first {rowLimit:N0} rows were included.");
        }

        // Column widths from the longest value (capped; longer text wraps).
        const double CharacterWidth = 5.6;
        var widths = new double[columnCount];
        for (var c = 0; c < columnCount; c++)
        {
            var longest = limited.Take(2000).Max(r => c < r.Count ? r[c].Split('\n').Max(l => l.Length) : 0);
            widths[c] = Math.Clamp(longest, 3, 48) * CharacterWidth + 8;
        }

        var html = new StringBuilder();
        var tableWidth = widths.Sum();
        html.Append("<table style=\"width: ").Append(Pt(tableWidth)).Append("\"><colgroup>");
        foreach (var width in widths)
            html.Append("<col style=\"width: ").Append(Pt(width)).Append("\">");
        html.Append("</colgroup><thead><tr class=\"hd\">");
        for (var c = 0; c < columnCount; c++)
            html.Append("<td class=\"wr\">").Append(Html(c < limited[0].Count ? limited[0][c] : "")).Append("</td>");
        html.Append("</tr></thead><tbody>\n");
        for (var r = 1; r < limited.Count; r++)
        {
            html.Append("<tr>");
            for (var c = 0; c < columnCount; c++)
            {
                var value = c < limited[r].Count ? limited[r][c] : "";
                var numeric = XlsxWriter.TryNumber(value.Trim(), out _);
                html.Append(numeric ? "<td class=\"wr an\">" : "<td class=\"wr\">").Append(Html(value)).Append("</td>");
            }
            html.Append("</tr>\n");
        }
        html.Append("</tbody></table>");

        var writer = new SheetHtmlWriter("Calibri", 11);
        writer.Add(null, html.ToString(), tableWidth, limited.Count * 15.0, new SheetPrint { Gridlines = true }, title, title);
        var page = writer.Build(title).Replace("</style>", ".sheet tr.hd td { font-weight: 700; background: #f2f2f2; }\n:where(.sheet) td { border: 0.5pt solid #b4b4b4; }\n</style>", StringComparison.Ordinal);
        return new SheetHtmlResult(page, 1, warnings) { Cells = limited.Count * columnCount };
    }
}
