using System.Text.RegularExpressions;

namespace Clementine.Engines.Files.Data;

/// <summary>A CSV-shaped table plus anything worth telling the user about the conversion.</summary>
internal sealed record Table(IReadOnlyList<string>? Header, IReadOnlyList<IReadOnlyList<string>> Rows, IReadOnlyList<string> Warnings);

/// <summary>
/// JSON ↔ CSV. Arrays of objects become one row per object, with nested objects flattened into "address.city"
/// columns and arrays kept as JSON text; CSV → JSON reverses both, and infers numbers/booleans conservatively
/// (no leading zeros, ≤ 15 digits, so ZIP codes and IDs stay strings).
/// </summary>
internal static partial class TableConversion
{
    public static Table ToTable(DataNode root)
    {
        var warnings = new List<string>();
        var records = RecordsOf(root, warnings);

        if (records.All(r => r is DataArray))
        {
            // An array of arrays is already a table.
            var raw = records.Cast<DataArray>().Select(a => (IReadOnlyList<string>)a.Items.Select(Cell).ToList()).ToList();
            return Guard(new Table(null, raw, warnings));
        }

        var columns = new List<string>();
        var known = new HashSet<string>(StringComparer.Ordinal);
        var flattened = new List<Dictionary<string, string>>();
        foreach (var record in records)
        {
            var row = new Dictionary<string, string>(StringComparer.Ordinal);
            if (record is DataObject obj)
                Flatten(obj, "", row, 0);
            else
                row["value"] = Cell(record);
            foreach (var key in row.Keys)
            {
                if (known.Add(key))
                    columns.Add(key);
            }
            flattened.Add(row);
        }
        var rows = flattened.Select(r => (IReadOnlyList<string>)columns.Select(c => r.GetValueOrDefault(c, "")).ToList()).ToList();
        return Guard(new Table(columns, rows, warnings));
    }

    static List<DataNode> RecordsOf(DataNode root, List<string> warnings)
    {
        switch (root)
        {
            case DataArray array:
                return array.Items;
            case DataObject obj when obj.Properties.Count(p => p.Value is DataArray) == 1
                                  && obj.Properties.Single(p => p.Value is DataArray).Value is DataArray { Items.Count: > 0 } inner
                                  && inner.Items.All(i => i is DataObject):
                // API responses: { "data": [ {...}, {...} ], "page": 1 }
                var name = obj.Properties.Single(p => p.Value is DataArray).Key;
                warnings.Add($"Used the \"{name}\" list inside the JSON object as the table rows.");
                return inner.Items;
            case DataObject obj:
                return [obj];
            default:
                throw new OperationException("This JSON is a single value, not a list of records, so there's no table to make.");
        }
    }

    static void Flatten(DataObject obj, string prefix, Dictionary<string, string> row, int depth)
    {
        if (depth > DataLimits.MaxDepth)
            throw DataLimits.TooDeep();
        foreach (var (key, value) in obj.Properties)
        {
            var column = prefix.Length == 0 ? key : $"{prefix}.{key}";
            if (value is DataObject { Properties.Count: > 0 } nested)
                Flatten(nested, column, row, depth + 1);
            else
                row[column] = Cell(value);
        }
    }

    static string Cell(DataNode node) => node switch
    {
        DataString s => s.Value,
        DataNumber n => n.Raw,
        DataBool b => b.Value ? "true" : "false",
        DataNull => "",
        _ => JsonTextWriter.Write(node, indent: false),
    };

