using System.Windows;

namespace Clementine.Wheel;

/// <summary>Word wrapping and ellipsis with a pluggable measure, so text fits circles and segments exactly.</summary>
internal static class TextWrap
{
    public const string Ellipsis = "…";

    /// <summary>
    /// Greedy word wrap where line <c>i</c> may be at most <paramref name="widthOfLine"/>(i) wide. Words wider than a
    /// line are broken by character; if text remains after <paramref name="maxLines"/>, the last line ends in "…".
    /// </summary>
    public static IReadOnlyList<string> Wrap(string text, Func<string, double> measure, Func<int, double> widthOfLine, int maxLines)
    {
        var words = new Queue<string>(text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var lines = new List<string>();
        while (words.Count > 0 && lines.Count < maxLines)
        {
            var width = Math.Max(1, widthOfLine(lines.Count));
            var lastAllowed = lines.Count == maxLines - 1;
            var line = words.Dequeue();

            if (measure(line) > width && !lastAllowed)
            {
                var (head, tail) = BreakWord(line, measure, width);
                lines.Add(head);
                if (tail.Length > 0)
                    words = new Queue<string>(words.Prepend(tail));
                continue;
            }

            while (words.Count > 0 && measure(line + " " + words.Peek()) <= width)
                line += " " + words.Dequeue();

            if (lastAllowed && (words.Count > 0 || measure(line) > width))
            {
                var rest = words.Count > 0 ? line + " " + string.Join(' ', words) : line;
                line = Ellipsize(rest, measure, width);
                words.Clear();
            }
            lines.Add(line);
        }
        return lines;
    }

    /// <summary>
    /// Like <see cref="Wrap"/>, but text made of " · "-separated phrases ("ProRes 4444 · alpha · with audio") breaks
    /// between phrases when that fits, and a separator is never left dangling at a line end or start.
    /// </summary>
    public static IReadOnlyList<string> WrapPhrases(string text, Func<string, double> measure, Func<int, double> widthOfLine, int maxLines)
    {
        var phrases = text.Split(" · ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (phrases.Length > 1)
        {
            var lines = new List<string>();
            var fits = true;
            for (var i = 0; i < phrases.Length && fits;)
            {
                var width = widthOfLine(lines.Count);
                var line = phrases[i++];
                if (measure(line) > width)
                {
                    fits = false;
                    break;
                }
                while (i < phrases.Length && measure(line + " · " + phrases[i]) <= width)
                    line += " · " + phrases[i++];
                lines.Add(line);
            }
            if (fits && lines.Count <= maxLines)
                return lines;
        }

        return Wrap(text, measure, widthOfLine, maxLines)
            .Select(line => line.EndsWith(" ·", StringComparison.Ordinal) ? line[..^2] : line)
            .Select(line => line.StartsWith("· ", StringComparison.Ordinal) ? line[2..] : line)
            .ToList();
    }

    /// <summary>
    /// <see cref="WrapPhrases"/> with balanced lines: the widths are narrowed as far as possible without adding a
    /// line, so a paragraph never ends with a lonely word. Lines that break between " · " phrases stay that way:
    /// "ProRes 422 HQ" / "no audio", never the more even "ProRes 422" / "HQ · no audio".
    /// </summary>
    public static IReadOnlyList<string> WrapBalanced(string text, Func<string, double> measure, Func<int, double> widthOfLine, int maxLines)
    {
        var lines = WrapPhrases(text, measure, widthOfLine, maxLines);
        if (lines.Count < 2 || lines[^1].EndsWith(Ellipsis, StringComparison.Ordinal))
            return lines;

        var keepPhrases = BreaksBetweenPhrases(text, lines);
        double low = 0.5, high = 1;
        var best = lines;
        for (var i = 0; i < 10; i++)
        {
            var factor = (low + high) / 2;
            var candidate = WrapPhrases(text, measure, line => widthOfLine(line) * factor, maxLines);
            if (candidate.Count == lines.Count && !candidate[^1].EndsWith(Ellipsis, StringComparison.Ordinal)
                && (!keepPhrases || BreaksBetweenPhrases(text, candidate)))
            {
                best = candidate;
                high = factor;
            }
            else
            {
                low = factor;
            }
        }
        return best;
    }

    /// <summary>Whether <paramref name="lines"/> hold whole " · " phrases of <paramref name="text"/> (which has several).</summary>
    static bool BreaksBetweenPhrases(string text, IReadOnlyList<string> lines)
    {
        var phrases = text.Split(" · ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return phrases.Length > 1 && string.Join(" · ", lines) == string.Join(" · ", phrases);
    }

    /// <summary>
    /// The longest prefix of <paramref name="text"/> that fits <paramref name="width"/> with a trailing "…". Spaces and
    /// punctuation before the "…" are dropped ("Festival loop…", never "Festival loop.…").
    /// </summary>
    public static string Ellipsize(string text, Func<string, double> measure, double width)
    {
        if (measure(text) <= width)
            return text;
        int low = 0, high = text.Length - 1;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (measure(Cut(text, mid) + Ellipsis) <= width)
                low = mid;
            else
                high = mid - 1;
        }
        var head = Cut(text, low);
        return head.Length == 0 ? Ellipsis : head + Ellipsis;
    }

    static readonly char[] TrailingJunk = [' ', '.', ',', ';', ':', '-', '_', '·', '(', '[', '/', '—', '–'];

    static string Cut(string text, int length) => text[..length].TrimEnd(TrailingJunk);

    /// <summary>Whether a wrapped paragraph lost text (ends in "…" the source didn't have) or split a word.</summary>
    public static bool IsDegraded(string text, IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
            return text.Length > 0;
        if (lines[^1].EndsWith(Ellipsis, StringComparison.Ordinal) && !text.EndsWith(Ellipsis, StringComparison.Ordinal))
            return true;
        // A word broken across lines leaves a line that ends mid-word: the joined lines then differ from the
        // source's words.
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var rejoined = string.Join(' ', lines).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w != "·").ToArray();
        return !words.Where(w => w != "·").SequenceEqual(rejoined);
    }

    /// <summary>
    /// Ways to show a short label: on one line, then split in two — at " · " first (variant names such as
    /// "HAP Alpha · no audio"), then at the most balanced space, then (only if <paramref name="allowHyphen"/>)
    /// after a hyphen.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> LabelLines(string label, bool allowHyphen = true)
    {
        var options = new List<IReadOnlyList<string>> { new[] { label } };
        var dot = label.IndexOf(" · ", StringComparison.Ordinal);
        if (dot > 0 && dot + 3 < label.Length)
            options.Add(new[] { label[..dot], label[(dot + 3)..] });

        var splits = new List<(int Index, int Cut, int Resume, int Depth)>();
        var depth = 0;
        for (var i = 1; i < label.Length - 1; i++)
        {
            depth += label[i - 1] == '(' ? 1 : label[i - 1] == ')' ? -1 : 0;
            if (label[i] == ' ' && label[i - 1] != '·' && label[i + 1] != '·')
                splits.Add((i, i, i + 1, depth));
            else if (allowHyphen && label[i] == '-' && label[i - 1] != ' ' && label[i + 1] != ' ')
                splits.Add((i, i + 1, i + 1, depth));
        }
        // Never inside brackets when avoidable ("Remux" / "(no re-encode)", not "Remux (no" / "re-encode)"), spaces
        // before hyphens, then the most balanced split.
        var ordered = splits
            .OrderBy(s => s.Depth > 0 ? 1 : 0)
            .ThenBy(s => label[s.Index] == '-' ? 1 : 0)
            .ThenBy(s => Math.Abs(s.Cut - (label.Length - s.Resume)));
        foreach (var (_, cut, resume, _) in ordered)
        {
            var first = label[..cut].TrimEnd();
            var second = label[resume..].TrimStart();
            if (first.Length > 0 && second.Length > 0 && !options.Any(o => o.Count == 2 && o[0] == first))
                options.Add(new[] { first, second });
        }
        return options;
    }

    static (string Head, string Tail) BreakWord(string word, Func<string, double> measure, double width)
    {
        int low = 1, high = word.Length;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (measure(word[..mid]) <= width)
                low = mid;
            else
                high = mid - 1;
        }
        return (word[..low], word[low..]);
    }
}

