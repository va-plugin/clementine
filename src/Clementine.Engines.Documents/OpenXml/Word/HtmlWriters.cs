using System.Text;
using static Clementine.Engines.Documents.OpenXml.Ooxml;

namespace Clementine.Engines.Documents.OpenXml.Word;

/// <summary>Turns CSS declaration blocks into short, shared class names ("p3", "r12") so big documents stay small.</summary>
internal sealed class CssClasses
{
    readonly Dictionary<string, string> _byCss = new(StringComparer.Ordinal);
    readonly StringBuilder _rules = new();

    public string For(string prefix, string declarations)
    {
        var key = prefix + "{" + declarations;
        if (_byCss.TryGetValue(key, out var name))
            return name;
        name = prefix + (_byCss.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        _byCss[key] = name;
        _rules.Append('.').Append(name).Append(" { ").Append(declarations).Append(" }\n");
        return name;
    }

    public string Rules => _rules.ToString();
}

/// <summary>
/// Writes a paragraph's inline HTML. Text with the same formatting is merged into one span; links stay open across
/// runs; with <see cref="SplitTabs"/> each tab ends a segment (links are closed and reopened around it) so the
/// paragraph can be laid out on its tab stops.
/// </summary>
internal sealed class InlineWriter
{
    readonly StringBuilder _current = new();
    readonly List<string> _segments = [];
    readonly List<(string Open, string Close)> _wrappers = [];
    string? _spanKey;
    string _spanClose = "";

    public bool SplitTabs { get; init; }

    /// <summary>Text, a picture or another visible thing was written (an empty paragraph still needs a line).</summary>
    public bool HasContent { get; private set; }

    /// <summary>Something inside is absolutely positioned against the paragraph.</summary>
    public bool NeedsPositioning { get; set; }

    /// <summary>Text boxes and floating blocks found inline; written before the paragraph.</summary>
    public StringBuilder Before { get; } = new();

    public int TabCount { get; private set; }

    public void Text(string text, string spanOpen, string spanClose, string key)
    {
        if (text.Length == 0)
            return;
        if (_spanKey != key)
        {
            EndSpan();
            _current.Append(spanOpen);
            _spanKey = key;
            _spanClose = spanClose;
        }
        _current.Append(Html(text));
        HasContent = true;
    }

    /// <summary>Markup that isn't run text (a picture, a break, a footnote mark).</summary>
    public void Raw(string html, bool visible)
    {
        EndSpan();
        _current.Append(html);
        HasContent |= visible;
    }

    public void Open(string open, string close)
    {
        EndSpan();
        _current.Append(open);
        _wrappers.Add((open, close));
    }

    public void Close()
    {
        if (_wrappers.Count == 0)
            return;
        EndSpan();
        _current.Append(_wrappers[^1].Close);
        _wrappers.RemoveAt(_wrappers.Count - 1);
    }

    public int OpenCount => _wrappers.Count;

    public void Tab()
    {
        TabCount++;
        if (!SplitTabs)
        {
            EndSpan();
            _current.Append('\t');
            return;
        }
        EndSpan();
        for (var i = _wrappers.Count - 1; i >= 0; i--)
            _current.Append(_wrappers[i].Close);
        _segments.Add(_current.ToString());
        _current.Clear();
        foreach (var (open, _) in _wrappers)
            _current.Append(open);
    }

    /// <summary>Closes everything and returns the segments (one unless tabs were split).</summary>
    public IReadOnlyList<string> Finish()
    {
        EndSpan();
        for (var i = _wrappers.Count - 1; i >= 0; i--)
            _current.Append(_wrappers[i].Close);
        _wrappers.Clear();
        _segments.Add(_current.ToString());
        _current.Clear();
        return _segments;
    }

    void EndSpan()
    {
        if (_spanKey is null)
            return;
        _current.Append(_spanClose);
        _spanKey = null;
    }
}

/// <summary>
/// Groups list paragraphs into nested &lt;ol&gt;/&lt;ul&gt; elements. Items carry their own (absolute) indent, so each
/// &lt;li&gt; gets the margin relative to the item it is nested in.
/// </summary>
internal sealed class ListWriter(StringBuilder html)
{
    sealed class Level
    {
        public required int Depth { get; init; }

        public required string Tag { get; init; }

        public required double ParentLeft { get; init; }

        public bool ItemOpen { get; set; }

        public double ItemLeft { get; set; }
    }

    readonly List<Level> _open = [];

    public bool IsOpen => _open.Count > 0;

