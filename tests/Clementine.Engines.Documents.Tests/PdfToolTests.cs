using Clementine.Core;
using UglyToad.PdfPig;
using static Clementine.Engines.Documents.Tests.Fixtures.DocumentFixtures;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace Clementine.Engines.Documents.Tests;

public sealed class PdfToolTests() : DocumentTestBase("pdftools")
{
    static List<string> Headings(string pdf) => PageTexts(pdf).Select(t => string.Join(' ', t.Split(' ').Take(2))).ToList();

    [Fact]
    public async Task Merge_joins_in_natural_name_order_with_bookmarks()
    {
        var ten = TextPdf(Workspace.Path("part 10.pdf"), "ten-a", "ten-b");
        var two = TextPdf(Workspace.Path("part 2.pdf"), "two-a");
        var one = TextPdf(Workspace.Path("part 1.pdf"), "one-a", "one-b");
        var output = Single(await RunAsync("pdf.merge", [ten, two, one]));

        Assert.Equal("part 1 (merged).pdf", Path.GetFileName(output));
        var texts = PageTexts(output);
        Assert.Equal(5, texts.Count);
        Assert.Equal(new[] { "one-a", "one-b", "two-a", "ten-a", "ten-b" }, texts.Select(t => t.Split(' ').Last()).ToList());
        using var document = PigDocument.Open(output);
        Assert.True(document.TryGetBookmarks(out var bookmarks));
        Assert.Equal(new[] { "part 1", "part 2", "part 10" }, bookmarks.Roots.Select(b => b.Title).ToList());
    }

    [Fact]
    public void Merge_needs_at_least_two_pdfs()
    {
        var one = TextPdf(Workspace.Path("solo.pdf"), "x");
        Assert.False(Op("pdf.merge").AcceptsAll([InputFile.FromPath(one)]));
    }

    [Fact]
    public async Task Split_every_page_makes_a_folder_of_single_pages()
    {
        var input = TextPdf(Workspace.Path("book.pdf"), "a", "b", "c");
        var folder = Single(await RunAsync("pdf.split.pages", input));

        Assert.Equal("book (pages)", Path.GetFileName(folder));
        var files = Directory.GetFiles(folder).Order().ToList();
        Assert.Equal(3, files.Count);
        Assert.All(files, f => Assert.Equal(1, PageCount(f)));
        Assert.Equal("Page 2 b", PageTexts(files[1])[0]);
    }

    [Fact]
    public async Task Split_by_ranges_writes_one_pdf_per_range()
    {
        var input = TextPdf(Workspace.Path("book.pdf"), "a", "b", "c", "d", "e");
        Assert.Equal("1-2, 3-", (await Op("pdf.split.ranges").GetDynamicDefaultsAsync([InputFile.FromPath(input)], default)).GetString("ranges", ""));

        var folder = Single(await RunAsync("pdf.split.ranges", input, new() { ["ranges"] = "1-2, 3-" }));
        var files = Directory.GetFiles(folder).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(new[] { "book (pages 1-2).pdf", "book (pages 3-5).pdf" }, files);
        Assert.Equal(3, PageCount(Path.Combine(folder, "book (pages 3-5).pdf")));
    }

    [Fact]
    public async Task Extract_pages_keeps_the_listed_order()
    {
        var input = TextPdf(Workspace.Path("doc.pdf"), "a", "b", "c", "d");
        var output = Single(await RunAsync("pdf.pages.extract", input, new() { ["pages"] = "4, 1-2" }));

        Assert.Equal("doc (pages 4, 1-2).pdf", Path.GetFileName(output));
        Assert.Equal(new[] { "Page 4", "Page 1", "Page 2" }, Headings(output));
    }

    [Fact]
    public async Task Delete_pages_removes_them()
    {
        var input = TextPdf(Workspace.Path("doc.pdf"), "a", "b", "c", "d");
        var output = Single(await RunAsync("pdf.pages.delete", input, new() { ["pages"] = "2-3" }));
        Assert.Equal(new[] { "Page 1", "Page 4" }, Headings(output));

        var error = await Assert.ThrowsAsync<OperationException>(() => RunAsync("pdf.pages.delete", input, new() { ["pages"] = "1-" }));
        Assert.Contains("every page", error.Message);
    }

    [Fact]
    public async Task Reorder_appends_unmentioned_pages_and_reverse_flips()
    {
        var input = TextPdf(Workspace.Path("doc.pdf"), "a", "b", "c", "d");
        Assert.Equal("1-4", (await Op("pdf.pages.reorder").GetDynamicDefaultsAsync([InputFile.FromPath(input)], default)).GetString("order", ""));

        var reordered = Single(await RunAsync("pdf.pages.reorder", input, new() { ["order"] = "3, 1" }));
        Assert.Equal(new[] { "Page 3", "Page 1", "Page 2", "Page 4" }, Headings(reordered));

        var reversed = Single(await RunAsync("pdf.pages.reverse", input));
        Assert.Equal(new[] { "Page 4", "Page 3", "Page 2", "Page 1" }, Headings(reversed));
    }

