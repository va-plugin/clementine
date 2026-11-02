using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Clementine.Core;

namespace Clementine.Engines.Documents.Common;

/// <summary>One contiguous run of pages, 1-based and inclusive. <see cref="First"/> may exceed <see cref="Last"/> (descending).</summary>
internal readonly record struct PageRange(int First, int Last)
{
    public IEnumerable<int> Pages()
    {
        var step = First <= Last ? 1 : -1;
        for (var page = First; ; page += step)
        {
            yield return page;
            if (page == Last)
                yield break;
        }
    }

    /// <summary>"5" or "1-3".</summary>
    public override string ToString() => First == Last ? First.ToString(CultureInfo.InvariantCulture) : $"{First}-{Last}";
}

/// <summary>
/// Parses page selections such as "1-3, 5, 8-", "-4", "last", "10-7" against a document's page count, with friendly
/// errors for the options dialog.
/// </summary>
internal static partial class PageRanges
{
    const string Example = "Enter the pages to use, for example \"1-3, 5, 8-\".";

    public static IReadOnlyList<PageRange> Parse(string? text, int pageCount)
    {
        if (pageCount < 1)
            throw new OperationException("This PDF has no pages.");
        if (string.IsNullOrWhiteSpace(text))
            throw new OperationException(Example);

        var ranges = new List<PageRange>();
        foreach (var part in Tokens(text))
        {
            if (part.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                ranges.Add(new PageRange(1, pageCount));
                continue;
            }

            var dash = part.IndexOf('-');
            if (dash < 0)
            {
                var page = ParsePage(part, pageCount, part);
                ranges.Add(new PageRange(page, page));
                continue;
            }
            var left = part[..dash];
            var right = part[(dash + 1)..];
            var first = left.Length == 0 ? 1 : ParsePage(left, pageCount, part);
            var last = right.Length == 0 ? pageCount : ParsePage(right, pageCount, part);
            ranges.Add(new PageRange(first, last));
        }

        if (ranges.Count == 0)
            throw new OperationException(Example);
        return ranges;
    }

    /// <summary>
    /// Splits "1-3, 5 8 -" into "1-3", "5", "8-": commas, semicolons and spaces separate parts; spaces around a dash
    /// don't. Accepts the dashes, commas and digits people paste from other apps or type on CJK keyboards.
    /// </summary>
    static IEnumerable<string> Tokens(string text)
    {
        var normal = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormKC))
        {
            normal.Append(c switch
            {
                '‐' or '‑' or '‒' or '–' or '—' or '―' or '−' or '﹣' or '－' or '~' => '-',
                ';' or '、' or '，' or '；' => ',',
                _ when char.IsWhiteSpace(c) => ' ',
                _ when char.IsDigit(c) => (char)('0' + CharUnicodeInfo.GetDecimalDigitValue(c)),
                _ => c,
            });
        }
        var joined = SpacesAroundDash().Replace(normal.ToString(), "-");
        return joined.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    [GeneratedRegex(@" *- *")]
    private static partial Regex SpacesAroundDash();

    /// <summary>Every page of the selection in order (duplicates kept).</summary>
    public static IReadOnlyList<int> Expand(IEnumerable<PageRange> ranges) => ranges.SelectMany(r => r.Pages()).ToList();

    /// <summary>"1-3, 5" for file names and messages.</summary>
    public static string Describe(IEnumerable<PageRange> ranges) => string.Join(", ", ranges.Select(r => r.ToString()));

    static int ParsePage(string token, int pageCount, string original)
    {
        if (token.Equals("last", StringComparison.OrdinalIgnoreCase) || token.Equals("end", StringComparison.OrdinalIgnoreCase))
            return pageCount;
        if (token.Length > 0 && token.All(char.IsAsciiDigit) && !int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            throw new OperationException(pageCount == 1 ? $"Page {token} doesn't exist — this PDF has only 1 page." : $"Page {token} doesn't exist — this PDF has {pageCount} pages.");
        if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var page))
            throw new OperationException($"\"{original}\" isn't a page number or range. Use something like \"1-3, 5, 8-\".");
        if (page < 1)
            throw new OperationException("Pages are numbered from 1, so there's no page 0.");
        if (page > pageCount)
        {
            throw new OperationException(pageCount == 1
                ? $"Page {page} doesn't exist — this PDF has only 1 page."
                : $"Page {page} doesn't exist — this PDF has {pageCount} pages.");
        }
        return page;
    }
}
