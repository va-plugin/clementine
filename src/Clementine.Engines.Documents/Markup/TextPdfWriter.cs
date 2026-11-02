using System.Globalization;
using System.Text;
using Clementine.Engines.Documents.Typography;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace Clementine.Engines.Documents.Markup;

/// <summary>
/// Typesets plain text onto A4 (or Letter, in non-metric regions) pages with PDFsharp: word wrapping (CJK may break between any two characters),
/// per-character font fallback so every script renders, tab stops, form feeds as page breaks and page numbers.
/// </summary>
internal sealed class TextPdfWriter
{
    static readonly bool Metric = System.Globalization.RegionInfo.CurrentRegion.IsMetric;
    static readonly double PageWidth = Metric ? 595.28 : 612;
    static readonly double PageHeight = Metric ? 841.89 : 792;
    const double MarginX = 56;
    const double MarginTop = 56;
    const double MarginBottom = 62;

    readonly FontFace _primary;
    readonly double _fontSize;
    readonly double _lineHeight;
    readonly bool _monospace;
    readonly Dictionary<string, XFont> _fonts = [];
    readonly Dictionary<(string Face, string Text), double> _widths = [];

    public TextPdfWriter(bool monospace)
    {
        _monospace = monospace;
        _primary = monospace ? FontLibrary.Instance.Face("Consolas") : FontLibrary.Instance.Face("Segoe UI");
        _fontSize = monospace ? 9.5 : 10.5;
        _lineHeight = _fontSize * 1.42;
    }

