using System.IO.Compression;
using System.Text;
using Clementine.Core;
using Clementine.Engines.Documents.Tests.Fixtures;
using static Clementine.Engines.Documents.Tests.Fixtures.DocumentFixtures;

namespace Clementine.Engines.Documents.Tests;

/// <summary>
/// Pages printed with Edge come from untrusted places (saved web pages, downloaded e-books, Markdown with raw HTML): printing
/// must not run their scripts or fetch anything from the network, and must clean up after itself.
/// </summary>
[Collection(ExternalToolsCollection.Name)]
public sealed class EdgeHardeningTests() : DocumentTestBase("edge-hardening")
{
    static string AllText(string pdf) => Normalize(string.Join(' ', PageTexts(pdf)));

    static string HostilePage(int port) => $$"""
        <!doctype html><meta charset=utf-8><title>Hostile</title>
        <link rel="stylesheet" href="http://127.0.0.1:{{port}}/style.css">
        <link rel="prefetch" href="http://127.0.0.1:{{port}}/prefetch">
        <style>@import url("http://localhost:{{port}}/import.css"); body { background-image: url("http://127.0.0.1:{{port}}/bg.png"); }</style>
        <h1>Hostile page</h1>
        <p id="state">Scripts stayed off</p>
        <img src="http://127.0.0.1:{{port}}/pixel.png" alt="">
        <img src="http://localhost:{{port}}/pixel2.png" alt="">
        <iframe src="http://127.0.0.1:{{port}}/frame"></iframe>
        <script>document.getElementById('state').textContent = 'Script ran'; fetch('http://127.0.0.1:{{port}}/fetch');</script>
        <img src="x" onerror="document.title = 'handler ran'; document.body.append('Handler ran')">
        """;

    [EdgeFact]
    public async Task Html_printing_runs_no_scripts_and_fetches_nothing()
    {
        using var server = new ConnectionRecorder();
        var input = Workspace.WriteText("hostile.html", HostilePage(server.Port));
        var pdf = Single(await RunAsync("html.pdf", input));

        var text = AllText(pdf);
        Assert.Contains("Hostile page", text);
        Assert.Contains("Scripts stayed off", text);
        Assert.DoesNotContain("Script ran", text);
        Assert.DoesNotContain("Handler ran", text);
        Assert.Equal(0, server.Connections);
    }

    [EdgeFact]
    public async Task Markdown_with_raw_html_runs_no_scripts_and_fetches_nothing()
    {
        using var server = new ConnectionRecorder();
        var input = Workspace.WriteText("notes.md", $"""
            # Notes

            <p id="state">Scripts stayed off</p>

            <script>document.getElementById('state').textContent = 'Script ran'; fetch('http://127.0.0.1:{server.Port}/fetch');</script>

            ![remote](http://127.0.0.1:{server.Port}/remote.png)
            """);
        var pdf = Single(await RunAsync("md.pdf", input));

        var text = AllText(pdf);
        Assert.Contains("Scripts stayed off", text);
        Assert.DoesNotContain("Script ran", text);
        Assert.Equal(0, server.Connections);
    }

    [EdgeFact]
    public async Task Epub_printing_fetches_nothing_remote()
    {
        using var server = new ConnectionRecorder();
        var input = RemoteImageEpub(Workspace.Path("remote.epub"), server.Port);
        var pdf = Single(await RunAsync("epub.pdf", input));

        Assert.Contains("Chapter with remote things", AllText(pdf));
        Assert.Equal(0, server.Connections);
    }

    [EdgeFact]
    public async Task Pages_with_hash_and_percent_signs_in_their_path_print_with_their_images()
    {
        const string folder = "Set #2 (100%)";
        File.WriteAllBytes(Workspace.Path($"{folder}/dot #1.png"), Png(40, 40, SkiaSharp.SKColors.Orange));
        var input = Workspace.WriteText($"{folder}/Track #1 50%.html",
            "<!doctype html><meta charset=utf-8><h1>Hash page</h1><img src=\"dot%20%231.png\" width=300 height=300>");
        var pdf = Single(await RunAsync("html.pdf", input));

        Assert.Equal("Track #1 50%.pdf", Path.GetFileName(pdf));
        Assert.Contains("Hash page", AllText(pdf));
        using var document = UglyToad.PdfPig.PdfDocument.Open(pdf);
        Assert.NotEmpty(document.GetPage(1).GetImages());
    }

