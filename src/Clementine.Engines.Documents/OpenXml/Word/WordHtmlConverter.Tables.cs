using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml;
using static Clementine.Engines.Documents.OpenXml.Ooxml;

namespace Clementine.Engines.Documents.OpenXml.Word;

internal sealed partial class WordHtmlConverter
{
    /// <summary>Table-level formatting merged from the table style chain and the table's own tblPr.</summary>
    sealed class TableFormat
    {
        public WordBorder? Top { get; set; }

        public WordBorder? Bottom { get; set; }

        public WordBorder? Left { get; set; }

        public WordBorder? Right { get; set; }

        public WordBorder? InsideH { get; set; }

        public WordBorder? InsideV { get; set; }

        public double PadTop { get; set; }

        public double PadBottom { get; set; }

        public double PadLeft { get; set; } = 5.4;

        public double PadRight { get; set; } = 5.4;

        public string? Shading { get; set; }

        public string? WidthType { get; set; }

        public double? Width { get; set; }

        public double Indent { get; set; }

        public string Align { get; set; } = "left";

        public int RowBand { get; set; } = 1;

        public int ColumnBand { get; set; } = 1;

        public void Apply(OpenXmlElement? properties, OfficeTheme theme)
        {
            if (properties is null)
                return;
            foreach (var child in properties.ChildElements)
            {
                switch (child.LocalName)
                {
                    case "tblBorders":
                        Top = WordBorder.From(child.Child("top"), theme) ?? Top;
                        Bottom = WordBorder.From(child.Child("bottom"), theme) ?? Bottom;
                        Left = WordBorder.From(child.Child("left") ?? child.Child("start"), theme) ?? Left;
                        Right = WordBorder.From(child.Child("right") ?? child.Child("end"), theme) ?? Right;
                        InsideH = WordBorder.From(child.Child("insideH"), theme) ?? InsideH;
                        InsideV = WordBorder.From(child.Child("insideV"), theme) ?? InsideV;
                        break;
                    case "tblCellMar":
                        PadTop = Twips(child.Child("top").Attr("w")) ?? PadTop;
                        PadBottom = Twips(child.Child("bottom").Attr("w")) ?? PadBottom;
                        PadLeft = Twips((child.Child("left") ?? child.Child("start")).Attr("w")) ?? PadLeft;
                        PadRight = Twips((child.Child("right") ?? child.Child("end")).Attr("w")) ?? PadRight;
                        break;
                    case "shd":
                        Shading = WordRunFormat.ShadingColor(child, theme) ?? Shading;
                        break;
                    case "tblW":
                        WidthType = child.Attr("type") ?? "dxa";
                        Width = Number(child.Attr("w"));
                        if (child.Attr("w") is { } text && text.EndsWith('%'))
                        {
                            WidthType = "pct";
                            Width *= 50;
                        }
                        break;
                    case "tblInd":
                        Indent = Twips(child.Attr("w")) ?? Indent;
                        break;
                    case "jc":
                        Align = child.Attr("val") ?? Align;
                        break;
                    case "tblStyleRowBandSize":
                        RowBand = Math.Max(1, Integer(child.Attr("val")) ?? 1);
                        break;
                    case "tblStyleColBandSize":
                        ColumnBand = Math.Max(1, Integer(child.Attr("val")) ?? 1);
                        break;
                }
            }
        }
    }

    sealed class CellModel
    {
        public required OpenXmlElement Cell { get; init; }

        public int GridStart { get; set; }

        public int Span { get; set; } = 1;

        public int RowSpan { get; set; } = 1;

        /// <summary>A vertically merged continuation (drawn by the cell above).</summary>
        public bool Covered { get; set; }

        /// <summary>For a covered cell: the cell that draws it (the top of the merged area).</summary>
        public CellModel? Owner { get; set; }

        public string? VerticalMerge { get; init; }
    }

    sealed class RowModel
    {
        public required OpenXmlElement Row { get; init; }

        public List<CellModel> Cells { get; } = [];