    /// <summary>Heuristic: logs, code and text laid out with runs of spaces read best in a monospaced font.</summary>
    public static bool PrefersMonospace(string text, string extension)
    {
        if (extension.Equals(".log", StringComparison.OrdinalIgnoreCase))
            return true;
        var lines = text.Split('\n').Take(400).Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count == 0)
            return false;
        // Leading indentation reads fine in any font; runs of spaces or tabs *inside* a line mean columns.
        var aligned = lines.Count(l =>
        {
            var body = l.Trim();
            return body.Contains("   ", StringComparison.Ordinal) || body.Contains('\t') || body.Any(c => c is >= '─' and <= '╿');
        });
        return aligned >= Math.Max(2, lines.Count * 0.15);
    }

    /// <summary>
    /// Right-to-left and complex scripts (Arabic, Hebrew, Indic, Thai…) need bidi reordering and glyph shaping, which
    /// PDFsharp doesn't do; such text is printed through a browser engine instead.
    /// </summary>
    public static bool NeedsComplexShaping(string text)
    {
        foreach (var c in text)
        {
            if (c is >= '֐' and <= 'ࣿ' or >= 'ऀ' and <= '෿' or >= '฀' and <= '࿿'
                or >= 'က' and <= '႟' or >= 'ក' and <= '៿' or >= 'יִ' and <= '﷿' or >= 'ﹰ' and <= 'ﻼ')
                return true;
        }
        return false;
    }

    public void Write(string text, string outputPath, string title, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        FontLibrary.EnsureInstalled();
        using var document = new PdfDocument();
        document.Info.Title = title;
        document.Info.Creator = "Clementine";

        var lines = TextFiles.NormalizeNewlines(text).Split('\n');
        var maxWidth = PageWidth - 2 * MarginX;
        var bottom = PageHeight - MarginBottom;
        PdfPage? page = null;
        XGraphics? gfx = null;
        var y = 0.0;

        void NewPage()
        {
            gfx?.Dispose();
            page = document.AddPage();
            page.Width = XUnit.FromPoint(PageWidth);
            page.Height = XUnit.FromPoint(PageHeight);
            gfx = XGraphics.FromPdfPage(page);
            y = MarginTop;
        }

        try
        {
            NewPage();
            for (var index = 0; index < lines.Length; index++)
            {
                if (index % 200 == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress.Report(new JobProgress(0.05 + 0.85 * index / lines.Length, "Typesetting…"));
                }

                var segments = lines[index].Split('\f');
                for (var s = 0; s < segments.Length; s++)
                {
                    if (s > 0)
                        NewPage();
                    foreach (var visual in Wrap(ExpandTabs(segments[s]), gfx!, maxWidth))
                    {
                        if (y + _lineHeight > bottom)
                            NewPage();
                        Draw(gfx!, visual, MarginX, y + _fontSize);
                        y += _lineHeight;
                    }
                }
            }
        }
        finally
        {
            gfx?.Dispose();
        }

        AddPageNumbers(document);
        progress.Report(new JobProgress(0.95, "Saving PDF…"));
        document.Save(outputPath);
    }

    void AddPageNumbers(PdfDocument document)
    {
        if (document.PageCount < 2)
            return;
        var font = FontFor(_primary.Key, 8);
        var brush = new XSolidBrush(XColor.FromArgb(255, 130, 130, 130));
        for (var i = 0; i < document.PageCount; i++)
        {
            using var gfx = XGraphics.FromPdfPage(document.Pages[i], XGraphicsPdfPageOptions.Append);
            var label = string.Create(CultureInfo.InvariantCulture, $"{i + 1} / {document.PageCount}");
            var width = gfx.MeasureString(label, font).Width;
            gfx.DrawString(label, font, brush, (PageWidth - width) / 2, PageHeight - MarginBottom / 2);
        }
    }

    string ExpandTabs(string line)
    {
        if (!line.Contains('\t'))
            return line;
        var builder = new StringBuilder(line.Length + 16);
        foreach (var c in line)
        {
            if (c == '\t')
            {
                var spaces = _monospace ? 4 - builder.Length % 4 : 4;
                builder.Append(' ', spaces);
            }
            else
            {
                builder.Append(c);
            }
        }
        return builder.ToString();
    }

    /// <summary>A piece of a line drawn with one face.</summary>
    readonly record struct Run(string Text, FontFace Face);

    /// <summary>Unbreakable unit: a word, a run of spaces, or a single CJK character, possibly in several faces.</summary>
    sealed class Token(bool isSpace)
    {
        public bool IsSpace { get; } = isSpace;

        public List<Run> Runs { get; } = [];

        public double Width { get; set; }
    }

    IEnumerable<List<Run>> Wrap(string line, XGraphics gfx, double maxWidth)
    {
        var tokens = Tokenize(line);
        if (tokens.Count == 0)
        {
            yield return [];
            yield break;
        }

        foreach (var token in tokens)
            token.Width = token.Runs.Sum(r => Measure(gfx, r));

        var current = new List<Run>();
        var x = 0.0;
        var firstVisualLine = true;
        foreach (var token in tokens)
        {
            if (token.IsSpace && current.Count == 0 && !firstVisualLine)
                continue; // wrapped lines don't start with the space that caused the wrap
            if (x + token.Width > maxWidth && current.Count > 0)
            {
                yield return TrimTrailingSpace(current);
                current = [];
                x = 0;
                firstVisualLine = false;
                if (token.IsSpace)
                    continue;
            }
            if (token.Width > maxWidth)
            {
                // A single word wider than the page: break it between characters.
                foreach (var piece in SplitLongToken(token, gfx, maxWidth))
                {
                    if (current.Count > 0)
                    {
                        yield return current;
                        firstVisualLine = false;
                    }
                    current = piece.Runs;
                    x = piece.Width;
                }
                continue;
            }
            current.AddRange(token.Runs);
            x += token.Width;
        }
        yield return TrimTrailingSpace(current);
    }

    IEnumerable<(List<Run> Runs, double Width)> SplitLongToken(Token token, XGraphics gfx, double maxWidth)
    {
        var runs = new List<Run>();
        var width = 0.0;
        foreach (var run in token.Runs)
        {
            foreach (var element in TextElements(run.Text))
            {
                var piece = new Run(element, run.Face);
                var w = Measure(gfx, piece);
                if (width + w > maxWidth && runs.Count > 0)
                {
                    yield return (Merge(runs), width);
                    runs = [];
                    width = 0;
                }
                runs.Add(piece);
                width += w;
            }
        }
        if (runs.Count > 0)
            yield return (Merge(runs), width);
    }

    List<Token> Tokenize(string line)
    {
        var tokens = new List<Token>();
        Token? token = null;
        var builder = new StringBuilder();
        FontFace? face = null;

        void FlushRun()
        {
            if (builder.Length > 0 && token is not null && face is not null)
                token.Runs.Add(new Run(builder.ToString(), face));
            builder.Clear();
        }

        void StartToken(bool isSpace)
        {
            FlushRun();
            token = new Token(isSpace);
            tokens.Add(token);
        }

        var previousBreaksAnywhere = false;
        foreach (var rune in line.EnumerateRunes())
        {
            var value = rune.Value;
            if (value < 0x20)
                continue; // control characters have no glyphs (tabs were expanded already)
            var isSpace = Rune.IsWhiteSpace(rune);
            var breaksAnywhere = IsBreakAnywhere(value);
            if (token is null || token.IsSpace != isSpace || breaksAnywhere || previousBreaksAnywhere)
                StartToken(isSpace);
            previousBreaksAnywhere = breaksAnywhere;

            var runeFace = isSpace ? _primary : FontLibrary.Instance.FaceFor(value, _primary);
            if (face is not null && face.Key != runeFace.Key)
                FlushRun();
            face = runeFace;
            builder.Append(rune.ToString());
        }
        FlushRun();
        return tokens;
    }

    /// <summary>CJK ideographs, kana, Hangul and fullwidth forms can wrap between any two characters.</summary>
    static bool IsBreakAnywhere(int c) =>
        c is >= 0x2E80 and <= 0x9FFF or >= 0xAC00 and <= 0xD7AF or >= 0xF900 and <= 0xFAFF or >= 0xFF00 and <= 0xFFEF or >= 0x20000 and <= 0x3FFFF;

    void Draw(XGraphics gfx, List<Run> runs, double x, double baseline)
    {
        foreach (var run in runs)
        {
            var font = FontFor(run.Face.Key, _fontSize);
            gfx.DrawString(run.Text, font, XBrushes.Black, x, baseline);
            x += Measure(gfx, run);
        }
    }

    double Measure(XGraphics gfx, Run run)
    {
        var key = (run.Face.Key, run.Text);
        if (_widths.TryGetValue(key, out var width))
            return width;
        width = gfx.MeasureString(run.Text, FontFor(run.Face.Key, _fontSize)).Width;
        if (_widths.Count < 50_000)
            _widths[key] = width;
        return width;
    }

    XFont FontFor(string faceKey, double size)
    {
        var key = string.Create(CultureInfo.InvariantCulture, $"{faceKey}@{size}");
        if (!_fonts.TryGetValue(key, out var font))
        {
            font = new XFont(faceKey, size, XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.Unicode));
            _fonts[key] = font;
        }
        return font;
    }

    static List<Run> TrimTrailingSpace(List<Run> runs)
    {
        while (runs.Count > 0 && string.IsNullOrWhiteSpace(runs[^1].Text))
            runs.RemoveAt(runs.Count - 1);
        return runs;
    }

    static List<Run> Merge(List<Run> runs)
    {
        var merged = new List<Run>();
        foreach (var run in runs)
        {
            if (merged.Count > 0 && merged[^1].Face.Key == run.Face.Key)
                merged[^1] = merged[^1] with { Text = merged[^1].Text + run.Text };
            else
                merged.Add(run);
        }
        return merged;
    }

    static IEnumerable<string> TextElements(string text)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
            yield return enumerator.GetTextElement();
    }
}