    /// <summary>
    /// CSV injection: a cell starting with = + - @ (or tab/CR) runs as a formula when the CSV is opened in Excel.
    /// Such text cells get a leading apostrophe; numbers like "-5" are left alone.
    /// </summary>
    static Table Guard(Table table)
    {
        var escaped = 0;
        var rows = table.Rows.Select(r => (IReadOnlyList<string>)r.Select(cell =>
        {
            if (cell.Length == 0 || cell[0] is not ('=' or '+' or '-' or '@' or '\t' or '\r') || NumberCell().IsMatch(cell))
                return cell;
            escaped++;
            return "'" + cell;
        }).ToList()).ToList();
        if (escaped == 0)
            return table;
        var warnings = table.Warnings.Append(
            $"{escaped} cell(s) starting with = + - or @ got a leading ' so spreadsheet apps don't run them as formulas.").ToList();
        return table with { Rows = rows, Warnings = warnings };
    }

    /// <summary>CSV rows (first row = header) → an array of objects.</summary>
    public static DataArray FromRows(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var result = new DataArray();
        if (rows.Count == 0)
            return result;

        var header = UniqueHeader(rows[0]);
        var nestable = NestableColumns(header);
        foreach (var row in rows.Skip(1))
        {
            var obj = new DataObject();
            var width = Math.Max(header.Count, row.Count);
            for (var i = 0; i < width; i++)
            {
                var key = i < header.Count ? header[i] : $"column{i + 1}";
                var value = i < row.Count ? Typed(row[i]) : DataNull.Instance;
                if (nestable.Contains(key))
                    SetNested(obj, key.Split('.'), value);
                else
                    obj.Add(key, value);
            }
            result.Items.Add(obj);
        }
        return result;
    }

    static List<string> UniqueHeader(IReadOnlyList<string> raw)
    {
        var header = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < raw.Count; i++)
        {
            var name = raw[i].Trim();
            if (name.Length == 0)
                name = $"column{i + 1}";
            var unique = name;
            for (var n = 2; !seen.Add(unique); n++)
                unique = $"{name}_{n}";
            header.Add(unique);
        }
        return header;
    }

    /// <summary>Dotted columns that can be nested without clashing ("a" and "a.b" together can't).</summary>
    static HashSet<string> NestableColumns(List<string> header)
    {
        var all = new HashSet<string>(header, StringComparer.Ordinal);
        var nestable = new HashSet<string>(StringComparer.Ordinal);
        foreach (var column in header.Where(h => h.Contains('.')))
        {
            var parts = column.Split('.');
            if (parts.Any(p => p.Length == 0))
                continue;
            var clash = false;
            for (var i = 1; i < parts.Length && !clash; i++)
                clash = all.Contains(string.Join('.', parts[..i]));
            if (!clash)
                nestable.Add(column);
        }
        return nestable;
    }

    static void SetNested(DataObject obj, string[] path, DataNode value)
    {
        var current = obj;
        for (var i = 0; i < path.Length - 1; i++)
        {
            if (current.Get(path[i]) is not DataObject child)
            {
                child = new DataObject();
                current.Set(path[i], child);
            }
            current = child;
        }
        current.Set(path[^1], value);
    }

    /// <summary>Empty → null; true/false; plain integers/decimals; JSON arrays/objects; everything else stays text.</summary>
    internal static DataNode Typed(string cell)
    {
        if (cell.Length == 0)
            return DataNull.Instance;
        if (cell is "true" or "TRUE" or "True")
            return DataBool.True;
        if (cell is "false" or "FALSE" or "False")
            return DataBool.False;
        if (SafeNumber().IsMatch(cell) && cell.Count(char.IsAsciiDigit) <= 15)
            return new DataNumber(cell);
        if ((cell[0] == '[' && cell[^1] == ']') || (cell[0] == '{' && cell[^1] == '}'))
        {
            try
            {
                return JsonData.Parse(cell);
            }
            catch (OperationException)
            {
            }
        }
        return new DataString(cell);
    }

    [GeneratedRegex(@"^-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?$")]
    private static partial Regex SafeNumber();

    [GeneratedRegex(@"^[-+]?(?:[0-9]+\.?[0-9]*|\.[0-9]+)(?:[eE][-+]?[0-9]+)?$")]
    private static partial Regex NumberCell();
}