        public bool Header { get; init; }
    }

    static IEnumerable<OpenXmlElement> Unwrap(OpenXmlElement parent, string localName)
    {
        foreach (var child in parent.ChildElements)
        {
            if (child.LocalName == localName)
                yield return child;
            else if (child.LocalName is "sdt")
            {
                if (child.Child("sdtContent") is { } content)
                    foreach (var inner in Unwrap(content, localName))
                        yield return inner;
            }
            else if (child.LocalName is "customXml" or "ins" or "moveTo")
            {
                foreach (var inner in Unwrap(child, localName))
                    yield return inner;
            }
        }
    }

    void RenderTable(OpenXmlElement table, StringBuilder html, Scope scope)
    {
        var properties = table.Child("tblPr");
        var styleId = properties.Child("tblStyle").Attr("val");
        var chain = _styles.Chain(styleId, "table").Where(s => s.Type == "table").ToList();
        var format = new TableFormat();
        // Word's table defaults when no style says otherwise: no borders, 0.08" cell margins.
        foreach (var style in chain)
            format.Apply(style.TableProperties, _theme);
        format.Apply(properties, _theme);

        // tblLook: which conditional formats apply (Word's default look is header row + first column + banded rows).
        var look = properties.Child("tblLook");
        var lookBits = look?.Attr("val") is { } hex && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var bits) ? bits : 0x04A0;
        bool LookFlag(string name, int bit) => look?.Attr(name) is { } value ? IsTrue(value) : (lookBits & bit) != 0;
        var firstRow = LookFlag("firstRow", 0x20);
        var lastRow = LookFlag("lastRow", 0x40);
        var firstColumn = LookFlag("firstColumn", 0x80);
        var lastColumn = LookFlag("lastColumn", 0x100);
        var noRowBands = LookFlag("noHBand", 0x200);
        var noColumnBands = LookFlag("noVBand", 0x400);

        // Rows and cells on the grid.
        var rows = new List<RowModel>();
        var leading = true;
        foreach (var row in Unwrap(table, "tr"))
        {
            var rowProperties = row.Child("trPr");
            if (rowProperties.Child("del") is not null)
                continue;
            var header = leading && IsOn(rowProperties.Child("tblHeader"));
            leading = header;
            var model = new RowModel { Row = row, Header = header };
            // Skipped grid columns, clamped like gridSpan (Word's tables have at most 63 columns).
            var column = Math.Clamp(Integer(rowProperties.Child("gridBefore").Attr("val")) ?? 0, 0, 64);
            CellModel? horizontalStart = null;
            foreach (var cell in Unwrap(row, "tc"))
            {
                var cellProperties = cell.Child("tcPr");
                var span = Math.Clamp(Integer(cellProperties.Child("gridSpan").Attr("val")) ?? 1, 1, 64);
                var horizontalMerge = cellProperties.Child("hMerge");
                if (horizontalMerge is not null && horizontalMerge.Attr("val") != "restart" && horizontalStart is not null)
                {
                    horizontalStart.Span += span;
                    column += span;
                    continue;
                }
                var verticalMerge = cellProperties.Child("vMerge");
                var cellModel = new CellModel
                {
                    Cell = cell,
                    GridStart = column,
                    Span = span,
                    VerticalMerge = verticalMerge is null ? null : verticalMerge.Attr("val") ?? "continue",
                };
                horizontalStart = horizontalMerge is not null ? cellModel : null;
                model.Cells.Add(cellModel);
                column += span;
            }
            rows.Add(model);
        }
        if (rows.Count == 0)
            return;

        // Vertical merges: a "restart" cell spans the continuation cells below it in the same grid column.
        for (var r = 0; r < rows.Count; r++)
        {
            foreach (var cell in rows[r].Cells)
            {
                if (cell.VerticalMerge == "continue")
                {
                    var above = FindAbove(rows, r, cell.GridStart);
                    if (above is not null)
                    {
                        above.RowSpan++;
                        cell.Covered = true;
                        cell.Owner = above;
                    }
                }
            }
        }