/// <summary>The drawable area of one segment, for checking whether a label box fits inside it.</summary>
internal readonly record struct SectorFrame(AngleRange Range, double InnerRadius, double OuterRadius, double Gap)
{
    public double MidRadius => (InnerRadius + OuterRadius) / 2;

    /// <summary>
    /// Whether an axis-aligned box of <paramref name="width"/> × <paramref name="height"/> centred at
    /// <paramref name="centre"/> (relative to the wheel centre) stays inside the segment with <paramref name="margin"/>
    /// to every edge. Wedges up to 180° are convex, so corners decide the straight edges and the outer arc; the inner
    /// arc bulges into boxes, so the point of each box edge closest to the centre is checked against it.
    /// </summary>
    public bool Fits(Vector centre, double width, double height, double margin)
    {
        var hw = width / 2;
        var hh = height / 2;
        Vector[] corners =
        [
            new(centre.X - hw, centre.Y - hh), new(centre.X + hw, centre.Y - hh),
            new(centre.X + hw, centre.Y + hh), new(centre.X - hw, centre.Y + hh),
        ];

        var outerLimit = OuterRadius - margin;
        if (corners.Any(c => c.Length > outerLimit))
            return false;

        var innerLimit = InnerRadius + margin;
        for (var i = 0; i < 4; i++)
        {
            if (ClosestToOrigin(corners[i], corners[(i + 1) % 4]).Length < innerLimit)
                return false;
        }

        if (Range.IsFullCircle)
            return true;
        var startNormal = Angles.Direction(Range.Start + 90);
        var endNormal = -Angles.Direction(Range.End + 90);
        var limit = Gap / 2 + margin;
        return corners.All(c => c * startNormal >= limit && c * endNormal >= limit);
    }

    static Vector ClosestToOrigin(Vector a, Vector b)
    {
        var ab = b - a;
        var t = Math.Clamp(-(a * ab) / (ab * ab), 0, 1);
        return a + ab * t;
    }
}

