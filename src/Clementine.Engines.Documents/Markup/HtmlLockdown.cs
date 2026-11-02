using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace Clementine.Engines.Documents.Markup;

/// <summary>
/// Makes an untrusted HTML page safe to print from disk. Edge already runs with scripts and networking off, but a
/// page opened from a file:// URL has one more way out: protocol-relative ("//cdn.example.com/x.png"), UNC
/// ("\\host\share") and "file://host/" links resolve to network shares, which Windows fetches itself over SMB/WebDAV
/// (outside the browser's proxy) — slow, and it can hand the user's Windows credentials to that host. This removes
/// those links (however they're spelled: CSS escapes, image-set() strings, custom properties, @import chains, linked
/// local stylesheets that point onwards), scripts, frames, refreshes and prefetch hints; local files, data: URIs and
/// ordinary text stay.
/// </summary>
internal static partial class HtmlLockdown
{
    static readonly string[] UrlAttributes =
        ["src", "href", "xlink:href", "srcset", "imagesrcset", "poster", "data", "background", "action", "formaction", "cite",
         "longdesc", "lowsrc", "dynsrc", "manifest", "ping", "codebase", "archive", "usemap", "profile", "icon"];

    static readonly string[] HintRelations = ["prefetch", "preload", "modulepreload", "dns-prefetch", "preconnect", "prerender", "import"];

    /// <summary>
    /// Elements that run code or show other documents. &lt;noscript&gt; goes too: Edge parses it as raw text (scripting is
    /// on, just blocked), so markup hidden in it could come back to life past this sanitiser — and it's never shown anyway.
    /// </summary>
    static readonly string[] ActiveElements =
        ["script", "iframe", "frame", "frameset", "object", "embed", "applet", "portal", "fencedframe", "noscript"];

    /// <summary>SVG animation elements, which can rewrite an href after this sanitiser has looked at it.</summary>
    static readonly string[] AnimationElements = ["animate", "set", "animatemotion", "animatetransform", "animatecolor"];

    const int MaxImportDepth = 8;
    const long MaxStylesheetBytes = 8L * 1024 * 1024;
    const long MaxInlinedBytes = 32L * 1024 * 1024;

    /// <summary>No scripts, frames, plugins or connections; pictures, styles, fonts and media only from local files or data: URIs.</summary>
    public const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'none'; object-src 'none'; frame-src 'none'; worker-src 'none'; connect-src 'none'; " +
        "form-action 'none'; base-uri file:; img-src file: data: blob:; style-src file: data: 'unsafe-inline'; font-src file: data:; media-src file: data:";

    /// <summary>For pages Clementine writes for people to open (Markdown → HTML): nothing runs, but web pictures and links still work.</summary>
    public const string WebPagePolicy = "script-src 'none'; object-src 'none'; frame-src 'none'; worker-src 'none'";

    /// <summary>
    /// Reads <paramref name="htmlPath"/> (honouring its declared charset), locks it down and returns it as UTF-8 HTML.
    /// Relative links keep pointing at <paramref name="htmlPath"/>'s folder through a &lt;base&gt; element, so the result
    /// can be printed from any folder.
    /// </summary>
    public static string ForPrinting(string htmlPath)
    {
        var bytes = File.ReadAllBytes(htmlPath);
        if (bytes.LongLength > TextFiles.MaxTextBytes)
            throw new OperationException($"This page is too large to print ({bytes.LongLength / (1024 * 1024)} MB).");
        return Sanitize(Decode(bytes), Path.GetDirectoryName(Path.GetFullPath(htmlPath)));
    }

    /// <summary>
    /// The print lockdown: everything below, plus linked local stylesheets (and local @imports) are inlined after the same
    /// cleaning and every other stylesheet link or import is dropped, relative links are made absolute against
    /// <paramref name="baseDirectory"/>, and a strict Content-Security-Policy is added.
    /// </summary>
    public static string Sanitize(string html, string? baseDirectory) => Lockdown(html, baseDirectory, print: true);

