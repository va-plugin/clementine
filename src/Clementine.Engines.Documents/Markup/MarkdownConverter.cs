using System.Text;
using Clementine.Engines.Documents.OpenXml;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using SkiaSharp;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableCell = Markdig.Extensions.Tables.TableCell;
using MdTableRow = Markdig.Extensions.Tables.TableRow;

namespace Clementine.Engines.Documents.Markup;

/// <summary>Markdown → HTML page (Markdig, GitHub-style extensions) and Markdown → Word (Markdig AST → OpenXML).</summary>
internal static class MarkdownConverter
{
    static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    public static MarkdownDocument Parse(string markdown) => Markdown.Parse(markdown, Pipeline);

    /// <summary>
    /// A complete, self-contained HTML page. <paramref name="baseDirectory"/> resolves relative images when the page lives
    /// elsewhere. Markdown may carry raw HTML, so the page is cleaned like a printed one (<see cref="HtmlLockdown.ForWebPage"/>):
    /// no scripts, frames, plugins, event handlers, javascript: URLs or network-share links — ordinary markup and links stay.
    /// </summary>
    public static string ToHtmlPage(string markdown, string fallbackTitle, string? baseDirectory = null)
    {
        var document = Parse(markdown);
        var body = document.ToHtml(Pipeline);
        var page = HtmlPage.Build(TitleOf(document, fallbackTitle), body, baseDirectory is null ? null : HtmlPage.DirectoryUri(baseDirectory));
        return HtmlLockdown.ForWebPage(page, baseDirectory);
    }

    /// <summary>The text of the first heading, else <paramref name="fallback"/>.</summary>
    public static string TitleOf(MarkdownDocument document, string fallback)
    {
        var heading = document.Descendants<HeadingBlock>().FirstOrDefault();
        var text = heading?.Inline is null ? null : PlainText(heading.Inline).Trim();
        return string.IsNullOrEmpty(text) ? fallback : text;
    }

    /// <summary>Writes the Markdown as a styled Word document. Local images are embedded; remote ones are never downloaded.</summary>
    public static IReadOnlyList<string> WriteDocx(string markdown, string outputPath, string fallbackTitle, string sourceDirectory, CancellationToken cancellationToken)
    {
        var document = Parse(markdown);
        var writer = new DocxWriter(sourceDirectory, cancellationToken);
        using var docx = DocxBuilder.Create(outputPath, TitleOf(document, fallbackTitle));
        writer.WriteBlocks(docx, document, listLevel: 0);
        docx.Save();
        return writer.Warnings;
    }

    static string PlainText(ContainerInline inline)
    {
        var text = new StringBuilder();
        foreach (var child in inline.Descendants())
        {
            switch (child)
            {
                case LiteralInline literal:
                    text.Append(literal.Content.ToString());
                    break;
                case CodeInline code:
                    text.Append(code.Content);
                    break;
                case LineBreakInline:
                    text.Append(' ');
                    break;
            }
        }
        return text.ToString();
    }

    sealed class DocxWriter(string sourceDirectory, CancellationToken cancellationToken)
    {
        readonly List<string> _warnings = [];
        readonly string _folder = Path.EndsInDirectorySeparator(Path.GetFullPath(sourceDirectory))
            ? Path.GetFullPath(sourceDirectory)
            : Path.GetFullPath(sourceDirectory) + Path.DirectorySeparatorChar;
        bool _remoteImageWarned;
        bool _outsideImageWarned;

        public IReadOnlyList<string> Warnings => _warnings;

        public void WriteBlocks(DocxBuilder docx, ContainerBlock container, int listLevel, int? listId = null)
        {
            foreach (var block in container)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WriteBlock(docx, block, listLevel, listId);
            }
        }