    [EdgeFact]
    public async Task Edge_print_cleans_up_its_profile_and_processes()
    {
        var input = Workspace.WriteText("plain.html", "<!doctype html><meta charset=utf-8><h1>Tidy</h1>");
        Single(await RunAsync("html.pdf", input));

        Assert.Empty(await ToolProcesses.WaitGoneAsync("msedge.exe", Workspace.Root, TimeSpan.FromSeconds(5)));
        Assert.Empty(Directory.GetDirectories(Workspace.Root, "edge-*", SearchOption.AllDirectories));
    }

    [EdgeFact]
    public async Task Cancelling_an_edge_print_kills_edge_and_removes_its_profile()
    {
        // Big enough that Edge needs several seconds to lay it out.
        var body = new StringBuilder("<!doctype html><meta charset=utf-8><title>Big</title>");
        for (var i = 0; i < 60_000; i++)
            body.Append("<p>Paragraph ").Append(i).Append(" with <b>some</b> <i>styled</i> text to lay out and print.</p>\n");
        var input = Workspace.WriteText("big.html", body.ToString());

        using var cancel = new CancellationTokenSource();
        var run = RunAsync("html.pdf", input, cancellationToken: cancel.Token);
        // Cancel once Edge is actually running.
        for (var i = 0; i < 100 && ToolProcesses.Find("msedge.exe", Workspace.Root).Count == 0 && !run.IsCompleted; i++)
            await Task.Delay(100);
        await Task.Delay(500);
        cancel.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            Assert.Empty(await ToolProcesses.WaitGoneAsync("msedge.exe", Workspace.Root, TimeSpan.FromSeconds(5)));
            Assert.Empty(Directory.GetDirectories(Workspace.Root, "edge-*", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetFiles(Workspace.Root, "*.pdf"));
            Assert.Empty(Directory.GetFiles(Workspace.Root, ".*partial*"));
        }
        finally
        {
            ToolProcesses.KillAll("msedge.exe", Workspace.Root);
        }
    }

    [EdgeFact]
    public async Task Several_edge_prints_can_run_at_once()
    {
        var inputs = Enumerable.Range(1, 3)
            .Select(i => Workspace.WriteText($"page {i}.html", $"<!doctype html><meta charset=utf-8><h1>Parallel {i}</h1>"))
            .ToList();
        var results = await Task.WhenAll(inputs.Select(i => RunAsync("html.pdf", i)));
        for (var i = 0; i < results.Length; i++)
            Assert.Contains($"Parallel {i + 1}", AllText(Single(results[i])));
    }

    /// <summary>An EPUB whose chapter points at remote images and fonts (some books do, for tracking or by mistake).</summary>
    static string RemoteImageEpub(string path, int port)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Add(zip, "mimetype", "application/epub+zip");
        Add(zip, "META-INF/container.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
              <rootfiles><rootfile full-path="content.opf" media-type="application/oebps-package+xml"/></rootfiles>
            </container>
            """);
        Add(zip, "content.opf", """
            <?xml version="1.0" encoding="UTF-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">x</dc:identifier><dc:title>Remote</dc:title></metadata>
              <manifest><item id="c1" href="c1.xhtml" media-type="application/xhtml+xml"/><item id="css" href="book.css" media-type="text/css"/></manifest>
              <spine><itemref idref="c1"/></spine>
            </package>
            """);
        Add(zip, "book.css", $"@font-face {{ font-family: Remote; src: url(\"http://127.0.0.1:{port}/font.woff\"); }} h1 {{ font-family: Remote; background: url(http://127.0.0.1:{port}/bg.png); }}");
        Add(zip, "c1.xhtml", $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml"><head><title>One</title><link rel="stylesheet" href="book.css"/></head>
            <body><h1>Chapter with remote things</h1><p><img src="http://127.0.0.1:{port}/tracker.png" alt=""/></p>
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink"><image xlink:href="http://localhost:{port}/svg.png"/></svg></body></html>
            """);
        return path;
    }

    static void Add(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}

/// <summary>LibreOffice/Edge tests share static timeouts and one private LibreOffice profile, so they never run in parallel.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ExternalToolsCollection
{
    public const string Name = "External tools";
}
