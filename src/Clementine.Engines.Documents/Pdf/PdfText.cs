using System.Text;
using Clementine.Core;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace Clementine.Engines.Documents.Pdf;

/// <summary>A paragraph-like run of text on a page with its typical font size, weight and family (for structure detection).</summary>
internal sealed record PdfTextBlock(string Text, double FontSize, bool IsBold, bool IsMonospaced = false);

/// <summary>Text extraction with PdfPig.</summary>
internal static class PdfText
{
    /// <summary>Opens with PdfPig (lenient parsing), mapping failures to friendly messages.</summary>
    public static PigDocument Open(string path, string? password = null)
    {
        var bytes = PdfFiles.ReadBytes(path);
        try
        {
            var options = new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true };
            if (!string.IsNullOrEmpty(password))
                options.Password = password;
            return PigDocument.Open(bytes, options);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("encrypt", StringComparison.OrdinalIgnoreCase))
                throw new OperationException("This PDF is password-protected. Use Unlock to remove the password first.", ex.Message, ex);
            throw new OperationException($"Couldn't read \"{Path.GetFileName(path)}\" — it may be damaged or not a real PDF.", ex.ToString(), ex);
        }
    }

    /// <summary>Plain text of every page in reading order; pages separated by a blank line.</summary>
    public static string ExtractText(string path, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        using var document = Open(path);
        var text = new StringBuilder();
        var count = document.NumberOfPages;
        for (var number = 1; number <= count; number++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pageText = PageText(document.GetPage(number));
            if (text.Length > 0)
                text.Append("\n\n");
            text.Append(pageText.TrimEnd());
            progress.Report(new JobProgress((double)number / count, $"Reading page {number} of {count}…"));
        }
        if (string.IsNullOrWhiteSpace(text.ToString()))
            throw new OperationException("This PDF has no selectable text — it's probably scanned images.");
        return text.ToString();
    }

    /// <summary>Text blocks of every page, in reading order, for structure-aware exports (DOCX).</summary>
    public static IReadOnlyList<IReadOnlyList<PdfTextBlock>> ExtractBlocks(string path, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        using var document = Open(path);
        var pages = new List<IReadOnlyList<PdfTextBlock>>();
        var count = document.NumberOfPages;
        for (var number = 1; number <= count; number++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pages.Add(PageBlocks(document.GetPage(number)));
            progress.Report(new JobProgress((double)number / count * 0.8, $"Reading page {number} of {count}…"));
        }
        if (pages.All(p => p.Count == 0))
            throw new OperationException("This PDF has no selectable text — it's probably scanned images.");
        return pages;
    }

    /// <summary>Whether the first few pages carry any text (used by Info).</summary>
    public static bool HasText(PigDocument document)
    {
        for (var number = 1; number <= Math.Min(document.NumberOfPages, 5); number++)
        {
            if (document.GetPage(number).Letters.Any(l => !string.IsNullOrWhiteSpace(l.Value)))
                return true;
        }
        return false;
    }

    static string PageText(Page page)
    {
        try
        {
            return TextFiles.NormalizeNewlines(ContentOrderTextExtractor.GetText(page));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Layout analysis can trip over odd pages; the raw text is better than nothing.
            return page.Text;
        }
    }

    static IReadOnlyList<PdfTextBlock> PageBlocks(Page page)
    {
        var words = NearestNeighbourWordExtractor.Instance.GetWords(page.Letters).ToList();
        if (words.Count == 0)
            return [];
        var blocks = DocstrumBoundingBoxes.Instance.GetBlocks(words);
        var ordered = UnsupervisedReadingOrderDetector.Instance.Get(blocks);
        var result = new List<PdfTextBlock>();
        foreach (var block in ordered)
        {
            var lines = block.TextLines.Where(l => l.Text.Trim().Length > 0).ToList();
            if (lines.Count == 0)
                continue;
            var widest = lines.Max(l => l.BoundingBox.Width);

            // A layout block can hold a heading plus the paragraph below it, or a whole list: start a new paragraph
            // where the size, weight or font family changes, at list markers, and after a line that ends early.
            var group = new List<string>();
            LineStyle? style = null;
            var previousEndedEarly = false;
            foreach (var line in lines)
            {
                var text = line.Text.Trim();
                var lineStyle = StyleOf(line);
                if (style is { } previous && (!previous.SameAs(lineStyle) || StartsListItem(text) || (previousEndedEarly && !previous.Mono)))
                {
                    result.Add(previous.ToBlock(group));
                    group.Clear();
                }
                style = lineStyle;
                group.Add(text);
                previousEndedEarly = line.BoundingBox.Width < widest * 0.7;
            }
            if (style is { } last)
                result.Add(last.ToBlock(group));
        }
        return result;
    }

    /// <summary>Typical size, weight and family of a line's letters.</summary>
    readonly record struct LineStyle(double Size, bool Bold, bool Mono)
    {
        public bool SameAs(LineStyle other) => Math.Abs(Size - other.Size) <= 0.6 && Bold == other.Bold && Mono == other.Mono;

        public PdfTextBlock ToBlock(IReadOnlyList<string> lines) =>
            new(Mono ? string.Join('\n', lines) : JoinLines(lines), Size, Bold, Mono);
    }

    static LineStyle StyleOf(TextLine line)
    {
        var letters = line.Words.SelectMany(w => w.Letters).Where(l => !string.IsNullOrWhiteSpace(l.Value)).ToList();
        if (letters.Count == 0)
            return new LineStyle(0, false, false);
        return new LineStyle(
            Math.Round(Median(letters.Select(l => l.PointSize)), 1),
            letters.Count(l => IsBoldFont(l.FontName)) > letters.Count * 0.6,
            letters.Count(l => IsMonospacedFont(l.FontName)) > letters.Count * 0.6);
    }

    /// <summary>"1.", "a)", "•", "–", "◦" at the start of a line begin a new list item.</summary>
    static bool StartsListItem(string text)
    {
        if (text.Length < 2)
            return false;
        if (text[0] is '•' or '◦' or '▪' or '‣' or '–' or '-' or '*' && text[1] == ' ')
            return true;
        var marker = 0;
        while (marker < text.Length && marker < 3 && char.IsAsciiLetterOrDigit(text[marker]))
            marker++;
        return marker > 0 && marker + 1 < text.Length && text[marker] is '.' or ')' && text[marker + 1] == ' '
            && (char.IsAsciiDigit(text[0]) || marker == 1);
    }

    /// <summary>Joins wrapped lines into one paragraph, undoing end-of-line hyphenation.</summary>
    static string JoinLines(IReadOnlyList<string> lines)
    {
        var text = new StringBuilder(lines[0]);
        for (var i = 1; i < lines.Count; i++)
        {
            if (text.Length > 1 && text[^1] == '-' && char.IsLetter(text[^2]) && lines[i].Length > 0 && char.IsLower(lines[i][0]))
                text.Length--;
            else
                text.Append(' ');
            text.Append(lines[i]);
        }
        return text.ToString();
    }

    static bool IsBoldFont(string? name) =>
        name is not null && (name.Contains("Bold", StringComparison.OrdinalIgnoreCase) || name.Contains("Black", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Heavy", StringComparison.OrdinalIgnoreCase) || name.Contains("Semibold", StringComparison.OrdinalIgnoreCase));

    static bool IsMonospacedFont(string? name) =>
        name is not null && (name.Contains("Mono", StringComparison.OrdinalIgnoreCase) || name.Contains("Consol", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Courier", StringComparison.OrdinalIgnoreCase) || name.Contains("Menlo", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Code", StringComparison.OrdinalIgnoreCase));

    static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
    }
}
