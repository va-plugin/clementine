using System.Globalization;
using System.Text;

namespace Clementine.Engines.Documents.Markup;

/// <summary>
/// Finds every place a stylesheet names something to fetch — url(…) tokens, quoted strings inside url(), image-set(),
/// -webkit-image-set(), src() and friends, @import targets, and strings held in custom properties (which var() can drop
/// into image-set()) — decoding CSS escapes the way a browser's tokenizer does ("\2f\2f host" is "//host",
/// "\75 rl(" is "url("), and lets the caller replace them. Everything else is copied through byte for byte.
/// </summary>
internal static class CssUrls
{
    /// <summary>Where a URL-ish value was found.</summary>
    public enum Kind
    {
        /// <summary>A url() token, a string in a url-taking function, or an @import target: definitely a URL.</summary>
        Url,

        /// <summary>A string in a custom property ("--x: '…'"), which only becomes a URL if var() drops it into image-set().</summary>
        CustomProperty,
    }

    /// <summary>One @import rule: its decoded target, its media/layer/supports conditions as written, and the whole rule's text.</summary>
    /// <param name="Valid">False when browsers ignore the rule anyway (no target, inside a block, or after other rules).</param>
    public sealed record Import(string Url, string Conditions, string Original, bool Valid);

    static readonly string[] UrlFunctions = ["url", "src", "image", "image-set", "-webkit-image-set", "cross-fade", "-webkit-cross-fade"];

    /// <summary>
    /// Rewrites <paramref name="css"/>. <paramref name="mapUrl"/> gets each decoded URL and returns its replacement (written
    /// quoted and escaped), or null to keep the original text; <paramref name="mapImport"/> returns the text that replaces a
    /// whole @import rule.
    /// </summary>
    public static string Rewrite(string css, Func<string, Kind, string?> mapUrl, Func<Import, string> mapImport)
    {
        var output = new StringBuilder(css.Length + 16);
        var functions = new List<string>();
        var braces = 0;
        var rulesSeen = false;
        var customFrom = -1; // brace depth of the custom property declaration we're inside, else -1
        var customFunctions = 0;
        var i = 0;
        while (i < css.Length)
        {
            var c = css[i];
            if (c == '/' && At(css, i + 1) == '*')
            {
                var end = css.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? css.Length : end + 2;
                output.Append(css, i, end - i);
                i = end;
                continue;
            }
            if (c is '"' or '\'')
            {
                var start = i;
                var value = ConsumeString(css, ref i);
                var kind = functions.Any(IsUrlFunction) ? Kind.Url : customFrom >= 0 ? Kind.CustomProperty : (Kind?)null;
                if (kind is { } k && mapUrl(value, k) is { } replacement)
                    output.Append(Quote(replacement));
                else
                    output.Append(css, start, i - start);
                continue;
            }
            if (c == '@' && StartsIdent(css, i + 1))
            {
                var start = i;
                i++;
                var name = ConsumeName(css, ref i);
                if (name.Equals("import", StringComparison.OrdinalIgnoreCase))
                {
                    output.Append(mapImport(ConsumeImport(css, start, ref i, braces == 0 && functions.Count == 0 && !rulesSeen)));
                    continue;
                }
                output.Append(css, start, i - start);
                continue;
            }
            if (StartsIdent(css, i))
            {
                var start = i;
                var name = ConsumeName(css, ref i);
                if (At(css, i) == '(')
                {
                    if (name.Equals("url", StringComparison.OrdinalIgnoreCase) && !QuoteFollows(css, i + 1))
                    {
                        var value = ConsumeUnquotedUrl(css, ref i);
                        if (mapUrl(value, Kind.Url) is { } replacement)
                            output.Append("url(").Append(Quote(replacement)).Append(')');
                        else
                            output.Append(css, start, i - start);
                        continue;
                    }
                    functions.Add(name.ToLowerInvariant());
                    i++;
                    output.Append(css, start, i - start);
                    continue;
                }
                if (customFrom < 0 && (name.StartsWith("--", StringComparison.Ordinal) || name.Equals("initial-value", StringComparison.OrdinalIgnoreCase))
                    && ColonFollows(css, i))
                {
                    customFrom = braces;
                    customFunctions = functions.Count;
                }
                output.Append(css, start, i - start);
                continue;
            }
            switch (c)
            {
                case '(' or '[':
                    functions.Add("");
                    break;
                case ')' or ']':
                    if (functions.Count > 0)
                        functions.RemoveAt(functions.Count - 1);
                    break;
                case '{':
                    if (braces == 0 && customFrom < 0)
                        rulesSeen = true;
                    braces++;
                    break;
                case '}':
                    if (braces > 0)
                        braces--;
                    if (customFrom >= 0 && braces < customFrom)
                        customFrom = -1;
                    break;
                case ';' or '!':
                    if (customFrom >= 0 && braces == customFrom && functions.Count <= customFunctions)
                        customFrom = -1;
                    break;
            }
            output.Append(c);
            i++;
        }
        return output.ToString();
    }

