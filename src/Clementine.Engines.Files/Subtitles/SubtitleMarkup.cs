using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Clementine.Engines.Files.Subtitles;

/// <summary>
/// Converts cue text between each format's markup and the neutral SRT-style markup (see <see cref="SubtitleCue"/>).
/// </summary>
internal static partial class SubtitleMarkup
{
    // ---------- ASS / SSA ----------

    /// <summary>
    /// ASS text → neutral: \i \b \u \s map to tags, \N (and \n with WrapStyle 2) to line breaks, \h to a no-break space,
    /// \an/\a positions are kept as a leading "{\anN}", drawings ({\p1}…) are removed, every other override is dropped.
    /// </summary>
    public static string FromAss(string raw, int wrapStyle, out bool hadDrawing)
    {
        hadDrawing = false;
        var text = new StringBuilder(raw.Length);
        var open = new List<string>();
        string? position = null;
        var drawing = false;

        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (c == '{')
            {
                var close = raw.IndexOf('}', i + 1);
                if (close < 0)
                {
                    text.Append(raw, i, raw.Length - i);
                    break;
                }
                var block = raw.Substring(i + 1, close - i - 1);
                i = close;
                if (!block.Contains('\\'))
                    continue; // {a comment}
                foreach (Match tag in AssTag().Matches(block))
                {
                    var name = tag.Groups["name"].Value;
                    var value = tag.Groups["value"].Value.Trim();
                    switch (name)
                    {
                        case "i" or "b" or "u" or "s":
                            // A bare "\i" resets to the style default, which is (almost always) off.
                            var on = value.Length > 0 && value != "0";
                            if (name == "b" && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var weight))
                                on = weight != 0;
                            Toggle(text, open, name, on);
                            break;
                        case "r":
                            CloseAll(text, open);
                            break;
                        case "p":
                            drawing = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var scale) && scale > 0;
                            hadDrawing |= drawing;
                            break;
                        case "an" when value.Length == 1 && value[0] is >= '1' and <= '9':
                            position ??= value;
                            break;
                        case "a" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var legacy):
                            position ??= LegacyAlignment(legacy);
                            break;
                    }
                }
                continue;
            }

            if (drawing)
                continue;
            if (c == '\\' && i + 1 < raw.Length)
            {
                var next = raw[i + 1];
                if (next == 'N' || (next == 'n' && wrapStyle == 2))
                {
                    text.Append('\n');
                    i++;
                    continue;
                }
                if (next == 'n')
                {
                    text.Append(' ');
                    i++;
                    continue;
                }
                if (next == 'h')
                {
                    text.Append('\u00A0');
                    i++;
                    continue;
                }
            }
            text.Append(c);
        }
        CloseAll(text, open);
        var result = TidyLines(text.ToString());
        return position is null || position == "2" || result.Length == 0 ? result : $"{{\\an{position}}}{result}";
    }

    /// <summary>Neutral → ASS: tags become overrides, line breaks become \N, other HTML-ish tags are dropped.</summary>
    public static string ToAss(string neutral)
    {
        var text = new StringBuilder(neutral.Length + 16);
        var position = 0;
        foreach (Match match in NeutralToken().Matches(neutral))
        {
            AppendAssText(text, neutral[position..match.Index]);
            position = match.Index + match.Length;
            if (match.Groups["ass"].Success)
            {
                text.Append(match.Value); // {\an8} and friends are already ASS
                continue;
            }
            var closing = match.Groups["slash"].Value == "/";
            var tag = match.Groups["tag"].Value.ToLowerInvariant();
            switch (tag)
            {
                case "i" or "b" or "u" or "s":
                    text.Append("{\\").Append(tag).Append(closing ? '0' : '1').Append('}');
                    break;
                case "font" when closing:
                    text.Append("{\\c}");
                    break;
                case "font" when FontColor().Match(match.Value) is { Success: true } color:
                    // ASS colours are &HBBGGRR&.
                    var rgb = color.Groups[1].Value.ToUpperInvariant();
                    text.Append("{\\c&H").Append(rgb, 4, 2).Append(rgb, 2, 2).Append(rgb, 0, 2).Append("&}");
                    break;
            }
        }
        AppendAssText(text, neutral[position..]);
        return text.ToString();
    }

    static void AppendAssText(StringBuilder text, string segment) => text.Append(segment.Replace("\n", "\\N"));

    // ---------- WebVTT ----------

    /// <summary>
    /// WebVTT text → neutral: &lt;i&gt; &lt;b&gt; &lt;u&gt; kept, voice/class/lang/ruby/timestamp tags dropped (ruby
    /// annotations removed, base text kept), entities decoded.
    /// </summary>
    public static string FromVtt(string raw)
    {
        var withoutRuby = RubyText().Replace(raw, "");
        var text = new StringBuilder(withoutRuby.Length);
        var position = 0;
        foreach (Match match in VttTag().Matches(withoutRuby))
        {
            text.Append(WebUtility.HtmlDecode(withoutRuby[position..match.Index]));
            position = match.Index + match.Length;
            var name = match.Groups["name"].Value.ToLowerInvariant();
            if (name is "i" or "b" or "u")
                text.Append(match.Groups["slash"].Value == "/" ? $"</{name}>" : $"<{name}>");
        }
        text.Append(WebUtility.HtmlDecode(withoutRuby[position..]));
        return TidyLines(text.ToString());
    }

    /// <summary>Neutral → WebVTT: &lt;i&gt; &lt;b&gt; &lt;u&gt; kept, other tags and ASS blocks dropped, &amp; &lt; &gt; escaped.</summary>
    public static string ToVtt(string neutral, out string? positionSetting)
    {
        positionSetting = null;
        var text = new StringBuilder(neutral.Length + 8);
        var position = 0;
        foreach (Match match in NeutralToken().Matches(neutral))
        {
            text.Append(EscapeVtt(neutral[position..match.Index]));
            position = match.Index + match.Length;
            if (match.Groups["ass"].Success)
            {
                positionSetting ??= VttPosition(match.Value);
                continue;
            }
            var name = match.Groups["tag"].Value.ToLowerInvariant();
            if (name is "i" or "b" or "u")
                text.Append(match.Groups["slash"].Value == "/" ? $"</{name}>" : $"<{name}>");
        }
        text.Append(EscapeVtt(neutral[position..]));
        return text.ToString(); // '>' is always escaped, so no "-->" can end up in the cue text
    }

    // ---------- Plain text and SRT ----------

    /// <summary>Neutral → plain text (all tags and ASS blocks removed).</summary>
    public static string ToPlain(string neutral) => TidyLines(NeutralToken().Replace(neutral, ""));

    /// <summary>
    /// SRT text → neutral. SRT already uses the neutral tags; this only normalises line endings and stray blank lines,
    /// and decodes the few HTML entities some tools write.
    /// </summary>
    public static string FromSrt(string raw)
    {
        var text = raw.Contains('&') ? SrtEntity().Replace(raw, m => WebUtility.HtmlDecode(m.Value)) : raw;
        return TidyLines(text);
    }

    /// <summary>Neutral → SRT (no blank lines inside a cue — they would end it early).</summary>
    public static string ToSrt(string neutral) => TidyLines(neutral);

    /// <summary>Removes formatting tags but keeps a "{\anN}" position (it decides whether a cue covers on-screen text).</summary>
    public static string StripFormatting(string neutral)
    {
        string? position = null;
        var stripped = NeutralToken().Replace(neutral, match =>
        {
            if (match.Groups["ass"].Success && AnTag().Match(match.Value) is { Success: true } an)
                position ??= an.Groups[1].Value;
            return "";
        });
        stripped = TidyLines(stripped);
        return position is null || stripped.Length == 0 ? stripped : $"{{\\an{position}}}{stripped}";
    }

    /// <summary>Trims each line, drops empty lines (a blank line would end an SRT/VTT cue early).</summary>
    public static string TidyLines(string text)
    {
        if (text.Length == 0)
            return text;
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        return string.Join('\n', lines.Select(l => l.TrimEnd()).Where(l => l.Trim().Length > 0));
    }

    static void Toggle(StringBuilder text, List<string> open, string tag, bool on)
    {
        if (on && !open.Contains(tag))
        {
            text.Append('<').Append(tag).Append('>');
            open.Add(tag);
        }
        else if (!on && open.Contains(tag))
        {
            // Close in reverse order so tags stay properly nested.
            var index = open.IndexOf(tag);
            for (var j = open.Count - 1; j >= index; j--)
                text.Append("</").Append(open[j]).Append('>');
            var reopen = open.Skip(index + 1).ToList();
            open.RemoveRange(index, open.Count - index);
            foreach (var t in reopen)
            {
                text.Append('<').Append(t).Append('>');
                open.Add(t);
            }
        }
    }

    static void CloseAll(StringBuilder text, List<string> open)
    {
        for (var j = open.Count - 1; j >= 0; j--)
            text.Append("</").Append(open[j]).Append('>');
        open.Clear();
    }

    /// <summary>SSA's legacy \a: 1-3 bottom, +4 top (5-7), +8 middle (9-11) → numpad-style \an.</summary>
    static string? LegacyAlignment(int a) => a switch
    {
        >= 1 and <= 3 => a.ToString(CultureInfo.InvariantCulture),
        >= 5 and <= 7 => (a + 2).ToString(CultureInfo.InvariantCulture),
        >= 9 and <= 11 => (a - 5).ToString(CultureInfo.InvariantCulture),
        _ => null,
    };

    static string? VttPosition(string assBlock)
    {
        var an = AnTag().Match(assBlock);
        if (!an.Success)
            return null;
        return an.Groups[1].Value switch
        {
            "7" => "line:0 align:start",
            "8" => "line:0",
            "9" => "line:0 align:end",
            "4" => "line:50% align:start",
            "5" => "line:50%",
            "6" => "line:50% align:end",
            "1" => "align:start",
            "3" => "align:end",
            _ => null,
        };
    }

    static string EscapeVtt(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    [GeneratedRegex(@"\\(?<name>an|fn|fs|fsc[xy]|fsp|fr[xyz]?|fa[xy]|fe|bord|xbord|ybord|shad|xshad|yshad|blur|be|alpha|[1-4]a|[1-4]c|c|a|i|b|u|s|r|p|pos|move|org|clip|iclip|fad|fade|t|k[fo]?|K|q)(?<value>[^\\]*)")]
    private static partial Regex AssTag();

    /// <summary>An HTML-ish tag (&lt;i&gt;, &lt;/font&gt;, &lt;font color=…&gt;) or an ASS override block ({\an8}).</summary>
    [GeneratedRegex(@"(?<ass>\{\\[^}]*\})|<(?<slash>/?)(?<tag>[a-zA-Z][a-zA-Z0-9]*)(?:\s[^<>]*)?>")]
    private static partial Regex NeutralToken();

    [GeneratedRegex(@"color\s*=\s*[""']?#?([0-9a-fA-F]{6})", RegexOptions.IgnoreCase)]
    private static partial Regex FontColor();

    [GeneratedRegex(@"\\an([1-9])")]
    private static partial Regex AnTag();

    [GeneratedRegex(@"<(?<slash>/?)(?<name>[a-zA-Z]+|\d[\d:.]*)(?:[.\s][^<>]*)?>")]
    private static partial Regex VttTag();

    [GeneratedRegex(@"<rt>.*?</rt>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex RubyText();

    [GeneratedRegex(@"&(?:amp|lt|gt|quot|apos|nbsp|#\d{1,6}|#x[0-9a-fA-F]{1,6});")]
    private static partial Regex SrtEntity();
}
