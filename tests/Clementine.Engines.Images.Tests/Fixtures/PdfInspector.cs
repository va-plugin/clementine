using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Clementine.Engines.Images.Tests.Fixtures;

/// <summary>What a generated PDF contains (enough to check structure without a PDF renderer).</summary>
public sealed record PdfSummary(int Pages, IReadOnlyList<(double Width, double Height)> MediaBoxes, IReadOnlyList<(int Width, int Height, string Filter, bool HasMask)> Images);

/// <summary>Validates the structure of PDFs written by Clementine's image writer (header, xref offsets, trailer).</summary>
public static partial class PdfInspector
{
    public static PdfSummary Inspect(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var text = Encoding.Latin1.GetString(bytes);
        Assert.StartsWith("%PDF-1.", text);
        Assert.EndsWith("%%EOF\n", text);

        var startxref = int.Parse(StartXref().Match(text).Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.Equal("xref", text.Substring(startxref, 4));
        var xref = XrefHeader().Match(text, startxref);
        Assert.True(xref.Success, "xref table header");
        var count = int.Parse(xref.Groups[1].Value, CultureInfo.InvariantCulture);
        var entries = text.Substring(xref.Index + xref.Length, count * 20);
        for (var id = 1; id < count; id++)
        {
            var entry = entries.Substring(id * 20, 20);
            if (entry[17] != 'n')
                continue;
            var offset = int.Parse(entry[..10], CultureInfo.InvariantCulture);
            Assert.True(text.AsSpan(offset).StartsWith($"{id} 0 obj"), $"xref entry {id} points at offset {offset}, which isn't its object");
        }
        Assert.Matches(@"trailer\n<< /Size \d+ /Root 1 0 R", text);

        var boxes = MediaBox().Matches(text)
            .Select(m => (double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)))
            .ToList();
        var masks = SoftMaskReference().Matches(text).Select(m => m.Groups[1].Value).ToHashSet();
        var images = new List<(int, int, string, bool)>();
        foreach (Match m in ImageObject().Matches(text))
        {
            var header = text.LastIndexOf(" 0 obj", m.Index, StringComparison.Ordinal);
            var id = text[(text.LastIndexOf('\n', header) + 1)..header];
            if (masks.Contains(id))
                continue;
            images.Add((int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                m.Groups[4].Value, m.Groups[5].Success));
        }
        var pages = PageObject().Matches(text).Count;
        return new PdfSummary(pages, boxes, images);
    }

    [GeneratedRegex(@"startxref\n(\d+)\n%%EOF")]
    private static partial Regex StartXref();

    [GeneratedRegex(@"xref\n0 (\d+)\n")]
    private static partial Regex XrefHeader();

    [GeneratedRegex(@"/MediaBox \[0 0 ([\d.]+) ([\d.]+)\]")]
    private static partial Regex MediaBox();

    [GeneratedRegex(@"/Subtype /Image /Width (\d+) /Height (\d+) /ColorSpace (\[[^\]]*\]|/\w+) /BitsPerComponent 8 /Filter /(\w+)( /SMask \d+ 0 R)?")]
    private static partial Regex ImageObject();

    [GeneratedRegex(@"/SMask (\d+) 0 R")]
    private static partial Regex SoftMaskReference();

    [GeneratedRegex(@"/Type /Page /Parent")]
    private static partial Regex PageObject();
}
