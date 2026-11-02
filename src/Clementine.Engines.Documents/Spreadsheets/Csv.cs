using System.Text;

namespace Clementine.Engines.Documents.Spreadsheets;

/// <summary>RFC 4180 CSV reading (quoted fields, embedded newlines, delimiter sniffing) and writing.</summary>
internal static class Csv
{
    static readonly char[] CandidateDelimiters = [',', ';', '\t', '|'];

    /// <summary>Guesses the delimiter from the first lines: the candidate that splits them most consistently.</summary>
    public static char DetectDelimiter(string text, string extension)
    {
        if (extension.Equals(".tsv", StringComparison.OrdinalIgnoreCase))
            return '\t';
        var sample = text.Length > 64_000 ? text[..64_000] : text;
        var best = ',';
        var bestScore = 0.0;
        foreach (var delimiter in CandidateDelimiters)
        {
            var rows = Parse(sample, delimiter).Take(20).Select(r => r.Count).ToList();
            if (rows.Count == 0)
                continue;
            var columns = rows[0];
            if (columns < 2)
                continue;
            var consistent = rows.Count(c => c == columns) / (double)rows.Count;
            var score = consistent * Math.Min(columns, 50);
            if (score > bestScore)
            {
                bestScore = score;
                best = delimiter;
            }
        }
        return best;
    }

    /// <summary>Rows of fields. A trailing newline doesn't produce an empty row.</summary>
    public static IEnumerable<List<string>> Parse(string text, char delimiter)
    {
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
            else if (c == delimiter)
            {
                row.Add(field.ToString());
                field.Clear();
                fieldStarted = false;
            }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                row.Add(field.ToString());
                field.Clear();
                fieldStarted = false;
                yield return row;
                row = [];
            }
            else
            {
                field.Append(c);
                fieldStarted = true;
            }
        }
        if (field.Length > 0 || row.Count > 0 || fieldStarted)
        {
            row.Add(field.ToString());
            yield return row;
        }
    }

    /// <summary>One CSV line (no newline) with fields quoted only when needed.</summary>
    public static string FormatRow(IEnumerable<string> fields, char delimiter = ',')
    {
        var line = new StringBuilder();
        var first = true;
        foreach (var field in fields)
        {
            if (!first)
                line.Append(delimiter);
            first = false;
            if (field.IndexOfAny([delimiter, '"', '\n', '\r']) >= 0 || field.StartsWith(' ') || field.EndsWith(' '))
                line.Append('"').Append(field.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
            else
                line.Append(field);
        }
        return line.ToString();
    }
}
