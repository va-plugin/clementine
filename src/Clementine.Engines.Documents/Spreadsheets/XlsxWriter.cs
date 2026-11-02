using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace Clementine.Engines.Documents.Spreadsheets;

/// <summary>
/// Writes a single-sheet .xlsx from rows of text with OpenXML (streaming, so big CSVs are fine). Numbers become real
/// numeric cells (but "00123" and long digit strings such as phone numbers stay text); a header row is bolded and frozen.
/// </summary>
internal static class XlsxWriter
{
    public const int MaxRows = 1_048_576;
    public const int MaxColumns = 16_384;

    public static void Write(string path, string sheetName, IReadOnlyList<IReadOnlyList<string>> rows, CancellationToken cancellationToken)
    {
        if (rows.Count > MaxRows)
            throw new OperationException($"This file has {rows.Count:N0} rows — more than Excel's limit of {MaxRows:N0}.");
        var columns = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
        if (columns > MaxColumns)
            throw new OperationException($"This file has {columns:N0} columns — more than Excel's limit of {MaxColumns:N0}.");
        var header = LooksLikeHeader(rows);

        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var styles = workbookPart.AddNewPart<WorkbookStylesPart>();
        styles.Stylesheet = Stylesheet();

        var sharedStrings = new Dictionary<string, int>(StringComparer.Ordinal);
        var stringList = new List<string>();
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        using (var writer = OpenXmlWriter.Create(worksheetPart))
        {
            writer.WriteStartElement(new Worksheet());
            if (header)
            {
                writer.WriteStartElement(new SheetViews());
                writer.WriteStartElement(new SheetView { WorkbookViewId = 0U });
                writer.WriteElement(new Pane { VerticalSplit = 1D, TopLeftCell = "A2", ActivePane = PaneValues.BottomLeft, State = PaneStateValues.Frozen });
                writer.WriteEndElement();
                writer.WriteEndElement();
            }
            WriteColumnWidths(writer, rows, columns);
            writer.WriteStartElement(new DocumentFormat.OpenXml.Spreadsheet.SheetData());
            for (var r = 0; r < rows.Count; r++)
            {
                if (r % 5000 == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                var row = rows[r];
                writer.WriteStartElement(new Row { RowIndex = (uint)(r + 1) });
                for (var c = 0; c < row.Count; c++)
                {
                    var value = row[c];
                    if (value.Length == 0)
                        continue;
                    var cell = new Cell { CellReference = Reference(c, r) };
                    if (header && r == 0)
                        cell.StyleIndex = 1U;
                    if (!(header && r == 0) && TryNumber(value, out var number))
                    {
                        cell.CellValue = new CellValue(number.ToString("R", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        if (!sharedStrings.TryGetValue(value, out var index))
                        {
                            index = stringList.Count;
                            sharedStrings[value] = index;
                            stringList.Add(value);
                        }
                        cell.DataType = CellValues.SharedString;
                        cell.CellValue = new CellValue(index.ToString(CultureInfo.InvariantCulture));
                    }
                    writer.WriteElement(cell);
                }
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
            if (header && columns > 0)
                writer.WriteElement(new AutoFilter { Reference = $"A1:{Reference(columns - 1, Math.Max(0, rows.Count - 1))}" });
            writer.WriteEndElement();
        }

        var sharedStringPart = workbookPart.AddNewPart<SharedStringTablePart>();
        using (var writer = OpenXmlWriter.Create(sharedStringPart))
        {
            writer.WriteStartElement(new SharedStringTable { Count = (uint)stringList.Count, UniqueCount = (uint)stringList.Count });
            foreach (var text in stringList)
            {
                writer.WriteStartElement(new SharedStringItem());
                writer.WriteElement(new Text(Sanitize(text)) { Space = SpaceProcessingModeValues.Preserve });
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
        }

        workbookPart.Workbook.AppendChild(new Sheets(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = 1U,
            Name = SheetName(sheetName),
        }));
        if (header && columns > 0)
        {
            workbookPart.Workbook.AppendChild(new DefinedNames(new DefinedName($"'{SheetName(sheetName).Replace("'", "''", StringComparison.Ordinal)}'!$A$1:${ColumnName(columns - 1)}${Math.Max(1, rows.Count)}")
            {
                Name = "_xlnm._FilterDatabase",
                LocalSheetId = 0U,
                Hidden = true,
            }));
        }
        workbookPart.Workbook.Save();
    }

    /// <summary>Numbers that survive a round trip: no leading zeros, no thousands separators, ≤ 15 significant digits.</summary>
    public static bool TryNumber(string text, out double number)
    {
        number = 0;
        var value = text.Trim();
        if (value.Length == 0 || value.Length > 20 || value != text)
            return false;
        var digits = value.TrimStart('-', '+');
        if (digits.Length > 1 && digits[0] == '0' && digits[1] != '.')
            return false;
        if (digits.Count(char.IsAsciiDigit) > 15)
            return false;
        if (!value.All(c => char.IsAsciiDigit(c) || c is '.' or '-' or '+' or 'e' or 'E'))
            return false;
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number);
    }

    static bool LooksLikeHeader(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        if (rows.Count < 2 || rows[0].Count == 0)
            return false;
        var first = rows[0];
        return first.All(v => v.Length > 0 && !TryNumber(v, out _)) && first.Distinct(StringComparer.OrdinalIgnoreCase).Count() == first.Count;
    }

    static void WriteColumnWidths(OpenXmlWriter writer, IReadOnlyList<IReadOnlyList<string>> rows, int columns)
    {
        if (columns == 0)
            return;
        var widths = new double[columns];
        foreach (var row in rows.Take(2000))
        {
            for (var c = 0; c < row.Count; c++)
                widths[c] = Math.Max(widths[c], row[c].Split('\n')[0].Length);
        }
        writer.WriteStartElement(new Columns());
        for (var c = 0; c < columns; c++)
        {
            writer.WriteElement(new Column
            {
                Min = (uint)(c + 1),
                Max = (uint)(c + 1),
                Width = Math.Clamp(widths[c] * 1.1 + 2, 6, 60),
                CustomWidth = true,
            });
        }
        writer.WriteEndElement();
    }

    static Stylesheet Stylesheet() => new(
        new Fonts(new Font(new FontSize { Val = 11 }, new FontName { Val = "Calibri" }),
            new Font(new Bold(), new FontSize { Val = 11 }, new FontName { Val = "Calibri" })) { Count = 2U },
        new Fills(new Fill(new PatternFill { PatternType = PatternValues.None }), new Fill(new PatternFill { PatternType = PatternValues.Gray125 })) { Count = 2U },
        new Borders(new Border(new LeftBorder(), new RightBorder(), new TopBorder(), new BottomBorder(), new DiagonalBorder())) { Count = 1U },
        new CellStyleFormats(new CellFormat { NumberFormatId = 0U, FontId = 0U, FillId = 0U, BorderId = 0U }) { Count = 1U },
        new CellFormats(
            new CellFormat { NumberFormatId = 0U, FontId = 0U, FillId = 0U, BorderId = 0U, FormatId = 0U },
            new CellFormat { NumberFormatId = 0U, FontId = 1U, FillId = 0U, BorderId = 0U, FormatId = 0U, ApplyFont = true }) { Count = 2U });

    static string Reference(int column, int row) => ColumnName(column) + (row + 1).ToString(CultureInfo.InvariantCulture);

    static string ColumnName(int column)
    {
        var name = "";
        for (var n = column + 1; n > 0; n = (n - 1) / 26)
            name = (char)('A' + (n - 1) % 26) + name;
        return name;
    }

    /// <summary>Excel sheet names: ≤ 31 characters, none of : \ / ? * [ ].</summary>
    static string SheetName(string name)
    {
        var cleaned = new string(name.Select(c => c is ':' or '\\' or '/' or '?' or '*' or '[' or ']' ? '_' : c).ToArray()).Trim('\'').Trim();
        if (cleaned.Length == 0)
            cleaned = "Sheet1";
        return cleaned.Length > 31 ? cleaned[..31] : cleaned;
    }

    static string Sanitize(string text) =>
        text.Any(c => c < 0x20 && c is not ('\t' or '\n' or '\r')) ? new string(text.Where(c => c >= 0x20 || c is '\t' or '\n' or '\r').ToArray()) : text;
}