        // Column widths: the table grid (Word's last layout), scaled down to the column when it's too wide.
        var gridCount = Math.Max(1, rows.Max(r => r.Cells.Count == 0 ? 0 : r.Cells.Max(c => c.GridStart + c.Span)));
        var grid = Unwrap(table, "tblGrid").SelectMany(g => g.Children("gridCol")).Select(c => Twips(c.Attr("w")) ?? 0).ToList();
        if (grid.Count == 0)
            grid = Enumerable.Range(0, gridCount).Select(_ => 0.0).ToList();
        while (grid.Count < gridCount)
            grid.Add(0);
        var known = grid.Where(w => w > 0).ToList();
        var fallback = known.Count > 0 ? known.Average() : scope.Width / gridCount;
        for (var i = 0; i < grid.Count; i++)
        {
            if (grid[i] <= 0)
                grid[i] = fallback;
        }
        var total = grid.Sum();
        var gridTotal = Math.Max(1, grid.Take(gridCount).Sum());
        var tableWidth = format.WidthType switch
        {
            "pct" when format.Width is > 0 => scope.Width * Math.Min(format.Width.Value / 5000, 1),
            "dxa" when format.Width is > 0 && format.Width / 20 > total * 1.05 => format.Width.Value / 20,
            _ => total,
        };
        var available = Math.Max(36, scope.Width - Math.Max(0, format.Indent));
        if (tableWidth > available * 1.01)
            tableWidth = available;

        var tableCss = new StringBuilder("table-layout: fixed; ");
        tableCss.Append("width: ").Append(Pt(tableWidth)).Append("; ");
        if (format.Align == "center")
            tableCss.Append("margin-left: auto; margin-right: auto; ");
        else if (format.Align is "right" or "end")
            tableCss.Append("margin-left: auto; ");
        else if (Math.Abs(format.Indent) > 0.1)
            tableCss.Append("margin-left: ").Append(Pt(Math.Max(-format.PadLeft, format.Indent - format.PadLeft))).Append("; ");
        if (format.Shading is not null)
            tableCss.Append("background-color: ").Append(format.Shading).Append("; ");

        html.Append("<table class=\"").Append(_css.For("t", tableCss.ToString().TrimEnd())).Append("\"><colgroup>");
        foreach (var width in grid.Take(gridCount))
            html.Append("<col style=\"width: ").Append(Css(Math.Round(width / gridTotal * 100, 3))).Append("%\">");
        html.Append("</colgroup>");

        var headerRows = rows.TakeWhile(r => r.Header).Count();
        if (headerRows == rows.Count)
            headerRows = 0;
        html.Append(headerRows > 0 ? "<thead>" : "<tbody>");
        for (var r = 0; r < rows.Count; r++)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (r == headerRows && headerRows > 0)
                html.Append("</thead><tbody>");
            var row = rows[r];
            var rowProperties = row.Row.Child("trPr");
            var rowCss = new StringBuilder();
            if (Twips(rowProperties.Child("trHeight").Attr("val")) is { } height and > 0)
                rowCss.Append("height: ").Append(Pt(height)).Append("; ");
            if (IsOn(rowProperties.Child("cantSplit")))
                rowCss.Append("break-inside: avoid; ");
            html.Append(rowCss.Length > 0 ? $"<tr class=\"{_css.For("w", rowCss.ToString().TrimEnd())}\">" : "<tr>");