/// <summary>A label placed in a segment: its lines, size, whether the glyph shows, and where its box is centred.</summary>
internal sealed record LabelFit(IReadOnlyList<string> Lines, double FontSize, double LineHeight, bool ShowGlyph, Vector Centre, bool Truncated);

/// <summary>Chooses font size, line breaks and position so segment labels fit without clipping.</summary>
internal static class LabelFitter
{
    public const double GlyphGap = 3;
    static readonly double[] RadialShifts = [0, 0.07, -0.07, 0.14, 0.21, 0.27];

    public static double LineHeightFor(double fontSize) => Math.Ceiling(fontSize * 1.32);

    /// <summary>
    /// Tries to place <paramref name="label"/> at exactly <paramref name="fontSize"/>: one line, then the two-line
    /// splits of <see cref="TextWrap.LabelLines"/>, each at a few radial positions. Null if nothing fits.
    /// </summary>
    public static LabelFit? TryFit(
        string label, double fontSize, double glyphSize, bool showGlyph, SectorFrame frame,
        Func<string, double, double> measure, double margin, bool allowHyphen = true)
    {
        var lineHeight = LineHeightFor(fontSize);
        var ring = frame.OuterRadius - frame.InnerRadius;
        foreach (var lines in TextWrap.LabelLines(label, allowHyphen))
        {
            var width = lines.Max(l => measure(l, fontSize));
            var height = lines.Count * lineHeight + (showGlyph ? glyphSize + GlyphGap : 0);
            foreach (var shift in RadialShifts)
            {
                var centre = Angles.Direction(frame.Range.Mid) * (frame.MidRadius + shift * ring);
                if (frame.Fits(centre, width, height, margin))
                    return new LabelFit(lines, fontSize, lineHeight, showGlyph, centre, Truncated: false);
            }
        }
        return null;
    }

    /// <summary>
    /// Fits a set of labels that should look alike (one ring or one fan): the largest common font size, with glyphs
    /// either on every label or on none. Labels that can't fit even at the smallest size are ellipsized.
    /// </summary>
    public static IReadOnlyList<LabelFit> FitAll(
        IReadOnlyList<(string Label, bool HasGlyph, SectorFrame Frame)> items,
        IReadOnlyList<double> fontSizes, double glyphSize,
        Func<string, double, double> measure, double margin)
    {
        if (items.Count == 0)
            return [];
        var anyGlyph = items.Any(i => i.HasGlyph);
        bool[] glyphModes = anyGlyph ? [true, false] : [false];
        // Hyphen breaks ("Ping-" / "pong") only once every size and glyph option has failed without them.
        foreach (var allowHyphen in new[] { false, true })
        {
            foreach (var size in fontSizes)
            {
                foreach (var glyphs in glyphModes)
                {
                    var fits = new LabelFit[items.Count];
                    var ok = true;
                    for (var i = 0; i < items.Count && ok; i++)
                    {
                        var fit = TryFit(items[i].Label, size, glyphSize, glyphs && items[i].HasGlyph, items[i].Frame, measure, margin, allowHyphen);
                        if (fit is null)
                            ok = false;
                        else
                            fits[i] = fit;
                    }
                    if (ok)
                        return fits;
                }
            }
        }

        // Last resort: smallest size, no glyphs, fit what fits and ellipsize the rest.
        var smallest = fontSizes[^1];
        return items.Select(item =>
            TryFit(item.Label, smallest, glyphSize, false, item.Frame, measure, margin)
            ?? Ellipsized(item.Label, smallest, item.Frame, measure, margin)).ToList();
    }

    static LabelFit Ellipsized(string label, double fontSize, SectorFrame frame, Func<string, double, double> measure, double margin)
    {
        var lineHeight = LineHeightFor(fontSize);
        var centre = Angles.Direction(frame.Range.Mid) * frame.MidRadius;
        // Widest box that still fits at the centroid, found by halving.
        double low = 0, high = frame.OuterRadius;
        for (var i = 0; i < 20; i++)
        {
            var mid = (low + high) / 2;
            if (frame.Fits(centre, mid, lineHeight, margin))
                low = mid;
            else
                high = mid;
        }
        var text = TextWrap.Ellipsize(label, s => measure(s, fontSize), low);
        return new LabelFit([text], fontSize, lineHeight, false, centre, Truncated: text != label);
    }
}
