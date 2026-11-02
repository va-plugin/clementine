using System.Globalization;
using System.Text.RegularExpressions;
using Clementine.Engines.Documents.OpenXml;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace Clementine.Engines.Documents.Spreadsheets;

/// <summary>
/// A worksheet's non-empty cell values as display strings, kept sparse: a stray cell in XFD1048576 must not turn into a
/// 17-billion-cell grid. <see cref="Rows"/> produces the rows one at a time.
/// </summary>
internal sealed class SheetData(string name, SortedDictionary<int, List<(int Column, string Value)>> cells)
{
    /// <summary>Above this many padded cells (and mostly empty), rows are no longer padded to the same width.</summary>
    public const long MaxPaddedCells = 20_000_000;

    public string Name { get; } = name;

    public bool IsEmpty => cells.Count == 0;

    /// <summary>Rows from 1 to the last row with data (leading and in-between empty rows count).</summary>
    public int RowCount => cells.Count == 0 ? 0 : cells.Keys.Max();

    /// <summary>Columns from A to the right-most column with data.</summary>
    public int Width { get; } = cells.Count == 0 ? 0 : cells.Values.Max(r => r.Max(c => c.Column));

    public long CellCount { get; } = cells.Values.Sum(r => (long)r.Count);

    /// <summary>
    /// True when padding every row to <see cref="Width"/> would mostly write empty cells into an enormous file; rows then
    /// end at their own last value (every value still lands in its own row and column).
    /// </summary>
    public bool IsRagged => (long)RowCount * Width > Math.Max(MaxPaddedCells, CellCount * 4);

    /// <summary>Every row from 1 to <see cref="RowCount"/>, empty rows included, built lazily.</summary>
    public IEnumerable<IReadOnlyList<string>> Rows()
    {
        var ragged = IsRagged;
        var next = 1;
        foreach (var (index, row) in cells)
        {
            for (; next < index; next++)
                yield return ragged ? [] : Enumerable.Repeat("", Width).ToArray();
            var values = Enumerable.Repeat("", ragged ? row.Max(c => c.Column) : Width).ToArray();
            foreach (var (column, value) in row)
                values[column - 1] = value;
            yield return values;
            next = index + 1;
        }
    }
}

/// <summary>
/// Reads .xlsx/.xlsm with OpenXML in streaming mode. Values come out the way a CSV should hold them: shared and inline
/// strings, booleans as TRUE/FALSE, errors as shown (#DIV/0!), dates as ISO 8601, numbers in invariant culture.
/// </summary>
internal static partial class XlsxReader
{
    const uint MaxRows = 1_048_576;
    const int MaxColumns = 16_384;

    /// <summary>Visible worksheets in workbook order (chart sheets and hidden sheets are skipped).</summary>
    public static IReadOnlyList<SheetData> Read(string path, CancellationToken cancellationToken)
    {
        using var document = OpenXmlFiles.OpenSpreadsheet(path);
        try
        {
            return Read(document, cancellationToken);
        }
        catch (Exception ex) when (ex is not (OperationException or OperationCanceledException))
        {
            // A damaged part, a Word file renamed to .xlsx…: never the SDK's own message.
            throw new OperationException($"Couldn't read \"{Path.GetFileName(path)}\" — it's damaged or isn't really an Excel workbook.", ex.ToString(), ex);
        }
    }