    /// <summary>
    /// For an HTML page Clementine hands to the user (Markdown → HTML): scripts, frames, plugins, event handlers,
    /// javascript: URLs, refreshes and network-share links go; ordinary markup, web links, pictures and stylesheets stay.
    /// <paramref name="documentDirectory"/> is the folder the page's relative links mean.
    /// </summary>
    public static string ForWebPage(string html, string? documentDirectory) => Lockdown(html, documentDirectory, print: false);

    static string Lockdown(string html, string? directory, bool print)
    {
        var document = new HtmlParser().ParseDocument(html);

        foreach (var element in Elements(document))
        {
            var name = element.LocalName;
            if (ActiveElements.Contains(name, StringComparer.OrdinalIgnoreCase))
                element.Remove();
            else if (AnimationElements.Contains(name, StringComparer.OrdinalIgnoreCase) && AnimatesUrl(element))
                element.Remove();
        }
        foreach (var meta in Elements(document).Where(e => e.LocalName == "meta"))
        {
            var equiv = meta.GetAttribute("http-equiv");
            if (equiv is not null && (equiv.Equals("refresh", StringComparison.OrdinalIgnoreCase) || equiv.Equals("content-type", StringComparison.OrdinalIgnoreCase)
                || equiv.Equals("set-cookie", StringComparison.OrdinalIgnoreCase) || equiv.StartsWith("content-security-policy", StringComparison.OrdinalIgnoreCase)))
                meta.Remove();
            else if (meta.HasAttribute("charset"))
                meta.Remove();
        }
        foreach (var link in Elements(document).Where(e => e.LocalName == "link" && Relations(e).Any(r => HintRelations.Contains(r, StringComparer.OrdinalIgnoreCase))))
            link.Remove();

        // What relative URLs in the page mean: its own <base> (if that is safe), else the folder it came from.
        var directoryUri = directory is null ? null : new Uri(HtmlPage.DirectoryUri(directory));
        var head = document.Head!;
        var bases = Elements(document).Where(e => e.LocalName == "base").ToList();
        var pageBase = bases.FirstOrDefault(b => !string.IsNullOrWhiteSpace(b.GetAttribute("href")) && !IsUnsafe(b.GetAttribute("href"), directoryUri));
        foreach (var extra in bases.Where(b => b != pageBase))
            extra.Remove();
        var documentUri = directoryUri;
        if (pageBase is not null)
        {
            var href = Clean(pageBase.GetAttribute("href")!);
            documentUri = directoryUri is not null && Uri.TryCreate(directoryUri, href, out var combined) ? combined
                : Uri.TryCreate(href, UriKind.Absolute, out var absolute) ? absolute : null;
        }

        var stylesheets = new StylesheetInliner(print);
        foreach (var element in Elements(document))
            SanitizeAttributes(element, documentUri);
        foreach (var style in Elements(document).Where(e => e.LocalName == "style"))
            style.TextContent = EscapeForStyleElement(stylesheets.CleanCss(style.TextContent, documentUri, absolutize: false, depth: 0));
        foreach (var link in Elements(document).Where(e => e.LocalName == "link" && Relations(e).Contains("stylesheet", StringComparer.OrdinalIgnoreCase)).ToList())
            stylesheets.ReplaceLink(document, link, documentUri);

        // The page is printed from a temp folder, but its pictures and stylesheets live beside the original: point
        // relative links there. (Not with a <base>, which would turn "#chapter-2" into a link to another document.)
        if (print && pageBase is null && directoryUri is not null)
            Absolutize(document, directoryUri);
        // Scripts are gone already; the policy is the second lock (and stops anything we missed fetching http(s)).
        var policy = document.CreateElement("meta");
        policy.SetAttribute("http-equiv", "Content-Security-Policy");
        policy.SetAttribute("content", print ? ContentSecurityPolicy : WebPagePolicy);
        head.Prepend(policy);
        var charset = document.CreateElement("meta");
        charset.SetAttribute("charset", "utf-8");
        head.Prepend(charset);

        var doctype = document.Doctype is null ? "" : "<!DOCTYPE html>\n";
        return doctype + document.DocumentElement.OuterHtml;
    }

