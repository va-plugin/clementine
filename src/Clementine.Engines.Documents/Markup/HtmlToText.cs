using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Clementine.Engines.Documents.Markup;

/// <summary>
/// Readable plain text from HTML: block elements become paragraphs, lists get bullets or numbers, table cells are
/// tab-separated, headings are underlined, &lt;pre&gt; keeps its spacing, scripts and styles are dropped.
/// </summary>
internal static partial class HtmlToText
{
    static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "noscript", "template", "head", "iframe", "object", "embed", "select", "canvas", "svg", "math",
    };

    static readonly HashSet<string> Blocks = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "section", "article", "header", "footer", "nav", "aside", "main", "blockquote", "pre", "figure",
        "figcaption", "address", "form", "fieldset", "details", "summary", "dl", "dt", "dd", "ul", "ol", "table", "tr",
        "h1", "h2", "h3", "h4", "h5", "h6", "hr", "body", "center", "caption",
    };

    static readonly HashSet<string> Paragraphs = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "blockquote", "pre", "figure", "table", "h1", "h2", "h3", "h4", "h5", "h6", "hr", "dl", "address", "section", "article",
    };

    public static string Convert(string html)
    {
        var document = new HtmlParser().ParseDocument(html);
        return Convert(document.Body ?? (INode?)document.DocumentElement ?? document);
    }

    public static string Convert(INode root)
    {
        var writer = new Writer();
        writer.Walk(root);
        return writer.ToString();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    sealed class Writer
    {
        readonly StringBuilder _text = new();
        readonly Stack<int> _orderedCounters = new();
        int _pendingNewlines;
        int _preDepth;
        int _listDepth;
        bool _atLineStart = true;

        public override string ToString() => _text.ToString().Trim('\n', ' ') + "\n";

        public void Walk(INode node)
        {
            switch (node)
            {
                case IText text:
                    WriteText(text.Data);
                    return;
                case IElement element:
                    WalkElement(element);
                    return;
                default:
                    foreach (var child in node.ChildNodes)
                        Walk(child);
                    return;
            }
        }

        void WalkElement(IElement element)
        {
            var name = element.LocalName;
            if (Skipped.Contains(name) || element.HasAttribute("hidden"))
                return;

            switch (name)
            {
                case "br":
                    NewLine(1, force: true);
                    return;
                case "hr":
                    Block(2);
                    Write("———");
                    Block(2);
                    return;
                case "img":
                    if (element.GetAttribute("alt") is { Length: > 0 } alt)
                        WriteText($"[{alt}]");
                    return;
                case "td" or "th":
                    if (element.PreviousElementSibling is not null)
                        Write("\t");
                    WalkChildren(element);
                    return;
                case "li":
                    WriteListItem(element);
                    return;
                case "ol":
                    _orderedCounters.Push(int.TryParse(element.GetAttribute("start"), out var start) ? start - 1 : 0);
                    WalkList(element);
                    _orderedCounters.Pop();
                    return;
                case "ul":
                    _orderedCounters.Push(-1);
                    WalkList(element);
                    _orderedCounters.Pop();
                    return;
                case "pre":
                    Block(2);
                    _preDepth++;
                    WalkChildren(element);
                    _preDepth--;
                    Block(2);
                    return;
                case "h1" or "h2":
                    Block(2);
                    var heading = Whitespace().Replace(element.TextContent, " ").Trim();
                    Write(heading);
                    NewLine(1, force: true);
                    Write(new string(name == "h1" ? '=' : '-', Math.Max(3, heading.Length)));
                    Block(2);
                    return;
            }

            var isBlock = Blocks.Contains(name);
            var gap = Paragraphs.Contains(name) ? 2 : 1;
            if (isBlock)
                Block(gap);
            WalkChildren(element);
            if (isBlock)
                Block(gap);
        }

        void WalkList(IElement list)
        {
            Block(_listDepth == 0 ? 2 : 1);
            _listDepth++;
            WalkChildren(list);
            _listDepth--;
            Block(_listDepth == 0 ? 2 : 1);
        }

        void WriteListItem(IElement item)
        {
            Block(1);
            var indent = new string(' ', Math.Max(0, _listDepth - 1) * 2);
            string marker;
            if (_orderedCounters.Count > 0 && _orderedCounters.Peek() >= 0)
            {
                var number = _orderedCounters.Pop() + 1;
                _orderedCounters.Push(number);
                marker = $"{number}. ";
            }
            else
            {
                marker = "• ";
            }
            Write(indent + marker);
            WalkChildren(item);
            Block(1);
        }

        void WalkChildren(INode node)
        {
            foreach (var child in node.ChildNodes)
                Walk(child);
        }

        void WriteText(string data)
        {
            if (_preDepth > 0)
            {
                foreach (var c in data.Replace("\r\n", "\n", StringComparison.Ordinal))
                {
                    if (c == '\n')
                        NewLine(1, force: true);
                    else
                        Write(c.ToString());
                }
                return;
            }
            var collapsed = Whitespace().Replace(data, " ");
            if (_atLineStart)
                collapsed = collapsed.TrimStart();
            if (collapsed.Length == 0)
                return;
            Write(collapsed);
        }

        void Write(string text)
        {
            if (text.Length == 0)
                return;
            if (_pendingNewlines > 0 && _text.Length > 0)
            {
                TrimTrailingSpaces();
                _text.Append('\n', _pendingNewlines);
            }
            _pendingNewlines = 0;
            _text.Append(text);
            _atLineStart = false;
        }

        void Block(int newlines)
        {
            if (_text.Length == 0)
                return;
            _pendingNewlines = Math.Max(_pendingNewlines, newlines);
            _atLineStart = true;
        }

        void NewLine(int count, bool force)
        {
            if (force)
            {
                if (_pendingNewlines > 0)
                {
                    _pendingNewlines = Math.Max(_pendingNewlines, count);
                }
                else
                {
                    TrimTrailingSpaces();
                    _text.Append('\n', count);
                }
                _atLineStart = true;
            }
        }

        void TrimTrailingSpaces()
        {
            while (_text.Length > 0 && _text[^1] == ' ')
                _text.Length--;
        }
    }
}