    static bool IsUrlFunction(string name) => UrlFunctions.Contains(name);

    static char At(string s, int i) => i < s.Length ? s[i] : '\0';

    static bool IsNewline(char c) => c is '\n' or '\r' or '\f';

    static bool IsWhitespace(char c) => c is ' ' or '\t' or '\n' or '\r' or '\f';

    static bool IsNameStart(char c) => char.IsAsciiLetter(c) || c == '_' || c >= 0x80;

    static bool IsNameChar(char c) => IsNameStart(c) || char.IsAsciiDigit(c) || c == '-';

    static bool IsValidEscape(string s, int i) => i < s.Length && s[i] == '\\' && !IsNewline(At(s, i + 1));

    static bool IsNonPrintable(char c) => c <= '\u0008' || c == '\u000B' || (c >= '\u000E' && c <= '\u001F') || c == '\u007F';

    /// <summary>CSS Syntax §4.3.9 "would start an ident sequence".</summary>
    static bool StartsIdent(string s, int i)
    {
        if (i >= s.Length)
            return false;
        var c = s[i];
        if (c == '-')
            return i + 1 < s.Length && (IsNameStart(s[i + 1]) || s[i + 1] == '-' || IsValidEscape(s, i + 1));
        return IsNameStart(c) || IsValidEscape(s, i);
    }

    static string ConsumeName(string s, ref int i)
    {
        var name = new StringBuilder();
        while (i < s.Length)
        {
            if (IsNameChar(s[i]))
            {
                name.Append(s[i++]);
            }
            else if (IsValidEscape(s, i))
            {
                i++;
                name.Append(ConsumeEscape(s, ref i));
            }
            else
            {
                break;
            }
        }
        return name.ToString();
    }

