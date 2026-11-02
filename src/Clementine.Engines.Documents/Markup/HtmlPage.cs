using System.Globalization;
using System.Net;
using System.Text;

namespace Clementine.Engines.Documents.Markup;

/// <summary>The self-contained HTML page Clementine wraps converted content in (Markdown, e-books): clean, readable CSS.</summary>
internal static class HtmlPage
{
    /// <summary>Paper size for printing: A4 in metric regions, Letter elsewhere.</summary>
    public static string PaperSize => RegionInfo.CurrentRegion.IsMetric ? "A4" : "letter";

    public static string Build(string title, string bodyHtml, string? baseHref = null, string? extraCss = null, string? language = null)
    {
        var html = new StringBuilder(bodyHtml.Length + 6000);
        html.Append("<!DOCTYPE html>\n<html");
        if (!string.IsNullOrWhiteSpace(language))
            html.Append(" lang=\"").Append(WebUtility.HtmlEncode(language)).Append('"');
        html.Append(">\n<head>\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        html.Append("<meta name=\"generator\" content=\"Clementine\">\n");
        if (baseHref is not null)
            html.Append("<base href=\"").Append(WebUtility.HtmlEncode(baseHref)).Append("\">\n");
        html.Append("<title>").Append(WebUtility.HtmlEncode(title)).Append("</title>\n<style>\n");
        html.Append(Css.Replace("@PAPER@", PaperSize, StringComparison.Ordinal));
        html.Append("</style>\n");
        if (!string.IsNullOrWhiteSpace(extraCss))
            html.Append("<style>\n").Append(extraCss).Append("\n</style>\n");
        html.Append("</head>\n<body>\n<main class=\"clementine-doc\">\n");
        html.Append(bodyHtml);
        html.Append("\n</main>\n</body>\n</html>\n");
        return html.ToString();
    }

    /// <summary>"file:///C:/folder/" for resolving a document's relative images and links.</summary>
    public static string DirectoryUri(string directory) =>
        FileUris.For(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar);

    const string Css = """
        :root { color-scheme: light dark; --text: #1f2328; --muted: #59636e; --border: #d1d9e0; --soft: #f6f8fa; --link: #0969da; --bg: #ffffff; }
        @media screen and (prefers-color-scheme: dark) {
          :root { --text: #e6edf3; --muted: #9198a1; --border: #3d444d; --soft: #151b23; --link: #4493f8; --bg: #0d1117; }
        }
        html { background: var(--bg); }
        body { margin: 0; color: var(--text); background: var(--bg);
          font: 16px/1.65 "Segoe UI Variable Text", "Segoe UI", system-ui, -apple-system, "Helvetica Neue", Arial, "Noto Sans", "Yu Gothic UI", "Microsoft YaHei UI", "Malgun Gothic", sans-serif;
          -webkit-text-size-adjust: 100%; }
        main.clementine-doc { max-width: 46rem; margin: 0 auto; padding: 3rem 1.5rem 5rem; overflow-wrap: break-word; }
        h1, h2, h3, h4, h5, h6 { line-height: 1.25; margin: 1.6em 0 0.6em; font-weight: 650; }
        h1 { font-size: 2em; padding-bottom: 0.3em; border-bottom: 1px solid var(--border); }
        h2 { font-size: 1.5em; padding-bottom: 0.3em; border-bottom: 1px solid var(--border); }
        h3 { font-size: 1.25em; } h4 { font-size: 1em; } h5 { font-size: 0.875em; } h6 { font-size: 0.85em; color: var(--muted); }
        main > :first-child, section.chapter > :first-child { margin-top: 0; }
        p, ul, ol, dl, table, pre, blockquote, figure { margin: 0 0 1em; }
        ul, ol { padding-left: 2em; } li + li { margin-top: 0.25em; }
        a { color: var(--link); text-decoration: none; } a:hover { text-decoration: underline; }
        code, kbd, pre, samp { font-family: "Cascadia Code", "Cascadia Mono", Consolas, "SF Mono", Menlo, monospace; font-size: 0.9em; }
        :not(pre) > code { background: var(--soft); padding: 0.15em 0.4em; border-radius: 6px; }
        pre { background: var(--soft); padding: 0.9em 1em; border-radius: 8px; overflow: auto; line-height: 1.45; }
        pre code { background: none; padding: 0; font-size: 1em; }
        blockquote { padding: 0 1em; color: var(--muted); border-left: 4px solid var(--border); }
        table { border-collapse: collapse; display: block; max-width: 100%; overflow: auto; }
        th, td { border: 1px solid var(--border); padding: 6px 13px; }
        th { background: var(--soft); font-weight: 600; }
        img, svg, video { max-width: 100%; height: auto; }
        hr { border: 0; border-top: 1px solid var(--border); margin: 2em 0; }
        ul.contains-task-list { list-style: none; padding-left: 1.2em; }
        .task-list-item input { margin: 0 0.4em 0 -1.2em; }
        section.chapter + section.chapter { margin-top: 3em; }
        @page { size: @PAPER@; margin: 18mm 16mm; }
        @media print {
          :root { --link: #1f2328; }
          body { font-size: 11pt; }
          main.clementine-doc { max-width: none; padding: 0; }
          pre, blockquote, table, img, figure { break-inside: avoid; }
          h1, h2, h3, h4 { break-after: avoid; }
          section.chapter + section.chapter { break-before: page; margin-top: 0; }
        }

        """;
}
