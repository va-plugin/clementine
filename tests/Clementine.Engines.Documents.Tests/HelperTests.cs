using Clementine.Core;
using Clementine.Engines.Documents.Common;
using Clementine.Engines.Documents.Pdf;
using Clementine.Engines.Documents.Typography;

namespace Clementine.Engines.Documents.Tests;

public sealed class HelperTests
{
    [Theory]
    [InlineData("1-3,5,8-", 10, new[] { 1, 2, 3, 5, 8, 9, 10 })]
    [InlineData(" -2 ; last ", 4, new[] { 1, 2, 4 })]
    [InlineData("5-3", 6, new[] { 5, 4, 3 })]
    [InlineData("all", 3, new[] { 1, 2, 3 })]
    [InlineData("2–3", 3, new[] { 2, 3 })]
    public void Page_ranges_expand(string text, int pageCount, int[] expected) =>
        Assert.Equal(expected, PageRanges.Expand(PageRanges.Parse(text, pageCount)));

    [Theory]
    [InlineData("0")]
    [InlineData("11")]
    [InlineData("x")]
    [InlineData("")]
    [InlineData("3-12")]
    public void Bad_page_ranges_are_rejected(string text) =>
        Assert.Throws<OperationException>(() => PageRanges.Parse(text, 10));

    [Fact]
    public void Natural_sort_orders_numbers_by_value()
    {
        string[] names = ["scan 10.pdf", "Scan 2.pdf", "scan 1.pdf", "appendix.pdf", "scan 02b.pdf"];
        Assert.Equal(new[] { "appendix.pdf", "scan 1.pdf", "Scan 2.pdf", "scan 02b.pdf", "scan 10.pdf" }, names.Order(NaturalComparer.Instance));
    }

    [Theory]
    [InlineData(595.28, 841.89, "210 × 297 mm (A4, portrait)")]
    [InlineData(792, 612, "279 × 216 mm (Letter, landscape)")]
    [InlineData(500, 500, "176 × 176 mm (square)")]
    public void Page_sizes_are_named(double width, double height, string expected) =>
        Assert.Equal(expected, PdfMetadataTools.DescribePageSize(width, height));

    [Theory]
    [InlineData("Plain English, Ünïcødé – 東京, Ελληνικά, Кириллица", false)]
    [InlineData("مرحبا", true)]
    [InlineData("שלום", true)]
    [InlineData("हिन्दी", true)]
    [InlineData("ภาษาไทย", true)]
    public void Complex_scripts_are_detected(string text, bool complex) =>
        Assert.Equal(complex, Clementine.Engines.Documents.Markup.TextPdfWriter.NeedsComplexShaping(text));

    [Fact]
    public void Cjk_text_falls_back_to_a_font_that_has_the_glyphs()
    {
        var segoe = FontLibrary.Instance.Face("Segoe UI");
        var fallback = FontLibrary.Instance.FaceFor('東', segoe);
        Assert.NotEqual(segoe.Key, fallback.Key);
        Assert.True(fallback.Covers('東'));
        Assert.NotNull(FontLibrary.Instance.GetFont(fallback.Key));
    }

    [Fact]
    public void Faces_are_split_out_of_font_collections()
    {
        // Yu Gothic / MS Gothic / Cambria Math ship as .ttc collections on Windows.
        var face = FontLibrary.Instance.FaceFor('漢', FontLibrary.Instance.Face("Segoe UI"));
        var data = FontLibrary.Instance.GetFont(face.Key)!;
        Assert.False(TrueTypeCollection.IsCollection(data));
        var version = (uint)(data[0] << 24 | data[1] << 16 | data[2] << 8 | data[3]);
        Assert.True(version is 0x00010000 or 0x4F54544F or 0x74727565, $"sfnt version {version:X8}");
    }

    [Theory]
    [InlineData(@"C:\Pages\Track #1 (100%).html", "file:///C:/Pages/Track%20%231%20%28100%25%29.html")]
    [InlineData(@"C:\Bücher\東京?.html", "file:///C:/B%C3%BCcher/%E6%9D%B1%E4%BA%AC%3F.html")]
    [InlineData(@"C:\Folder #2\", "file:///C:/Folder%20%232/")]
    [InlineData(@"\\server\share\a b.html", "file://server/share/a%20b.html")]
    [InlineData(@"C:\", "file:///C:/")]
    public void File_uris_keep_hashes_and_percents_in_the_path(string path, string expected) =>
        Assert.Equal(expected, FileUris.For(path));

    [Fact]
    public void Page_zero_and_empty_documents_get_clear_messages()
    {
        Assert.Contains("numbered from 1", Assert.Throws<OperationException>(() => PageRanges.Parse("0", 10)).Message);
        Assert.Contains("numbered from 1", Assert.Throws<OperationException>(() => PageRanges.Parse("0-3", 10)).Message);
        Assert.Contains("10 pages", Assert.Throws<OperationException>(() => PageRanges.Parse("3-12", 10)).Message);
        Assert.Contains("no pages", Assert.Throws<OperationException>(() => PageRanges.Parse("1", 0)).Message);
        Assert.Equal([1, 2, 3], PageRanges.Expand(PageRanges.Parse("1~3", 5)));
    }
}