    /// <summary>After a backslash: up to six hex digits (plus one whitespace), or the next character itself.</summary>
    static string ConsumeEscape(string s, ref int i)
    {
        if (i >= s.Length)
            return "�";
        if (char.IsAsciiHexDigit(s[i]))
        {
            var start = i;
            while (i < s.Length && i - start < 6 && char.IsAsciiHexDigit(s[i]))
                i++;
            var code = int.Parse(s.AsSpan(start, i - start), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (i < s.Length && IsWhitespace(s[i]))
                i += s[i] == '\r' && At(s, i + 1) == '\n' ? 2 : 1;
            return code == 0 || code is >= 0xD800 and <= 0xDFFF || code > 0x10FFFF ? "�" : char.ConvertFromUtf32(code);
        }
        var c = s[i++];
        if (char.IsHighSurrogate(c) && i < s.Length && char.IsLowSurrogate(s[i]))
            return new string([c, s[i++]]);
        return c.ToString();
    }

    static string ConsumeString(string s, ref int i)
    {
        var quote = s[i++];
        var value = new StringBuilder();
        while (i < s.Length)
        {
            var c = s[i];
            if (c == quote)
            {
                i++;
                break;
            }
            if (IsNewline(c))
                break; // a bad string: it ends here, and the newline isn't part of it
            if (c == '\\')
            {
                if (i + 1 >= s.Length)
                {
                    i++;
                    break;
                }
                if (IsNewline(s[i + 1]))
                {
                    i += s[i + 1] == '\r' && At(s, i + 2) == '\n' ? 3 : 2; // line continuation
                    continue;
                }
                i++;
                value.Append(ConsumeEscape(s, ref i));
                continue;
            }
            value.Append(c);
            i++;
        }
        return value.ToString();
    }

    static bool QuoteFollows(string s, int i)
    {
        while (i < s.Length && IsWhitespace(s[i]))
            i++;
        return At(s, i) is '"' or '\'';
    }

    static bool ColonFollows(string s, int i)
    {
        while (i < s.Length)
        {
            if (IsWhitespace(s[i]))
            {
                i++;
            }
            else if (s[i] == '/' && At(s, i + 1) == '*')
            {
                var end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0)
                    return false;
                i = end + 2;
            }
            else
            {
                return s[i] == ':';
            }
        }
        return false;
    }

    /// <summary>From the '(' of an unquoted url(…) to just past its ')'; bad URLs are consumed the way browsers do.</summary>
    static string ConsumeUnquotedUrl(string s, ref int i)
    {
        i++;
        while (i < s.Length && IsWhitespace(s[i]))
            i++;
        var value = new StringBuilder();
        while (i < s.Length)
        {
            var c = s[i];
            if (c == ')')
            {
                i++;
                return value.ToString();
            }
            if (IsWhitespace(c))
            {
                while (i < s.Length && IsWhitespace(s[i]))
                    i++;
                if (i >= s.Length || s[i] == ')')
                {
                    i = Math.Min(i + 1, s.Length);
                    return value.ToString();
                }
                break;
            }
            if (c is '"' or '\'' or '(' || IsNonPrintable(c))
                break;
            if (c == '\\')
            {
                if (!IsValidEscape(s, i))
                    break;
                i++;
                value.Append(ConsumeEscape(s, ref i));
                continue;
            }
            value.Append(c);
            i++;
        }
        // A bad url: skip to its closing ')' (escaped parentheses don't count). Browsers fetch nothing for it, but its
        // text is still checked by the caller.
        while (i < s.Length)
        {
            if (s[i] == ')')
            {
                i++;
                break;
            }
            if (IsValidEscape(s, i))
            {
                i++;
                ConsumeEscape(s, ref i);
                continue;
            }
            value.Append(s[i]);
            i++;
        }
        return value.ToString();
    }