    [Fact]
    public async Task Bad_page_numbers_get_friendly_errors()
    {
        var input = TextPdf(Workspace.Path("doc.pdf"), "a", "b");
        var tooHigh = await Assert.ThrowsAsync<OperationException>(() => RunAsync("pdf.pages.extract", input, new() { ["pages"] = "5" }));
        Assert.Equal("Page 5 doesn't exist — this PDF has 2 pages.", tooHigh.Message);
        var nonsense = await Assert.ThrowsAsync<OperationException>(() => RunAsync("pdf.pages.extract", input, new() { ["pages"] = "abc" }));
        Assert.Contains("isn't a page number", nonsense.Message);
    }

    [Theory]
    [InlineData("pdf.rotate.right", 90)]
    [InlineData("pdf.rotate.left", 270)]
    [InlineData("pdf.rotate.180", 180)]
    public async Task Rotate_sets_page_rotation(string id, int expected)
    {
        var input = TextPdf(Workspace.Path("doc.pdf"), "a", "b");
        var output = Single(await RunAsync(id, input));

        Assert.Equal("doc (rotated).pdf", Path.GetFileName(output));
        using var document = PigDocument.Open(output);
        Assert.All(document.GetPages(), p => Assert.Equal(expected, p.Rotation.Value));
        Assert.Equal("Page 1 a", PageTexts(output)[0]);
    }

    [Fact]
    public async Task Optimize_shrinks_photo_heavy_pdfs_and_keeps_them_readable()
    {
        var input = PdfWithPhoto(Workspace.Path("photo.pdf"));
        var output = Single(await RunAsync("pdf.compress.optimize", input));

        Assert.Equal("photo (compressed).pdf", Path.GetFileName(output));
        Assert.True(new FileInfo(output).Length < new FileInfo(input).Length * 0.7, $"{new FileInfo(output).Length} vs {new FileInfo(input).Length}");
        var png = Single(await RunAsync("pdf.png.150", output));
        using var bitmap = Decode(png);
        var pixel = bitmap.GetPixel(bitmap.Width - 20, bitmap.Height / 2);
        Assert.True(pixel.Red > 150, $"Photo should still render (red {pixel.Red}).");
    }

    [Fact]
    public async Task Optimize_says_so_when_nothing_can_be_saved()
    {
        var input = TextPdf(Workspace.Path("tiny.pdf"), "a");
        var error = await Assert.ThrowsAsync<OperationException>(() => RunAsync("pdf.compress.optimize", input));
        Assert.Contains("already well optimised", error.Message);
        Assert.False(File.Exists(Workspace.Path("tiny (compressed).pdf")));
    }

    [Fact]
    public async Task Rasterize_makes_scans_smaller_and_drops_text()
    {
        var input = PdfWithPhoto(Workspace.Path("scan.pdf"));
        var output = Single(await RunAsync("pdf.compress.rasterize", input));

        Assert.True(new FileInfo(output).Length < new FileInfo(input).Length);
        using var document = PigDocument.Open(output);
        var page = document.GetPage(1);
        Assert.InRange(page.Width, 840, 843); // A4 landscape, 297 mm = 841.9 pt
        Assert.Single(page.GetImages());
    }

    [Fact]
    public async Task Protect_and_unlock_round_trip()
    {
        const string password = "s3cret – ü";
        var input = TextPdf(Workspace.Path("secret.pdf"), "classified text");
        var protectedPdf = Single(await RunAsync("pdf.protect", input, new() { ["password"] = password }));
        Assert.Equal("secret (protected).pdf", Path.GetFileName(protectedPdf));

        Assert.ThrowsAny<Exception>(() => PigDocument.Open(protectedPdf).Dispose());
        Assert.Equal("Page 1 classified text", PageTexts(protectedPdf, password)[0]);

        var locked = await Assert.ThrowsAsync<OperationException>(() => RunAsync("pdf.pages.reverse", protectedPdf));
        Assert.Contains("password-protected", locked.Message);
        var render = await Assert.ThrowsAsync<OperationException>(() => RunAsync("pdf.png.150", protectedPdf));
        Assert.Contains("password-protected", render.Message);
        var text = await Assert.ThrowsAsync<OperationException>(() => RunAsync("pdf.txt", protectedPdf));
        Assert.Contains("password-protected", text.Message);
        var wrong = await Assert.ThrowsAsync<OperationException>(() => RunAsync("pdf.unlock", protectedPdf, new() { ["password"] = "nope" }));
        Assert.Equal("That password isn't right for this PDF.", wrong.Message);

        var unlocked = Single(await RunAsync("pdf.unlock", protectedPdf, new() { ["password"] = password }));
        using (var document = PigDocument.Open(unlocked))
            Assert.False(document.IsEncrypted);
        Assert.Equal("Page 1 classified text", PageTexts(unlocked)[0]);

        var notLocked = await Assert.ThrowsAsync<OperationException>(() => RunAsync("pdf.unlock", input, new() { ["password"] = "x" }));
        Assert.Contains("isn't password-protected", notLocked.Message);
    }

