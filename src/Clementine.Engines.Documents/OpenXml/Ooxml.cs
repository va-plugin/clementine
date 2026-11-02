using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml;

namespace Clementine.Engines.Documents.OpenXml;

/// <summary>
/// Small, forgiving accessors for Office Open XML elements by local name. The built-in converters read markup this way
/// (rather than through the SDK's typed properties) so unknown or newer elements and attributes never throw, and the
/// same code works for every container that shares child elements (w:rPr in styles, numbering, runs…).
/// </summary>
internal static class Ooxml
{
    public const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    public const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    public const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    public const string Mc = "http://schemas.openxmlformats.org/markup-compatibility/2006";

    public static OpenXmlElement? Child(this OpenXmlElement? element, string localName)
    {
        if (element is null)
            return null;
        foreach (var child in element.ChildElements)
        {
            if (child.LocalName == localName)
                return child;
        }
        return null;
    }

    public static IEnumerable<OpenXmlElement> Children(this OpenXmlElement? element, string localName)
    {
        if (element is null)
            yield break;
        foreach (var child in element.ChildElements)
        {
            if (child.LocalName == localName)
                yield return child;
        }
    }

    /// <summary>The first descendant with this local name (depth-first), or null.</summary>
    public static OpenXmlElement? Find(this OpenXmlElement? element, string localName) =>
        element?.Descendants().FirstOrDefault(d => d.LocalName == localName);

    /// <summary>An attribute by local name, in any namespace (w:val, val, r:embed…).</summary>
    public static string? Attr(this OpenXmlElement? element, string localName)
    {
        if (element is null || !element.HasAttributes)
            return null;
        foreach (var attribute in element.GetAttributes())
        {
            if (attribute.LocalName == localName)
                return attribute.Value;
        }
        return null;
    }

    /// <summary>An attribute by local name in one namespace (r:id rather than a VML shape's own id).</summary>
    public static string? Attr(this OpenXmlElement? element, string localName, string namespaceUri)
    {
        if (element is null || !element.HasAttributes)
            return null;
        foreach (var attribute in element.GetAttributes())
        {
            if (attribute.LocalName == localName && attribute.NamespaceUri == namespaceUri)
                return attribute.Value;
        }
        return null;
    }

    public static double? Number(string? text) =>
        text is not null && double.TryParse(text.Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
            ? value
            : null;

    public static int? Integer(string? text) =>
        text is not null && int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>A toggle element (&lt;w:b/&gt;, &lt;w:b w:val="0"/&gt;): present and not switched off.</summary>
    public static bool IsOn(OpenXmlElement? toggle) => toggle is not null && IsTrue(toggle.Attr("val") ?? "1");

    public static bool IsTrue(string? value) => value?.Trim().ToLowerInvariant() is "1" or "true" or "on" or "t";

    /// <summary>Twentieths of a point (twips) → points.</summary>
    public static double? Twips(string? text) => Number(text) is { } value ? value / 20.0 : null;

    /// <summary>English Metric Units → points.</summary>
    public static double? Emu(string? text) => Number(text) is { } value ? value / 12700.0 : null;

    /// <summary>The element to use from an mc:AlternateContent: the first Choice holding <paramref name="wanted"/>, else the Fallback.</summary>
    public static OpenXmlElement? PickAlternate(OpenXmlElement alternate, Func<OpenXmlElement, bool> wanted)
    {
        foreach (var choice in alternate.Children("Choice"))
        {
            if (wanted(choice))
                return choice;
        }
        return alternate.Child("Fallback");
    }

    /// <summary>Text for HTML element content and attribute values: &amp; &lt; &gt; &quot; escaped, control characters dropped.</summary>
    public static string Html(string text)
    {
        var needs = false;
        foreach (var c in text)
        {
            if (c is '&' or '<' or '>' or '"' || (char.IsControl(c) && c is not '\t' and not '\n'))
            {
                needs = true;
                break;
            }
        }
        if (!needs)
            return text;
        var builder = new StringBuilder(text.Length + 16);
        foreach (var c in text)
        {
            switch (c)
            {
                case '&':
                    builder.Append("&amp;");
                    break;
                case '<':
                    builder.Append("&lt;");
                    break;
                case '>':
                    builder.Append("&gt;");
                    break;
                case '"':
                    builder.Append("&quot;");
                    break;
                default:
                    if (!char.IsControl(c) || c is '\t' or '\n')
                        builder.Append(c);
                    break;
            }
        }
        return builder.ToString();
    }

    /// <summary>A CSS string literal ("…" with quotes, backslashes and newlines escaped).</summary>
    public static string CssString(string text)
    {
        var builder = new StringBuilder(text.Length + 2).Append('"');
        foreach (var c in text)
        {
            switch (c)
            {
                case '"' or '\\':
                    builder.Append('\\').Append(c);
                    break;
                case '\n':
                    builder.Append("\\A ");
                    break;
                case '<':
                    // Never let "</style" appear inside the stylesheet.
                    builder.Append("\\3C ");
                    break;
                default:
                    if (!char.IsControl(c))
                        builder.Append(c);
                    break;
            }
        }
        return builder.Append('"').ToString();
    }

    /// <summary>A CSS length in points with at most two decimals ("12.5pt").</summary>
    public static string Pt(double points) => Math.Round(points, 2).ToString("0.##", CultureInfo.InvariantCulture) + "pt";

    /// <summary>A plain CSS number ("1.15").</summary>
    public static string Css(double value) => Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>Links that may appear in an output page: web, mail, phone and in-document anchors.</summary>
    public static string? SafeHref(string? href)
    {
        if (string.IsNullOrWhiteSpace(href))
            return null;
        var value = href.Trim();
        if (value.StartsWith('#'))
            return value.Length > 1 ? value : null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return null;
        return uri.Scheme is "http" or "https" or "mailto" or "tel" ? uri.OriginalString : null;
    }
}
