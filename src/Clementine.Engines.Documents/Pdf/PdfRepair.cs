using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Clementine.Engines.Documents.Pdf;

/// <summary>
/// Rebuilds the cross-reference table of a PDF whose xref offsets are wrong (a careless editor, a bad FTP transfer, an
/// extra line inserted at the top). PDFium and PdfPig cope with that on their own; PDFsharp doesn't, so before giving up
/// on such a file the page tools scan it for "12 0 obj" markers and append a fresh xref table and trailer.
/// </summary>
internal static partial class PdfRepair
{
    /// <summary>Returns a repaired copy, or null when the file doesn't look repairable this way.</summary>
    public static byte[]? RebuildXref(byte[] pdf)
    {
        if (pdf.Length < 32)
            return null;
        // Latin-1 maps every byte to one char, so string offsets are byte offsets.
        var text = Encoding.Latin1.GetString(pdf);
        if (!text.StartsWith("%PDF-", StringComparison.Ordinal) && text.IndexOf("%PDF-", 0, Math.Min(text.Length, 1024), StringComparison.Ordinal) < 0)
            return null;

        // The last definition of each object wins (incremental updates append newer versions).
        var offsets = new SortedDictionary<int, (long Offset, int Generation)>();
        foreach (Match match in ObjectHeader().Matches(text))
        {
            if (!int.TryParse(match.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                || !int.TryParse(match.Groups["g"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var generation)
                || number <= 0 || number > 8_000_000 || generation > 65535)
                continue;
            offsets[number] = (match.Groups["n"].Index, generation);
        }
        if (offsets.Count == 0)
            return null;

        var trailer = LastTrailer(text);
        var root = Reference(trailer, "Root") ?? CatalogReference(text);
        if (root is null)
            return null;

        var size = offsets.Keys.Max() + 1;
        var xref = new StringBuilder(size * 20 + 256);
        xref.Append("\nxref\n0 ").Append(size.ToString(CultureInfo.InvariantCulture)).Append('\n');
        xref.Append("0000000000 65535 f \n");
        for (var number = 1; number < size; number++)
        {
            if (offsets.TryGetValue(number, out var entry))
                xref.Append(entry.Offset.ToString("D10", CultureInfo.InvariantCulture)).Append(' ')
                    .Append(entry.Generation.ToString("D5", CultureInfo.InvariantCulture)).Append(" n \n");
            else
                xref.Append("0000000000 00000 f \n");
        }
        xref.Append("trailer\n<< /Size ").Append(size.ToString(CultureInfo.InvariantCulture)).Append(" /Root ").Append(root);
        if (Reference(trailer, "Info") is { } info)
            xref.Append(" /Info ").Append(info);
        if (Reference(trailer, "Encrypt") is { } encrypt)
            xref.Append(" /Encrypt ").Append(encrypt);
        if (trailer is not null && IdArray().Match(trailer) is { Success: true } id)
            xref.Append(" /ID ").Append(id.Groups["id"].Value);
        var startXref = pdf.LongLength + 1; // the xref keyword follows the leading newline
        xref.Append(" >>\nstartxref\n").Append(startXref.ToString(CultureInfo.InvariantCulture)).Append("\n%%EOF\n");

        var tail = Encoding.Latin1.GetBytes(xref.ToString());
        var repaired = new byte[pdf.Length + tail.Length];
        pdf.CopyTo(repaired, 0);
        tail.CopyTo(repaired, pdf.Length);
        return repaired;
    }

    /// <summary>The dictionary text after the last "trailer" keyword (or of the last cross-reference stream).</summary>
    static string? LastTrailer(string text)
    {
        var index = text.LastIndexOf("trailer", StringComparison.Ordinal);
        if (index >= 0)
            return text.Substring(index, Math.Min(4096, text.Length - index));
        var stream = text.LastIndexOf("/XRef", StringComparison.Ordinal);
        if (stream < 0)
            return null;
        var start = text.LastIndexOf("<<", stream, StringComparison.Ordinal);
        return start < 0 ? null : text.Substring(start, Math.Min(4096, text.Length - start));
    }

    static string? Reference(string? dictionary, string key) =>
        dictionary is not null && Regex.Match(dictionary, $@"/{key}\s+(\d+\s+\d+\s+R)") is { Success: true } match ? match.Groups[1].Value : null;

    static string? CatalogReference(string text) =>
        CatalogObject().Matches(text).LastOrDefault() is { } match ? $"{match.Groups["n"].Value} {match.Groups["g"].Value} R" : null;

    [GeneratedRegex(@"(?<![0-9])(?<n>\d{1,7})[ \t\r\n\f\0]+(?<g>\d{1,5})[ \t\r\n\f\0]+obj\b")]
    private static partial Regex ObjectHeader();

    [GeneratedRegex(@"(?<![0-9])(?<n>\d{1,7})\s+(?<g>\d{1,5})\s+obj\s*<<(?:(?!endobj).){0,2000}?/Type\s*/Catalog\b", RegexOptions.Singleline)]
    private static partial Regex CatalogObject();

    [GeneratedRegex(@"/ID\s*(?<id>\[[^\]]*\])")]
    private static partial Regex IdArray();
}