    /// <summary>Starts a list item; returns the margin-left (pt) the item needs relative to its container.</summary>
    public double Item(int depth, string tag, double absoluteLeft)
    {
        while (_open.Count > 0 && _open[^1].Depth > depth)
            Pop();
        if (_open.Count > 0 && _open[^1].Depth == depth && _open[^1].Tag != tag)
            Pop();
        if (_open.Count == 0 || _open[^1].Depth < depth)
        {
            var parentLeft = _open.Count > 0 && _open[^1].ItemOpen ? _open[^1].ItemLeft : _open.Count > 0 ? _open[^1].ParentLeft : 0;
            html.Append('<').Append(tag).Append('>');
            _open.Add(new Level { Depth = depth, Tag = tag, ParentLeft = parentLeft });
        }
        else if (_open[^1].ItemOpen)
        {
            html.Append("</li>");
        }
        var level = _open[^1];
        level.ItemOpen = true;
        level.ItemLeft = absoluteLeft;
        return absoluteLeft - level.ParentLeft;
    }

    public void CloseAll()
    {
        while (_open.Count > 0)
            Pop();
    }

    void Pop()
    {
        var level = _open[^1];
        if (level.ItemOpen)
            html.Append("</li>");
        html.Append("</").Append(level.Tag).Append('>');
        _open.RemoveAt(_open.Count - 1);
    }
}

/// <summary>Font stacks and single-line heights for Word font names.</summary>
internal static class WordFonts
{
    static readonly Dictionary<string, string[]> Substitutes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Aptos"] = ["Calibri"],
        ["Aptos Display"] = ["Calibri Light", "Calibri"],
        ["Aptos Narrow"] = ["Arial Narrow"],
        ["Aptos Light"] = ["Calibri Light"],
        ["Aptos Serif"] = ["Cambria"],
        ["Aptos Mono"] = ["Consolas"],
        ["Helvetica"] = ["Arial"],
        ["Helvetica Neue"] = ["Arial"],
        ["Arial Nova"] = ["Arial"],
        ["Times"] = ["Times New Roman"],
        ["Courier"] = ["Courier New"],
        ["Menlo"] = ["Consolas"],
        ["Monaco"] = ["Consolas"],
        ["Avenir"] = ["Segoe UI"],
        ["Avenir Next"] = ["Segoe UI"],
        ["Georgia Pro"] = ["Georgia"],
        ["Calibri"] = ["Carlito"],
        ["Cambria"] = ["Caladea"],
        ["MS Mincho"] = ["Yu Mincho"],
        ["MS Gothic"] = ["Yu Gothic"],
        ["SimSun"] = ["Microsoft YaHei"],
    };

    static readonly Dictionary<string, double> LineHeights = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Calibri"] = 1.22, ["Calibri Light"] = 1.22, ["Aptos"] = 1.22, ["Aptos Display"] = 1.22, ["Aptos Narrow"] = 1.22,
        ["Cambria"] = 1.17, ["Times New Roman"] = 1.15, ["Arial"] = 1.15, ["Georgia"] = 1.14, ["Verdana"] = 1.22,
        ["Tahoma"] = 1.21, ["Segoe UI"] = 1.33, ["Courier New"] = 1.13, ["Consolas"] = 1.17, ["Garamond"] = 1.12,
        ["Trebuchet MS"] = 1.16, ["Century Gothic"] = 1.23, ["Book Antiqua"] = 1.17, ["Palatino Linotype"] = 1.32,
        ["Arial Narrow"] = 1.15, ["Franklin Gothic Book"] = 1.13, ["Lucida Sans"] = 1.17,
    };

    /// <summary>"Calibri", "Carlito", "Yu Gothic", sans-serif</summary>
    public static string Stack(string? font, string? eastAsia, string? generic)
    {
        var names = new List<string>();
        void Add(string? name)
        {
            if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name.Trim(), StringComparer.OrdinalIgnoreCase))
                names.Add(name.Trim());
        }
        Add(font);
        if (font is not null && Substitutes.TryGetValue(font, out var substitutes))
            foreach (var substitute in substitutes)
                Add(substitute);
        Add(eastAsia);
        var family = generic ?? GuessGeneric(font);
        var builder = new StringBuilder();
        foreach (var name in names)
            builder.Append(CssString(name)).Append(", ");
        builder.Append(family);
        return builder.ToString();
    }

    public static string GuessGeneric(string? font)
    {
        if (font is null)
            return "sans-serif";
        var name = font.ToLowerInvariant();
        if (name.Contains("mono") || name.Contains("courier") || name.Contains("consolas") || name.Contains("code"))
            return "monospace";
        if (name.Contains("times") || name.Contains("cambria") || name.Contains("georgia") || name.Contains("garamond") || name.Contains("serif") && !name.Contains("sans")
            || name.Contains("book") || name.Contains("palatino") || name.Contains("baskerville") || name.Contains("mincho") || name.Contains("song")
            || name.Contains("bodoni") || name.Contains("century schoolbook") || name.Contains("constantia"))
            return "serif";
        if (name.Contains("script") || name.Contains("brush") || name.Contains("hand"))
            return "cursive";
        return "sans-serif";
    }

    /// <summary>Word's "single" line height as a multiple of the font size.</summary>
    public static double SingleLine(string? font) =>
        font is not null && LineHeights.TryGetValue(font, out var value) ? value : 1.17;
}