    static void SanitizeAttributes(IElement element, Uri? documentUri)
    {
        foreach (var attribute in element.Attributes.ToList())
        {
            var name = attribute.Name;
            var value = attribute.Value;
            if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase))
            {
                element.RemoveAttribute(name);
                continue;
            }
            if (!UrlAttributes.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                // style="…", and SVG presentation attributes (fill, filter, mask, cursor…) are CSS too.
                if (name.Equals("style", StringComparison.OrdinalIgnoreCase) || value.Contains('('))
                {
                    var clean = SanitizeCss(value, documentUri);
                    if (clean != value)
                        element.SetAttribute(name, clean);
                }
                continue;
            }
            var unsafeUrl = name.EndsWith("srcset", StringComparison.OrdinalIgnoreCase)
                ? SrcsetSeparators().Split(value).Any(candidate => IsUnsafe(candidate, documentUri))
                : IsUnsafe(value, documentUri);
            if (!unsafeUrl)
                continue;
            // A protocol-relative link stays clickable as https; anything that would be fetched goes.
            var slashed = Clean(value).Replace('\\', '/');
            if (name.Equals("href", StringComparison.OrdinalIgnoreCase) && element.LocalName is "a" or "area"
                && slashed.StartsWith("//", StringComparison.Ordinal) && slashed.Length > 2 && slashed[2] != '/')
                element.SetAttribute(name, "https:" + slashed);
            else
                element.RemoveAttribute(name);
        }
    }

    /// <summary>&lt;set attributeName="href" to="//host/x"&gt; and friends.</summary>
    static bool AnimatesUrl(IElement animation)
    {
        var target = animation.GetAttribute("attributeName");
        if (target is null)
            return false;
        var local = target.Contains(':') ? target[(target.IndexOf(':') + 1)..] : target;
        return UrlAttributes.Contains(local.Trim(), StringComparer.OrdinalIgnoreCase) || local.Trim().Equals("style", StringComparison.OrdinalIgnoreCase);
    }

    static IEnumerable<string> Relations(IElement element) => (element.GetAttribute("rel") ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Every element of the document — including those inside &lt;template&gt; contents and shadow roots, which a
    /// declarative shadow root (&lt;template shadowrootmode="open"&gt;) makes live in Edge. A snapshot, so callers may remove.
    /// </summary>
    static List<IElement> Elements(INode root)
    {
        var result = new List<IElement>();
        var pending = new Stack<INode>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (node is IElement element)
            {
                if (node != root)
                    result.Add(element);
                if (element.ShadowRoot is { } shadow)
                    pending.Push(shadow);
                if (element is IHtmlTemplateElement template)
                    pending.Push(template.Content);
            }
            for (var i = node.ChildNodes.Length - 1; i >= 0; i--)
                pending.Push(node.ChildNodes[i]);
        }
        return result;
    }

    /// <summary>
    /// True for links that would leave the machine through Windows file sharing: "//host/…", "\\host\…", "file://host/…"
    /// (but not "file:///C:/…" or "file://localhost/…"). Also javascript: and vbscript: URLs.
    /// </summary>
    public static bool IsUnsafeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;
        // Browsers ignore leading/trailing whitespace and control characters, and treat '\' like '/'.
        var value = Clean(url).Replace('\\', '/');
        if (value.StartsWith("//", StringComparison.Ordinal))
            return true;
        if (value.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) || value.StartsWith("vbscript:", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!value.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return false;
        var rest = value[5..].TrimStart('/');
        var slashes = value.Length - 5 - rest.Length;
        // "file:////host/share" is a UNC path; "file:///C:/x", "file:/C:/x" and "file:C:/x" are local.
        if (slashes != 2)
            return slashes >= 4;
        // "file://host/x" is a share unless the "host" is empty, localhost or a drive letter.
        var host = rest.Split('/')[0];
        return !(host.Length == 0 || host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || DriveLetter().IsMatch(host));
    }

    /// <summary>
    /// <see cref="IsUnsafeUrl(string?)"/>, and then the simplest strong rule: resolved against <paramref name="baseUri"/>,
    /// a file: URL with a host (other than localhost) or a UNC path is unsafe however it was spelled.
    /// </summary>
    internal static bool IsUnsafe(string? url, Uri? baseUri)
    {
        if (IsUnsafeUrl(url))
            return true;
        if (string.IsNullOrWhiteSpace(url))
            return false;
        var value = Clean(url);
        var resolved = baseUri is not null && Uri.TryCreate(baseUri, value, out var combined) ? combined
            : Uri.TryCreate(value, UriKind.Absolute, out var absolute) ? absolute : null;
        return resolved is not null && IsNetworkFile(resolved);
    }

    static bool IsNetworkFile(Uri uri) =>
        uri.IsFile && (uri.IsUnc || uri.Host.Length > 0) && !uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);

    /// <summary>What a browser makes of a URL attribute: no control characters (tabs and newlines vanish anywhere), trimmed.</summary>
    static string Clean(string url) => new string(url.Where(c => !char.IsControl(c)).ToArray()).Trim();

    static void Absolutize(IHtmlDocument document, Uri baseUri)
    {
        foreach (var element in Elements(document))
        {
            foreach (var attribute in element.Attributes.ToList())
            {
                var name = attribute.Name;
                if (name.Equals("style", StringComparison.OrdinalIgnoreCase))
                    element.SetAttribute(name, AbsolutizeCss(attribute.Value, baseUri));
                else if (name.EndsWith("srcset", StringComparison.OrdinalIgnoreCase))
                    element.SetAttribute(name, string.Join(", ", attribute.Value.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(candidate =>
                    {
                        var parts = candidate.Trim().Split(' ', 2);
                        return Resolve(parts[0], baseUri) + (parts.Length > 1 ? " " + parts[1] : "");
                    })));
                else if (UrlAttributes.Contains(name, StringComparer.OrdinalIgnoreCase))
                    element.SetAttribute(name, Resolve(attribute.Value, baseUri));
            }
        }
        foreach (var style in Elements(document).Where(e => e.LocalName == "style"))
            style.TextContent = EscapeForStyleElement(AbsolutizeCss(style.TextContent, baseUri));
    }

    /// <summary>"pics/a b.png" → "file:///C:/Pages/pics/a%20b.png"; fragments ("#top"), absolute URLs and data: stay as they are.</summary>
    static string Resolve(string value, Uri baseUri)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#') || SchemePrefix().IsMatch(trimmed))
            return value;
        return Uri.TryCreate(baseUri, trimmed, out var combined) ? combined.AbsoluteUri : value;
    }

    static string AbsolutizeCss(string css, Uri baseUri) =>
        CssUrls.Rewrite(css,
            (url, kind) => kind == CssUrls.Kind.Url && Resolve(url, baseUri) is var resolved && resolved != url ? resolved : null,
            import => import.Original);

    /// <summary>
    /// Neutralises every url(…), image-set() string, custom-property string and @import target in CSS that points at a
    /// network share — CSS escapes decoded first — resolving relative ones against <paramref name="baseUri"/> when given.
    /// </summary>
    public static string SanitizeCss(string css, Uri? baseUri = null) =>
        CssUrls.Rewrite(css,
            (url, _) => IsUnsafe(url, baseUri) ? "data:," : null,
            import => !import.Valid || IsUnsafe(import.Url, baseUri) ? "" : import.Original);

    /// <summary>Stylesheet text can't end its own &lt;style&gt; element (a CSS escape keeps the meaning: "\/" is "/").</summary>
    static string EscapeForStyleElement(string css) => StyleEndTag().Replace(css, @"<\/");

    /// <summary>
    /// Cleans stylesheets and decides what happens to @imports and &lt;link rel="stylesheet"&gt;. Printing inlines local
    /// files (cleaned the same way, their URLs made absolute against their own location, nested imports followed a few
    /// levels deep) and drops everything else; a web page keeps every link or import that isn't itself unsafe.
    /// </summary>
    sealed class StylesheetInliner(bool inline)
    {
        long _inlinedBytes;

        public string CleanCss(string css, Uri? baseUri, bool absolutize, int depth) =>
            CssUrls.Rewrite(css,
                (url, kind) =>
                {
                    if (IsUnsafe(url, baseUri))
                        return "data:,";
                    return absolutize && kind == CssUrls.Kind.Url && baseUri is not null && Resolve(url, baseUri) is var resolved && resolved != url ? resolved : null;
                },
                import => Import(import, baseUri, depth));

        string Import(CssUrls.Import import, Uri? baseUri, int depth)
        {
            if (!import.Valid || IsUnsafe(import.Url, baseUri))
                return "";
            if (!inline)
                return import.Original;
            return LocalTarget(import.Url, baseUri) is { } target && Load(target, depth + 1) is { } css
                ? "\n" + CssUrls.WrapImported(css, import.Conditions) + "\n"
                : "";
        }

        public void ReplaceLink(IHtmlDocument document, IElement link, Uri? documentUri)
        {
            if (!inline)
                return; // its href has been checked like every other URL
            var href = link.GetAttribute("href");
            var alternate = Relations(link).Contains("alternate", StringComparer.OrdinalIgnoreCase);
            if (!alternate && !link.HasAttribute("disabled") && !string.IsNullOrWhiteSpace(href)
                && LocalTarget(href, documentUri) is { } target && Load(target, 0) is { } css)
            {
                var style = document.CreateElement("style");
                if (link.GetAttribute("media") is { } media)
                    style.SetAttribute("media", media);
                style.TextContent = EscapeForStyleElement(css);
                link.Replace(style);
            }
            else
            {
                link.Remove();
            }
        }

        /// <summary>A file: URL on this machine (no host, not UNC), or null.</summary>
        static Uri? LocalTarget(string url, Uri? baseUri)
        {
            var value = HtmlLockdown.Clean(url);
            if (value.Length == 0 || value.StartsWith('#'))
                return null;
            var resolved = baseUri is not null && Uri.TryCreate(baseUri, value, out var combined) ? combined
                : Uri.TryCreate(value, UriKind.Absolute, out var absolute) ? absolute : null;
            return resolved is { IsFile: true } && !IsNetworkFile(resolved) && !resolved.LocalPath.StartsWith(@"\\", StringComparison.Ordinal) ? resolved : null;
        }

        string? Load(Uri sheet, int depth)
        {
            if (depth > MaxImportDepth)
                return null;
            try
            {
                var file = new FileInfo(sheet.LocalPath);
                if (!file.Exists || file.Length > MaxStylesheetBytes || _inlinedBytes + file.Length > MaxInlinedBytes)
                    return null;
                _inlinedBytes += file.Length;
                return CleanCss(TextFiles.Decode(File.ReadAllBytes(file.FullName)), sheet, absolutize: true, depth);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return null;
            }
        }
    }

    /// <summary>BOM, then a declared &lt;meta charset&gt; in the first 4 KB, then UTF-8 / Windows-1252.</summary>
    static string Decode(byte[] bytes)
    {
        var hasBom = bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)
            || (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF));
        if (!hasBom)
        {
            var head = Encoding.Latin1.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
            if (DeclaredCharset().Match(head) is { Success: true } match)
            {
                var name = match.Groups["name"].Value;
                if (!name.StartsWith("utf-8", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("utf8", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        _ = TextFiles.Utf8; // registers the code page provider (Windows-1252, Shift-JIS, GB2312…)
                        return Encoding.GetEncoding(name).GetString(bytes);
                    }
                    catch (ArgumentException)
                    {
                    }
                }
            }
        }
        return TextFiles.Decode(bytes);
    }

    [GeneratedRegex(@"^[a-zA-Z]:?$")]
    private static partial Regex DriveLetter();

    /// <summary>"https:", "data:", "mailto:", "file:"… (two or more letters, so "C:/x" doesn't count as a scheme).</summary>
    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9+.\-]+:")]
    private static partial Regex SchemePrefix();

    /// <summary>A srcset is checked word by word: any word (URL or not) that names a share condemns the attribute.</summary>
    [GeneratedRegex(@"[\s,]+")]
    private static partial Regex SrcsetSeparators();

    [GeneratedRegex(@"</(?=style)", RegexOptions.IgnoreCase)]
    private static partial Regex StyleEndTag();

    [GeneratedRegex(@"<meta[^>]+charset\s*=\s*['""]?(?<name>[A-Za-z0-9_\-:.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex DeclaredCharset();
}
