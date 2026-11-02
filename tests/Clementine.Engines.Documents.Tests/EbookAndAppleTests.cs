using System.IO.Compression;
using Clementine.Core;
using static Clementine.Engines.Documents.Tests.Fixtures.DocumentFixtures;

namespace Clementine.Engines.Documents.Tests;

public sealed class EbookAndAppleTests() : DocumentTestBase("ebook")
{
    [Fact]
    public async Task Epub_to_text_follows_the_spine()
    {
        var input = Epub(Workspace.Path("tale.epub"));
        var text = await File.ReadAllTextAsync(Single(await RunAsync("epub.txt", input)));

        Assert.StartsWith("The Clementine Tale\r\nAda Writer\r\n", text);
        var one = text.IndexOf("Chapter One", StringComparison.Ordinal);
        var two = text.IndexOf("Chapter Two", StringComparison.Ordinal);
        Assert.True(one > 0 && two > one, "Chapters must follow the spine order, not file names.");
        Assert.Contains("It began in Tōkyō – 東京.", text);
        Assert.Contains("[A red square]", text);
    }

    [Fact]
    public async Task Epub_to_html_is_one_self_contained_file()
    {
        var input = Epub(Workspace.Path("tale.epub"));
        var html = await File.ReadAllTextAsync(Single(await RunAsync("epub.html", input)));

        Assert.Contains("<title>The Clementine Tale</title>", html);
        Assert.Contains("lang=\"en\"", html);
        Assert.Contains("<section class=\"chapter\" id=\"chapter-1\">", html);
        Assert.Contains("src=\"data:image/png;base64,", html);
        Assert.Contains("href=\"#later\"", html);
        Assert.Contains("color: #b34700", html); // the book's own stylesheet
        Assert.DoesNotContain("../images/pic.png", html);
        Assert.True(html.IndexOf("Chapter One", StringComparison.Ordinal) < html.IndexOf("Chapter Two", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Drm_protected_epub_is_refused_politely()
    {
        var input = Epub(Workspace.Path("drm.epub"));
        using (var zip = ZipFile.Open(input, ZipArchiveMode.Update))
        {
            using var writer = new StreamWriter(zip.CreateEntry("META-INF/encryption.xml").Open());
            writer.Write("""
                <encryption xmlns="urn:oasis:names:tc:opendocument:xmlns:container" xmlns:enc="http://www.w3.org/2001/04/xmlenc#">
                  <enc:EncryptedData><enc:EncryptionMethod Algorithm="http://www.w3.org/2001/04/xmlenc#aes128-cbc"/>
                  <enc:CipherData><enc:CipherReference URI="OEBPS/text/a%20first.xhtml"/></enc:CipherData></enc:EncryptedData>
                </encryption>
                """);
        }
        var error = await Assert.ThrowsAsync<OperationException>(() => RunAsync("epub.txt", input));
        Assert.Contains("DRM-protected", error.Message);
    }

    /// <summary>
    /// 4 MB of zeros whose zip headers (local and central directory) claim 100 bytes. Deflated, .NET stops inflating at
    /// the declared size; stored, it hands over all 4 MB — so only counting the bytes really read keeps the limit.
    /// </summary>
    [Theory]
    [InlineData(CompressionLevel.NoCompression)]
    [InlineData(CompressionLevel.SmallestSize)]
    public void Epub_size_limit_counts_the_bytes_really_read(CompressionLevel level)
    {
        var input = Epub(Workspace.Path("bomb.epub"));
        using (var zip = ZipFile.Open(input, ZipArchiveMode.Update))
        {
            using var stream = zip.CreateEntry("OEBPS/filler.bin", level).Open();
            stream.Write(new byte[4 * 1024 * 1024]);
        }
        LieAboutUncompressedSize(input, "OEBPS/filler.bin", 100);
        using (var zip = ZipFile.OpenRead(input))
            Assert.True(zip.Entries.Sum(e => e.Length) < 64 * 1024, "the declared sizes should look harmless");

        var limit = Ebook.EpubBook.MaxBookBytes;
        Ebook.EpubBook.MaxBookBytes = 1024 * 1024;
        try
        {
            if (level == CompressionLevel.NoCompression)
            {
                var error = Assert.Throws<OperationException>(() => Ebook.EpubBook.Open(input, CancellationToken.None));
                Assert.Equal("This e-book is too large to convert.", error.Message);
            }
            else
            {
                var book = Ebook.EpubBook.Open(input, CancellationToken.None);
                Assert.True(book.TryGet("OEBPS/filler.bin", out var data));
                Assert.True(data.Length <= 100, $"{data.Length} bytes inflated");
            }
        }
        finally
        {
            Ebook.EpubBook.MaxBookBytes = limit;
        }
    }

    static void LieAboutUncompressedSize(string zipPath, string entryName, uint size)
    {
        var bytes = File.ReadAllBytes(zipPath);
        var name = System.Text.Encoding.UTF8.GetBytes(entryName);
        bool NameAt(int at, int length) => length == name.Length && at + length <= bytes.Length && bytes.AsSpan(at, length).SequenceEqual(name);
        var patched = 0;
        for (var i = 0; i + 46 <= bytes.Length; i++)
        {
            var signature = BitConverter.ToUInt32(bytes, i);
            if (signature == 0x04034b50 && NameAt(i + 30, BitConverter.ToUInt16(bytes, i + 26)))
                patched += BitConverter.TryWriteBytes(bytes.AsSpan(i + 22, 4), size) ? 1 : 0; // local header
            else if (signature == 0x02014b50 && NameAt(i + 46, BitConverter.ToUInt16(bytes, i + 28)))
                patched += BitConverter.TryWriteBytes(bytes.AsSpan(i + 24, 4), size) ? 1 : 0; // central directory
        }
        Assert.Equal(2, patched);
        File.WriteAllBytes(zipPath, bytes);
    }

    [Fact]
    public async Task Not_an_epub_gets_a_friendly_error()
    {
        var input = Workspace.WriteText("fake.epub", "plain text, not a zip");
        var error = await Assert.ThrowsAsync<OperationException>(() => RunAsync("epub.txt", input));
        Assert.Contains("isn't a valid EPUB", error.Message);
    }

    [Fact]
    public async Task Pages_preview_jpeg_becomes_pdf_and_jpeg()
    {
        var preview = PhotoJpeg(800, 1131, 85);
        var input = IWork(Workspace.Path("Letter.pages"), previewJpeg: preview);

        var pdfResult = await RunAsync("apple.pdf", input);
        var pdf = Single(pdfResult);
        Assert.Equal("Letter.pdf", Path.GetFileName(pdf));
        Assert.Equal(1, PageCount(pdf));
        Assert.Contains(pdfResult.Warnings, w => w.Contains("preview"));

        var jpeg = Single(await RunAsync("apple.jpeg", input));
        Assert.Equal(preview, await File.ReadAllBytesAsync(jpeg)); // the largest preview, untouched
    }

    [Fact]
    public async Task Keynote_quicklook_pdf_is_used_as_is()
    {
        var previewPdf = await File.ReadAllBytesAsync(TextPdf(Workspace.Path("source.pdf"), "slide one", "slide two"));
        var input = IWork(Workspace.Path("Talk.key"), previewPdf: previewPdf);

        var pdf = Single(await RunAsync("apple.pdf", input));
        Assert.Equal(previewPdf, await File.ReadAllBytesAsync(pdf));
        var jpeg = Single(await RunAsync("apple.jpeg", input));
        using var bitmap = Decode(jpeg);
        Assert.InRange(bitmap.Width, 1236, 1244); // first page at 150 DPI
    }

    [Fact]
    public async Task Numbers_package_folder_is_accepted()
    {
        var package = Workspace.Path("Budget.numbers/preview.jpg");
        await File.WriteAllBytesAsync(package, PhotoJpeg(300, 200, 80));
        var folder = Path.GetDirectoryName(package)!;

        Assert.True(Op("apple.pdf").Accepts(InputFile.FromPath(folder)));
        var pdf = Single(await RunAsync("apple.pdf", folder));
        Assert.Equal("Budget.pdf", Path.GetFileName(pdf));
    }

    [Fact]
    public async Task Iwork_file_without_preview_explains_what_to_do()
    {
        var input = IWork(Workspace.Path("Empty.numbers"));
        var error = await Assert.ThrowsAsync<OperationException>(() => RunAsync("apple.pdf", input));
        Assert.Contains("Export it as PDF from Numbers", error.Message);
    }
}
