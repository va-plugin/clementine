using System.Text;

namespace Clementine.Engines.Files.Data;

/// <summary>RFC 4180 CSV reading (quoted fields, embedded newlines, delimiter sniffing, Excel's "sep=" line) and writing.</summary>
internal static class Csv
{
    static readonly char[] Candidates = [',', ';', '\t', '|'];

    /// <summary>Parses CSV text into rows. Blank lines are skipped.</summary>
    public static List<List<string>> Parse(string text, char? delimiter = null)
    {
        text = text.TrimStart('\uFEFF');
        if (text.StartsWith("sep=", StringComparison.OrdinalIgnoreCase) && text.Length > 4)
        {
            delimiter ??= text[4];
            var end = text.IndexOf('\n');
            text = end < 0 ? "" : text[(end + 1)..];
        }
        var separator = delimiter ?? DetectDelimiter(text);

        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var fieldStarted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(c);
                }
                continue;
            }

            if (c == '"' && field.Length == 0 && !fieldStarted)
            {
                inQuotes = true;
                fieldStarted = true;
            }
            else if (c == separator)
            {
                row.Add(field.ToString());
                field.Clear();
                fieldStarted = false;
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                EndRow();
            }
            else
            {
                field.Append(c);
                fieldStarted = true;
            }
        }
        if (field.Length > 0 || row.Count > 0 || fieldStarted)
            EndRow();
        return rows;

        void EndRow()
        {
            row.Add(field.ToString());
            field.Clear();
            fieldStarted = false;
            if (row.Count > 1 || row[0].Length > 0)
                rows.Add(row);
            row = [];
        }
    }

    /// <summary>The candidate (comma, semicolon, tab, pipe) that splits the first lines most consistently.</summary>
    public static char DetectDelimiter(string text)
    {
        var lines = SampleLines(text, 20);
        if (lines.Count == 0)
            return ',';
        var best = ',';
        var bestScore = 0.0;
        foreach (var candidate in Candidates)
        {
            var counts = lines.Select(l => CountOutsideQuotes(l, candidate)).ToList();
            var mode = counts.Where(c => c > 0).GroupBy(c => c).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).FirstOrDefault();
            if (mode is null)
                continue;
            var consistency = (double)mode.Count() / lines.Count;
            var score = consistency * 100 + Math.Min(mode.Key, 50);
            if (score > bestScore)
                (best, bestScore) = (candidate, score);
        }
        return best;
    }

    /// <summary>Writes rows with CRLF line endings, quoting only fields that need it.</summary>
    public static string Write(IEnumerable<IReadOnlyList<string>> rows, char delimiter = ',')
    {
        var text = new StringBuilder();
        foreach (var row in rows)
        {
            for (var i = 0; i < row.Count; i++)
            {
                if (i > 0)
                    text.Append(delimiter);
                AppendField(text, row[i], delimiter);
            }
            text.Append("\r\n");
        }
        return text.ToString();
    }

    static void AppendField(StringBuilder text, string value, char delimiter)
    {
        var needsQuotes = value.Length > 0 && (value.IndexOfAny([delimiter, '"', '\r', '\n']) >= 0 || char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]));
        if (!needsQuotes)
        {
            text.Append(value);
            return;
        }
        text.Append('"').Append(value.Replace("\"", "\"\"")).Append('"');
    }

    static List<string> SampleLines(string text, int count)
    {
        var lines = new List<string>();
        var inQuotes = false;
        var start = 0;
        for (var i = 0; i < text.Length && lines.Count < count; i++)
        {
            if (text[i] == '"')
                inQuotes = !inQuotes;
            else if (text[i] == '\n' && !inQuotes)
            {
                var line = text[start..i].TrimEnd('\r');
                if (line.Length > 0)
                    lines.Add(line);
                start = i + 1;
            }
        }
        if (lines.Count < count && start < text.Length)
        {
            var last = text[start..].TrimEnd('\r', '\n');
            if (last.Length > 0)
                lines.Add(last);
        }
        return lines;
    }

    static int CountOutsideQuotes(string line, char delimiter)
    {
        var count = 0;
        var inQuotes = false;
        foreach (var c in line)
        {
            if (c == '"')
                inQuotes = !inQuotes;
            else if (c == delimiter && !inQuotes)
                count++;
        }
        return count;
    }
}