    static IReadOnlyList<SheetData> Read(SpreadsheetDocument document, CancellationToken cancellationToken)
    {
        var workbookPart = document.WorkbookPart ?? throw new OperationException("This workbook has no sheets.");
        var sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable?.Elements<SharedStringItem>().Select(i => i.InnerText).ToList() ?? [];
        var dateStyles = DateStyles(workbookPart);
        var sheets = new List<SheetData>();

        foreach (var sheet in workbookPart.Workbook?.Sheets?.Elements<Sheet>() ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sheet.State?.Value is { } state && state != SheetStateValues.Visible)
                continue;
            if (sheet.Id?.Value is not { } id || workbookPart.GetPartById(id) is not WorksheetPart worksheet)
                continue;
            sheets.Add(new SheetData(sheet.Name?.Value ?? $"Sheet{sheets.Count + 1}", ReadRows(worksheet, sharedStrings, dateStyles, cancellationToken)));
        }
        if (sheets.Count == 0)
            throw new OperationException("This workbook has no visible worksheets.");
        return sheets;
    }

    static SortedDictionary<int, List<(int Column, string Value)>> ReadRows(WorksheetPart worksheet, List<string> sharedStrings, DateStyleMap dateStyles, CancellationToken cancellationToken)
    {
        var rows = new SortedDictionary<int, List<(int Column, string Value)>>();
        var nextRow = 1;
        using (var reader = OpenXmlReader.Create(worksheet))
        {
            while (reader.Read())
            {
                if (reader.ElementType != typeof(Row) || !reader.IsStartElement)
                    continue;
                cancellationToken.ThrowIfCancellationRequested();
                var row = (Row)reader.LoadCurrentElement()!;
                // Excel's limits: 1,048,576 rows × 16,384 columns. Damaged files may claim more (or overflow).
                var rowIndex = row.RowIndex?.Value is { } declared ? (int)Math.Clamp(declared, 1U, MaxRows) : nextRow;
                nextRow = Math.Min(rowIndex + 1, (int)MaxRows);
                var cells = new List<(int, string)>();
                var nextColumn = 1;
                foreach (var cell in row.Elements<Cell>())
                {
                    var column = cell.CellReference?.Value is { } reference ? ColumnIndex(reference) : nextColumn;
                    nextColumn = column + 1;
                    var value = CellText(cell, sharedStrings, dateStyles);
                    if (value.Length > 0)
                        cells.Add((column, value));
                }
                if (cells.Count == 0)
                    continue;
                if (rows.TryGetValue(rowIndex, out var existing))
                    existing.AddRange(cells);
                else
                    rows[rowIndex] = cells;
            }
        }

        return rows;
    }

    static string CellText(Cell cell, List<string> sharedStrings, DateStyleMap dateStyles)
    {
        var raw = cell.CellValue?.Text;
        var type = cell.DataType?.Value;
        if (type == CellValues.SharedString)
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < sharedStrings.Count ? sharedStrings[index] : "";
        if (type == CellValues.InlineString)
            return cell.InlineString?.InnerText ?? "";
        if (raw is null)
            return "";
        if (type == CellValues.Boolean)
            return raw == "1" ? "TRUE" : "FALSE";
        if (type == CellValues.Error || type == CellValues.String || type == CellValues.Date)
            return raw;
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return raw;
        var style = (int)(cell.StyleIndex?.Value ?? 0);
        return dateStyles.Kind(style) switch
        {
            DateKind.Date => FromOADate(number).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateKind.DateTime => FormatDateTime(number),
            DateKind.Time => TimeSpan.FromDays(number % 1).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture),
            _ => FormatNumber(number),
        };
    }

    static string FormatDateTime(double serial)
    {
        var value = FromOADate(serial);
        return value.TimeOfDay == TimeSpan.Zero
            ? value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    static DateTime FromOADate(double serial)
    {
        try
        {
            // Round to the second so 0.99999999 doesn't print as 23:59:59.
            return DateTime.FromOADate(Math.Round(serial * 86400) / 86400);
        }
        catch (ArgumentException)
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>Invariant, without binary floating-point noise (0.30000000000000004 → 0.3).</summary>
    public static string FormatNumber(double number)
    {
        if (Math.Abs(number) < 1e15 && number == Math.Floor(number))
            return number.ToString("0", CultureInfo.InvariantCulture);
        var text = number.ToString("G15", CultureInfo.InvariantCulture);
        return text.Contains('E', StringComparison.Ordinal) ? number.ToString("R", CultureInfo.InvariantCulture) : text;
    }

    /// <summary>1-based column number of "BC12".</summary>
    public static int ColumnIndex(string reference)
    {
        var column = 0;
        foreach (var c in reference)
        {
            if (!char.IsAsciiLetter(c))
                break;
            column = column * 26 + (char.ToUpperInvariant(c) - 'A' + 1);
            if (column > MaxColumns)
                return MaxColumns;
        }
        return Math.Max(1, column);
    }

    enum DateKind
    {
        None,
        Date,
        DateTime,
        Time,
    }

    sealed class DateStyleMap(IReadOnlyList<DateKind> kinds)
    {
        public DateKind Kind(int styleIndex) => styleIndex >= 0 && styleIndex < kinds.Count ? kinds[styleIndex] : DateKind.None;
    }

    static DateStyleMap DateStyles(WorkbookPart workbook)
    {
        var stylesheet = workbook.WorkbookStylesPart?.Stylesheet;
        var custom = stylesheet?.NumberingFormats?.Elements<NumberingFormat>()
            .Where(f => f.NumberFormatId?.Value is not null)
            .ToDictionary(f => (int)f.NumberFormatId!.Value, f => f.FormatCode?.Value ?? "") ?? [];
        var kinds = stylesheet?.CellFormats?.Elements<CellFormat>()
            .Select(f => KindOfFormat((int)(f.NumberFormatId?.Value ?? 0), custom))
            .ToList() ?? [];
        return new DateStyleMap(kinds);
    }

    static DateKind KindOfFormat(int id, IReadOnlyDictionary<int, string> custom)
    {
        switch (id)
        {
            case >= 14 and <= 17 or >= 27 and <= 31 or >= 34 and <= 36 or >= 50 and <= 58:
                return DateKind.Date;
            case 22:
                return DateKind.DateTime;
            case >= 18 and <= 21 or >= 45 and <= 47 or 32 or 33:
                return DateKind.Time;
        }
        if (!custom.TryGetValue(id, out var code) || code.Length == 0)
            return DateKind.None;
        // Strip quoted literals, escapes and [colour]/[$-locale] sections before looking for date tokens.
        var bare = FormatNoise().Replace(code, "").ToLowerInvariant();
        var hasDate = bare.Contains('y') || bare.Contains('d');
        var hasTime = bare.Contains('h') || bare.Contains('s');
        return (hasDate, hasTime) switch
        {
            (true, true) => DateKind.DateTime,
            (true, false) => DateKind.Date,
            (false, true) => DateKind.Time,
            _ => DateKind.None,
        };
    }

    [GeneratedRegex("\"[^\"]*\"|\\\\.|\\[[^\\]]*\\]")]
    private static partial Regex FormatNoise();
}