        void WriteBlock(DocxBuilder docx, Block block, int listLevel, int? listId)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    docx.Heading(heading.Level, Inlines(heading.Inline));
                    break;
                case ParagraphBlock paragraph:
                    var inlines = Inlines(paragraph.Inline);
                    if (listId is { } id)
                        docx.ListItem(id, listLevel, inlines);
                    else if (inlines.Any(i => i.Text.Trim().Length > 0))
                        docx.Paragraph(inlines);
                    EmbedImages(docx, paragraph.Inline);
                    break;
                case ListBlock list:
                    var start = int.TryParse(list.OrderedStart, out var s) ? s : 1;
                    var numId = docx.NewList(list.IsOrdered, start);
                    var level = listId is null ? 0 : listLevel + 1;
                    foreach (var item in list.OfType<ListItemBlock>())
                    {
                        var first = true;
                        foreach (var child in item)
                        {
                            if (first && child is ParagraphBlock p)
                            {
                                docx.ListItem(numId, level, Inlines(p.Inline));
                                EmbedImages(docx, p.Inline);
                            }
                            else
                            {
                                WriteBlock(docx, child, level, child is ListBlock ? numId : null);
                            }
                            first = false;
                        }
                    }
                    break;
                case QuoteBlock quote:
                    foreach (var child in quote)
                    {
                        if (child is ParagraphBlock p)
                            docx.Quote(Inlines(p.Inline));
                        else
                            WriteBlock(docx, child, listLevel, null);
                    }
                    break;
                case CodeBlock code:
                    docx.CodeBlock(code.Lines.ToString());
                    break;
                case ThematicBreakBlock:
                    docx.HorizontalRule();
                    break;
                case MdTable table:
                    WriteTable(docx, table);
                    break;
                case HtmlBlock html:
                    var text = HtmlToText.Convert(html.Lines.ToString()).Trim();
                    if (text.Length > 0)
                        docx.Paragraph([RichRun.Plain(text)]);
                    break;
                case ContainerBlock other:
                    WriteBlocks(docx, other, listLevel, listId);
                    break;
            }
        }

        void WriteTable(DocxBuilder docx, MdTable table)
        {
            var rows = new List<IReadOnlyList<IReadOnlyList<RichRun>>>();
            var header = false;
            foreach (var row in table.OfType<MdTableRow>())
            {
                header |= row.IsHeader && rows.Count == 0;
                var cells = new List<IReadOnlyList<RichRun>>();
                foreach (var cell in row.OfType<MdTableCell>())
                {
                    var content = new List<RichRun>();
                    foreach (var paragraph in cell.OfType<ParagraphBlock>())
                        content.AddRange(Inlines(paragraph.Inline));
                    cells.Add(content);
                }
                rows.Add(cells);
            }
            docx.Table(rows, header);
        }

        List<RichRun> Inlines(ContainerInline? container)
        {
            var result = new List<RichRun>();
            if (container is not null)
                Collect(container, RunFormat.None, null, result);
            return result;
        }

        static void Collect(ContainerInline container, RunFormat format, string? link, List<RichRun> output)
        {
            foreach (var inline in container)
            {
                switch (inline)
                {
                    case LiteralInline literal:
                        output.Add(new RichRun(literal.Content.ToString(), format, link));
                        break;
                    case CodeInline code:
                        output.Add(new RichRun(code.Content, format | RunFormat.Code, link));
                        break;
                    case EmphasisInline emphasis:
                        var added = emphasis.DelimiterChar == '~'
                            ? RunFormat.Strike
                            : emphasis.DelimiterCount >= 2 ? RunFormat.Bold : RunFormat.Italic;
                        if (emphasis.DelimiterCount >= 3)
                            added |= RunFormat.Italic;
                        Collect(emphasis, format | added, link, output);
                        break;
                    case LinkInline { IsImage: true }:
                        break; // embedded separately
                    case LinkInline linkInline:
                        Collect(linkInline, format, linkInline.Url, output);
                        break;
                    case AutolinkInline auto:
                        output.Add(new RichRun(auto.Url, format, auto.IsEmail ? "mailto:" + auto.Url : auto.Url));
                        break;
                    case LineBreakInline lineBreak:
                        output.Add(lineBreak.IsHard ? RichRun.Break : new RichRun(" ", format, link));
                        break;
                    case HtmlEntityInline entity:
                        output.Add(new RichRun(entity.Transcoded.ToString(), format, link));
                        break;
                    case TaskList task:
                        output.Add(new RichRun(task.Checked ? "☑ " : "☐ ", format));
                        break;
                    case HtmlInline:
                        break;
                    case ContainerInline nested:
                        Collect(nested, format, link, output);
                        break;
                }
            }
        }

        void EmbedImages(DocxBuilder docx, ContainerInline? container)
        {
            if (container is null)
                return;
            foreach (var image in container.Descendants<LinkInline>().Where(l => l.IsImage))
            {
                var alt = PlainText(image);
                if (TryLoadImage(image.Url, out var data, out var type, out var width, out var height))
                    docx.Image(data, type, width, height, alt);
                else if (alt.Length > 0)
                    docx.Paragraph([new RichRun($"[{alt}]", RunFormat.Italic)]);
            }
        }

        bool TryLoadImage(string? url, out byte[] data, out string contentType, out int width, out int height)
        {
            data = [];
            contentType = "image/png";
            width = height = 0;
            if (string.IsNullOrWhiteSpace(url))
                return false;
            url = url.Trim();
            // "//host/share/x.png", "\\host\share\x.png", "\\?\C:\x.png": never handed to Uri or the file system.
            var share = url.Replace('\\', '/').StartsWith("//", StringComparison.Ordinal);
            Uri? absolute = null;
            if (!share && Uri.TryCreate(url, UriKind.Absolute, out absolute) && !absolute.IsFile)
            {
                if (!_remoteImageWarned)
                    _warnings.Add("Images on the web weren't downloaded — only local images are embedded.");
                _remoteImageWarned = true;
                return false;
            }
            try
            {
                if ((share ? null : ImagePath(url, absolute)) is not { } path)
                {
                    if (!_outsideImageWarned)
                        _warnings.Add("Images outside the Markdown file's folder (or on network shares) weren't embedded.");
                    _outsideImageWarned = true;
                    return false;
                }
                if (!File.Exists(path))
                    return false;
                data = File.ReadAllBytes(path);
                using var codec = SKCodec.Create(new MemoryStream(data));
                if (codec is null)
                    return false;
                width = codec.Info.Width;
                height = codec.Info.Height;
                contentType = codec.EncodedFormat switch
                {
                    SKEncodedImageFormat.Jpeg => "image/jpeg",
                    SKEncodedImageFormat.Png => "image/png",
                    SKEncodedImageFormat.Gif => "image/gif",
                    SKEncodedImageFormat.Bmp => "image/bmp",
                    _ => "",
                };
                if (contentType.Length == 0)
                {
                    // WebP and friends: Word can't show them, so re-encode as PNG.
                    using var bitmap = SKBitmap.Decode(data);
                    if (bitmap is null)
                        return false;
                    data = Pdf.PdfRenderer.Encode(bitmap, SKEncodedImageFormat.Png, 100);
                    contentType = "image/png";
                }
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return false;
            }
        }

        /// <summary>
        /// The local file an image link names, or null when it would leave the Markdown file's folder: UNC and device
        /// paths ("//host/share/x.png", "file://host/x.png", "\\?\…"), other drives and folders ("C:/Windows/x.png",
        /// "../x.png"), and symbolic links or junctions that lead out of it. A Markdown file is often downloaded; it
        /// mustn't be able to make Windows contact a server, or copy the user's other pictures into the document.
        /// </summary>
        string? ImagePath(string url, Uri? absolute)
        {
            string reference;
            if (absolute is { IsFile: true })
            {
                if (absolute.IsUnc || absolute.Host.Length > 0)
                    return null;
                reference = absolute.LocalPath;
            }
            else
            {
                reference = Uri.UnescapeDataString(url.Split('?', '#')[0]);
            }
            reference = reference.Replace('/', '\\');
            if (reference.StartsWith(@"\\", StringComparison.Ordinal))
                return null;
            var full = Path.GetFullPath(Path.Combine(_folder, reference));
            return StaysInside(full, depth: 0) ? full : null;
        }

        /// <summary>
        /// True when <paramref name="full"/> is at or below the folder, link by link: each step is checked from the
        /// folder down, before anything below it is opened, so a link to a share is never followed.
        /// </summary>
        bool StaysInside(string full, int depth)
        {
            if (depth > 8 || !full.StartsWith(_folder, StringComparison.OrdinalIgnoreCase) || full.Length == _folder.Length)
                return false;
            var parts = full[_folder.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            var current = _folder;
            for (var i = 0; i < parts.Length; i++)
            {
                current = Path.Join(current, parts[i]);
                FileSystemInfo step = i == parts.Length - 1 ? new FileInfo(current) : new DirectoryInfo(current);
                if (step.LinkTarget is not { } target)
                    continue;
                var resolved = Path.GetFullPath(target, Path.GetDirectoryName(current)!);
                var rest = string.Join(Path.DirectorySeparatorChar, parts[(i + 1)..]);
                return !resolved.StartsWith(@"\\", StringComparison.Ordinal) && StaysInside(rest.Length == 0 ? resolved : Path.Join(resolved, rest), depth + 1);
            }
            return true;
        }
    }
}
