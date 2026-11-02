using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Clementine.Engines.Documents.Markup;

namespace Clementine.Engines.Documents.Ebook;

/// <summary>
/// Flattens an EPUB into one self-contained HTML page: chapters in spine order (one &lt;section&gt; each), the book's
/// stylesheets inlined, images and fonts embedded as data URIs and links between chapters rewritten to anchors.
/// </summary>
internal static partial class EpubHtml
{
    public static string BuildSingleFile(EpubBook book, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        var parser = new HtmlParser();
        var chapterIds = book.Spine.Select((item, index) => (item.Path, Id: $"chapter-{index + 1}"))
            .GroupBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
        var css = new StringBuilder();
        var seenStylesheets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var body = new StringBuilder();

        for (var index = 0; index < book.Spine.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(new JobProgress(0.1 + 0.4 * index / book.Spine.Count, "Reading chapters…"));
            var item = book.Spine[index];
            if (!book.TryGet(item.Path, out var bytes))
                continue;
            var document = parser.ParseDocument(TextFiles.Decode(bytes));
            var directory = EpubBook.DirectoryOf(item.Path);

            foreach (var link in document.QuerySelectorAll("link[href]").Where(l => (l.GetAttribute("rel") ?? "").Contains("stylesheet", StringComparison.OrdinalIgnoreCase)))
            {
                var path = EpubBook.Resolve(directory, link.GetAttribute("href")!);
                if (seenStylesheets.Add(path) && book.TryGet(path, out var stylesheet))
                    css.Append(EmbedCssUrls(TextFiles.Decode(stylesheet), EpubBook.DirectoryOf(path), book)).Append('\n');
            }
            foreach (var style in document.QuerySelectorAll("head style"))
                css.Append(EmbedCssUrls(style.TextContent, directory, book)).Append('\n');

            if (document.Body is not { } chapter)
                continue;
            RemoveActiveContent(chapter);
            EmbedImages(chapter, directory, book);
            RewriteLinks(chapter, directory, chapterIds);
            body.Append("<section class=\"chapter\" id=\"").Append(chapterIds[item.Path]).Append("\">\n");
            body.Append(chapter.InnerHtml);
            body.Append("\n</section>\n");
        }

        return HtmlPage.Build(book.Title, body.ToString(), extraCss: css.ToString(), language: book.Language);
    }

    /// <summary>Plain text of the whole book: title, author, then each chapter.</summary>
    public static string ExtractText(EpubBook book, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        var parser = new HtmlParser();
        var text = new StringBuilder();
        text.Append(book.Title).Append('\n');
        if (book.Author is not null)
            text.Append(book.Author).Append('\n');
        for (var index = 0; index < book.Spine.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(new JobProgress(0.1 + 0.8 * index / book.Spine.Count, "Reading chapters…"));
            if (!book.TryGet(book.Spine[index].Path, out var bytes))
                continue;
            var document = parser.ParseDocument(TextFiles.Decode(bytes));
            var chapter = HtmlToText.Convert(document.Body ?? (INode)document).Trim();
            if (chapter.Length > 0)
                text.Append("\n\n").Append(chapter);
        }
        return text.ToString().Trim() + "\n";
    }

    /// <summary>Books are documents, not apps: drop scripts, embedded objects and inline event handlers.</summary>
    static void RemoveActiveContent(IElement root)
    {
        foreach (var element in root.QuerySelectorAll("script, iframe, object, embed, form").ToList())
            element.Remove();
        foreach (var element in root.QuerySelectorAll("*"))
        {
            foreach (var handler in element.Attributes.Where(a => a.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase)).Select(a => a.Name).ToList())
                element.RemoveAttribute(handler);
        }
    }

    static void EmbedImages(IElement root, string directory, EpubBook book)
    {
        foreach (var image in root.QuerySelectorAll("img[src]"))
        {
            if (DataUri(book, directory, image.GetAttribute("src")!) is { } uri)
                image.SetAttribute("src", uri);
            image.RemoveAttribute("srcset");
        }
        // SVG <image> (common for covers) uses href or xlink:href.
        foreach (var image in root.QuerySelectorAll("image"))
        {
            foreach (var attribute in image.Attributes.Where(a => a.LocalName == "href").ToList())
            {
                if (DataUri(book, directory, attribute.Value) is { } uri)
                    attribute.Value = uri;
            }
        }
    }

    static void RewriteLinks(IElement root, string directory, IReadOnlyDictionary<string, string> chapterIds)
    {
        foreach (var anchor in root.QuerySelectorAll("a[href]"))
        {
            var href = anchor.GetAttribute("href")!;
            if (href.StartsWith('#') || Uri.TryCreate(href, UriKind.Absolute, out _))
                continue;
            var target = EpubBook.Resolve(directory, href);
            var hash = href.IndexOf('#');
            if (hash >= 0 && hash < href.Length - 1)
                anchor.SetAttribute("href", "#" + href[(hash + 1)..]);
            else if (chapterIds.TryGetValue(target, out var id))
                anchor.SetAttribute("href", "#" + id);
        }
    }

    static string EmbedCssUrls(string css, string directory, EpubBook book) =>
        CssUrl().Replace(css, match =>
        {
            var url = match.Groups["url"].Value.Trim();
            return url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || Uri.TryCreate(url, UriKind.Absolute, out _)
                ? match.Value
                : DataUri(book, directory, url) is { } uri ? $"url(\"{uri}\")" : match.Value;
        });

    static string? DataUri(EpubBook book, string directory, string href)
    {
        if (href.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || Uri.TryCreate(href, UriKind.Absolute, out _))
            return null;
        var path = EpubBook.Resolve(directory, WebUtility.HtmlDecode(href));
        if (!book.TryGet(path, out var data))
            return null;
        return $"data:{book.MediaTypeOf(path)};base64,{Convert.ToBase64String(data)}";
    }

    [GeneratedRegex("""url\(\s*['"]?(?<url>[^'")]+)['"]?\s*\)""", RegexOptions.IgnoreCase)]
    private static partial Regex CssUrl();
}