    /// <summary>"@import" has been read; reads its target and conditions up to and including the ';'.</summary>
    static Import ConsumeImport(string s, int start, ref int i, bool allowed)
    {
        SkipWhitespaceAndComments(s, ref i);
        string? url = null;
        if (At(s, i) is '"' or '\'')
        {
            url = ConsumeString(s, ref i);
        }
        else if (StartsIdent(s, i))
        {
            var nameStart = i;
            var name = ConsumeName(s, ref i);
            if (name.Equals("url", StringComparison.OrdinalIgnoreCase) && At(s, i) == '(')
            {
                if (QuoteFollows(s, i + 1))
                {
                    i++;
                    SkipWhitespaceAndComments(s, ref i);
                    url = ConsumeString(s, ref i);
                    SkipWhitespaceAndComments(s, ref i);
                    if (At(s, i) == ')')
                        i++;
                    else
                        url = null;
                }
                else
                {
                    url = ConsumeUnquotedUrl(s, ref i);
                }
            }
            else
            {
                i = nameStart;
            }
        }

        var conditionsStart = i;
        var depth = 0;
        var conditionsEnd = s.Length;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '/' && At(s, i + 1) == '*')
            {
                var end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? s.Length : end + 2;
                continue;
            }
            if (c is '"' or '\'')
            {
                ConsumeString(s, ref i);
                continue;
            }
            if (c == '\\' && IsValidEscape(s, i))
            {
                i++;
                ConsumeEscape(s, ref i);
                continue;
            }
            if (c is '(' or '[')
                depth++;
            else if (c is ')' or ']' && depth > 0)
                depth--;
            else if (depth == 0 && c == ';')
            {
                conditionsEnd = i;
                i++;
                break;
            }
            else if (depth == 0 && c is '{' or '}')
            {
                conditionsEnd = i;
                url = null; // "@import x {" is not an import; the block that follows is dropped as junk
                break;
            }
            i++;
        }
        var conditions = s[conditionsStart..Math.Min(conditionsEnd, s.Length)].Trim();
        return new Import(url ?? "", conditions, s[start..i], allowed && url is not null);
    }

    static void SkipWhitespaceAndComments(string s, ref int i)
    {
        while (i < s.Length)
        {
            if (IsWhitespace(s[i]))
            {
                i++;
            }
            else if (s[i] == '/' && At(s, i + 1) == '*')
            {
                var end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? s.Length : end + 2;
            }
            else
            {
                return;
            }
        }
    }

    /// <summary>A double-quoted CSS string for <paramref name="value"/>.</summary>
    public static string Quote(string value)
    {
        var quoted = new StringBuilder(value.Length + 2).Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"' or '\\':
                    quoted.Append('\\').Append(c);
                    break;
                case < ' ' or '\u007F':
                    quoted.Append('\\').Append(((int)c).ToString("x", CultureInfo.InvariantCulture)).Append(' ');
                    break;
                default:
                    quoted.Append(c);
                    break;
            }
        }
        return quoted.Append('"').ToString();
    }

    /// <summary>
    /// The CSS that applies an imported stylesheet's rules under the import's conditions: "screen" → "@media screen { … }",
    /// "layer(base)" → "@layer base { … }", "supports(display: grid)" → "@supports (display: grid) { … }".
    /// </summary>
    public static string WrapImported(string css, string conditions)
    {
        var rest = conditions.Trim();
        var opening = new StringBuilder();
        var closing = new StringBuilder();
        if (rest.StartsWith("layer", StringComparison.OrdinalIgnoreCase) && (rest.Length == 5 || rest[5] is '(' or ' ' or '\t' or '\n'))
        {
            var name = "";
            rest = rest[5..];
            if (rest.StartsWith('('))
            {
                var close = MatchingParen(rest, 0);
                name = rest[1..close].Trim();
                rest = rest[Math.Min(close + 1, rest.Length)..];
            }
            opening.Append("@layer ").Append(name).Append(" {\n");
            closing.Insert(0, "\n}");
            rest = rest.TrimStart();
        }
        if (rest.StartsWith("supports(", StringComparison.OrdinalIgnoreCase))
        {
            var close = MatchingParen(rest, 8);
            opening.Append("@supports (").Append(rest[9..close]).Append(") {\n");
            closing.Insert(0, "\n}");
            rest = rest[Math.Min(close + 1, rest.Length)..].TrimStart();
        }
        if (rest.Length > 0)
        {
            opening.Append("@media ").Append(rest).Append(" {\n");
            closing.Insert(0, "\n}");
        }
        return opening.Length == 0 ? css : opening.Append(css).Append(closing).ToString();
    }

    static int MatchingParen(string s, int open)
    {
        var depth = 0;
        for (var i = open; i < s.Length; i++)
        {
            if (s[i] == '(')
                depth++;
            else if (s[i] == ')' && --depth == 0)
                return i;
        }
        return s.Length;
    }
}