    [Fact]
    public async Task Protect_with_restrictions_still_unlocks_with_the_open_password()
    {
        var input = TextPdf(Workspace.Path("restricted.pdf"), "text");
        var protectedPdf = Single(await RunAsync("pdf.protect", input, new() { ["password"] = "pw", ["allowPrinting"] = false }));
        var unlocked = Single(await RunAsync("pdf.unlock", protectedPdf, new() { ["password"] = "pw" }));
        Assert.Equal("Page 1 text", PageTexts(unlocked)[0]);
    }

    [Fact]
    public async Task Metadata_can_be_removed_and_edited()
    {
        var input = PdfWithInfo(Workspace.Path("meta.pdf"), "Secret plans", "Jane Doe", "x");
        using (var original = PigDocument.Open(input))
            Assert.Equal("Jane Doe", original.Information.Author);

        var defaults = await Op("pdf.metadata.edit").GetDynamicDefaultsAsync([InputFile.FromPath(input)], default);
        Assert.Equal("Secret plans", defaults.GetString("title", ""));

        var stripped = Single(await RunAsync("pdf.metadata.remove", input));
        using (var document = PigDocument.Open(stripped))
        {
            Assert.True(string.IsNullOrEmpty(document.Information.Title));
            Assert.True(string.IsNullOrEmpty(document.Information.Author));
            Assert.True(string.IsNullOrEmpty(document.Information.Creator), document.Information.Creator);
            Assert.Equal("PDFsharp 6.2.4", document.Information.Producer); // the library always names itself; nothing original survives
            Assert.True(string.IsNullOrEmpty(document.Information.CreationDate), document.Information.CreationDate);
            // PDFsharp always regenerates a minimal XMP packet; none of the original values may survive in it.
            var xmp = document.TryGetXmpMetadata(out var metadata) ? System.Text.Encoding.UTF8.GetString(metadata.GetXmlBytes().ToArray()) : "";
            Assert.DoesNotContain("Jane Doe", xmp);
            Assert.DoesNotContain("Secret plans", xmp);
        }

        var edited = Single(await RunAsync("pdf.metadata.edit", input, new() { ["title"] = "New title", ["author"] = "Ünïcødé Author" }));
        using (var document = PigDocument.Open(edited))
        {
            Assert.Equal("New title", document.Information.Title);
            Assert.Equal("Ünïcødé Author", document.Information.Author);
        }
    }

    [Fact]
    public async Task Info_reports_pages_size_and_metadata()
    {
        var input = PdfWithInfo(Workspace.Path("info.pdf"), "Report title", "Ada", "a", "b", "c");
        var result = await RunAsync("pdf.info", input);

        Assert.Empty(result.Outputs);
        Assert.Contains("Pages: 3", result.Text);
        Assert.Contains("Page size: 210 × 297 mm (A4, portrait)", result.Text);
        Assert.Contains("Title: Report title", result.Text);
        Assert.Contains("Author: Ada", result.Text);
        Assert.Contains("Encryption: None", result.Text);
        Assert.Contains("Text: Selectable", result.Text);

        var protectedPdf = Single(await RunAsync("pdf.protect", input, new() { ["password"] = "pw" }));
        var locked = await RunAsync("pdf.info", protectedPdf);
        Assert.Contains("Password required", locked.Text);
    }

    [Fact]
    public async Task Damaged_pdfs_get_a_friendly_error()
    {
        var input = Workspace.WriteText("broken.pdf", "%PDF-1.4 this is not really a pdf");
        var error = await Assert.ThrowsAsync<OperationException>(() => RunAsync("pdf.pages.reverse", input));
        Assert.Contains("damaged", error.Message);
    }

    [Fact]
    public async Task Inputs_are_never_modified()
    {
        var input = TextPdf(Workspace.Path("keep.pdf"), "a", "b");
        var before = await File.ReadAllBytesAsync(input);
        var stamp = File.GetLastWriteTimeUtc(input);
        foreach (var id in new[] { "pdf.rotate.right", "pdf.metadata.remove", "pdf.pages.reverse", "pdf.compress.rasterize" })
        {
            try
            {
                await RunAsync(id, input);
            }
            catch (OperationException)
            {
                // Rasterize may decline (would grow the file) — that's fine here.
            }
        }
        Assert.Equal(before, await File.ReadAllBytesAsync(input));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(input));
    }
}