            foreach (var cell in row.Cells)
            {
                if (cell.Covered)
                    continue;
                var lastRowOfCell = r + cell.RowSpan - 1;
                var types = ConditionalTypes(r, lastRowOfCell, rows.Count, cell, gridCount, format, firstRow, lastRow, firstColumn, lastColumn, noRowBands, noColumnBands);
                var layers = new List<OpenXmlElement>();
                foreach (var style in chain)
                {
                    if (style.Element is not null)
                        layers.Add(style.Element);
                }
                foreach (var type in types)
                {
                    foreach (var style in chain)
                    {
                        if (style.Conditional.TryGetValue(type, out var conditional))
                            layers.Add(conditional);
                    }
                }
                var cellWidth = grid.Skip(cell.GridStart).Take(cell.Span).Sum() / gridTotal * tableWidth;
                var (cellCss, contentWidth) = CellCss(cell, r == 0, lastRowOfCell == rows.Count - 1, gridCount, format, chain, types, cellWidth);
                html.Append("<td class=\"").Append(_css.For("d", cellCss)).Append('"');
                if (cell.Span > 1)
                    html.Append(" colspan=\"").Append(cell.Span.ToString(CultureInfo.InvariantCulture)).Append('"');
                if (cell.RowSpan > 1)
                    html.Append(" rowspan=\"").Append(cell.RowSpan.ToString(CultureInfo.InvariantCulture)).Append('"');
                html.Append('>');
                var key = (styleId ?? "") + ":" + string.Join(",", types);
                RenderBlocks(cell.Cell.ChildElements.Where(c => c.LocalName != "tcPr"), html,
                    new Scope(scope.Story, contentWidth) { TableLayers = layers, TableKey = key });
                html.Append("</td>");
            }
            html.Append("</tr>");
        }
        html.Append("</tbody></table>\n");
    }

    /// <summary>The cell a "continue" cell merges into: the one above it, or (when that is covered too) the one drawing it.</summary>
    static CellModel? FindAbove(List<RowModel> rows, int row, int gridStart)
    {
        if (row == 0 || rows[row - 1].Cells.FirstOrDefault(c => c.GridStart == gridStart) is not { } cell)
            return null;
        if (cell.Covered)
            return cell.Owner;
        return cell.VerticalMerge is "restart" or "continue" ? cell : null;
    }

    /// <summary>The table style's conditional formats for a cell, lowest priority first.</summary>
    static List<string> ConditionalTypes(int row, int lastRowOfCell, int rowCount, CellModel cell, int gridCount, TableFormat format,
        bool firstRow, bool lastRow, bool firstColumn, bool lastColumn, bool noRowBands, bool noColumnBands)
    {
        var types = new List<string>();
        var isFirstColumn = cell.GridStart == 0;
        var isLastColumn = cell.GridStart + cell.Span >= gridCount;
        var isFirstRow = row == 0;
        var isLastRow = lastRowOfCell == rowCount - 1;
        if (!noColumnBands && !(firstColumn && isFirstColumn) && !(lastColumn && isLastColumn))
        {
            var index = (cell.GridStart - (firstColumn ? 1 : 0)) / format.ColumnBand;
            types.Add(index % 2 == 0 ? "band1Vert" : "band2Vert");
        }
        if (!noRowBands && !(firstRow && isFirstRow) && !(lastRow && isLastRow))
        {
            var index = (row - (firstRow ? 1 : 0)) / format.RowBand;
            types.Add(index % 2 == 0 ? "band1Horz" : "band2Horz");
        }
        if (firstColumn && isFirstColumn)
            types.Add("firstCol");
        if (lastColumn && isLastColumn)
            types.Add("lastCol");
        if (firstRow && isFirstRow)
            types.Add("firstRow");
        if (lastRow && isLastRow)
            types.Add("lastRow");
        if (firstRow && isFirstRow && lastColumn && isLastColumn)
            types.Add("neCell");
        if (firstRow && isFirstRow && firstColumn && isFirstColumn)
            types.Add("nwCell");
        if (lastRow && isLastRow && lastColumn && isLastColumn)
            types.Add("seCell");
        if (lastRow && isLastRow && firstColumn && isFirstColumn)
            types.Add("swCell");
        return types;
    }

    /// <summary>A cell's borders, shading, padding and alignment; and the width left for its content.</summary>
    (string Css, double ContentWidth) CellCss(CellModel cell, bool isFirstRow, bool isLastRow, int gridCount, TableFormat format,
        IReadOnlyList<WordStyle> chain, IReadOnlyList<string> types, double width)
    {
        var isFirstColumn = cell.GridStart == 0;
        var isLastColumn = cell.GridStart + cell.Span >= gridCount;
        var top = isFirstRow ? format.Top : format.InsideH;
        var bottom = isLastRow ? format.Bottom : format.InsideH;
        var left = isFirstColumn ? format.Left : format.InsideV;
        var right = isLastColumn ? format.Right : format.InsideV;
        string? shading = null;
        string? verticalAlign = null;
        double padTop = format.PadTop, padBottom = format.PadBottom, padLeft = format.PadLeft, padRight = format.PadRight;
        var noWrap = false;

        void ApplyCell(OpenXmlElement? properties, bool conditional)
        {
            if (properties is null)
                return;
            if (properties.Child("tcBorders") is { } borders)
            {
                top = WordBorder.From(borders.Child("top"), _theme) ?? top;
                bottom = WordBorder.From(borders.Child("bottom"), _theme) ?? bottom;
                left = WordBorder.From(borders.Child("left") ?? borders.Child("start"), _theme) ?? left;
                right = WordBorder.From(borders.Child("right") ?? borders.Child("end"), _theme) ?? right;
                if (conditional && WordBorder.From(borders.Child("insideV"), _theme) is { } insideV)
                {
                    if (!isFirstColumn)
                        left = insideV;
                    if (!isLastColumn)
                        right = insideV;
                }
            }
            if (properties.Child("shd") is { } shd)
                shading = WordRunFormat.ShadingColor(shd, _theme) ?? shading;
            if (properties.Child("vAlign").Attr("val") is { } align)
                verticalAlign = align;
            if (properties.Child("tcMar") is { } margins)
            {
                padTop = Twips(margins.Child("top").Attr("w")) ?? padTop;
                padBottom = Twips(margins.Child("bottom").Attr("w")) ?? padBottom;
                padLeft = Twips((margins.Child("left") ?? margins.Child("start")).Attr("w")) ?? padLeft;
                padRight = Twips((margins.Child("right") ?? margins.Child("end")).Attr("w")) ?? padRight;
            }
            if (properties.Child("noWrap") is { } wrap)
                noWrap = IsOn(wrap);
        }

        foreach (var style in chain)
            ApplyCell(style.CellProperties, conditional: false);
        foreach (var type in types)
        {
            foreach (var style in chain)
            {
                if (style.Conditional.TryGetValue(type, out var conditional))
                {
                    ApplyCell(conditional.Child("tcPr"), conditional: true);
                    // Conditional table properties may carry borders for the region (e.g. a header row's bottom line).
                    if (conditional.Child("tblPr").Child("tblBorders") is { } regionBorders)
                    {
                        top = isFirstRow ? WordBorder.From(regionBorders.Child("top"), _theme) ?? top : top;
                        bottom = WordBorder.From(regionBorders.Child("bottom"), _theme) ?? bottom;
                    }
                }
            }
        }
        ApplyCell(cell.Cell.Child("tcPr"), conditional: false);

        var css = new StringBuilder();
        css.Append("border-top: ").Append(top?.Css ?? "none").Append("; ");
        css.Append("border-bottom: ").Append(bottom?.Css ?? "none").Append("; ");
        css.Append("border-left: ").Append(left?.Css ?? "none").Append("; ");
        css.Append("border-right: ").Append(right?.Css ?? "none").Append("; ");
        css.Append("padding: ").Append(Pt(padTop)).Append(' ').Append(Pt(padRight)).Append(' ').Append(Pt(padBottom)).Append(' ').Append(Pt(padLeft)).Append("; ");
        if (shading is not null)
            css.Append("background-color: ").Append(shading).Append("; ");
        css.Append("vertical-align: ").Append(verticalAlign switch { "center" => "middle", "bottom" => "bottom", _ => "top" }).Append("; ");
        if (noWrap)
            css.Append("white-space: nowrap; ");
        return (css.ToString().TrimEnd(), Math.Max(18, width - padLeft - padRight));
    }
}
