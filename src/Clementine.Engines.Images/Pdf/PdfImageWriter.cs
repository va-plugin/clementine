using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Clementine.Engines.Images.Pdf;

/// <summary>How an image is embedded in a PDF page.</summary>
public enum PdfImageEncoding
{
    /// <summary>Lossless (Flate), with a soft mask when the image has transparency.</summary>
    Lossless,

    /// <summary>Re-encoded as high-quality JPEG (photos without transparency).</summary>
    Jpeg,
}

/// <summary>One page: an image drawn into a rectangle (PDF points, origin bottom-left).</summary>
public sealed record PdfImagePage(
    IMagickImage<QuantumType> Image,
    double PageWidth,
    double PageHeight,
    double X,
    double Y,
    double DrawWidth,
    double DrawHeight)
{
    /// <summary>Original JPEG bytes to embed untouched (only for upright 8-bit RGB/grey JPEGs).</summary>
    public byte[]? PassThroughJpeg { get; init; }

    public PdfImageEncoding Encoding { get; init; } = PdfImageEncoding.Lossless;

    /// <summary>A page exactly the size of the image at <paramref name="dpi"/>.</summary>
    public static PdfImagePage FitToImage(IMagickImage<QuantumType> image, double dpi)
    {
        var w = image.Width * 72.0 / dpi;
        var h = image.Height * 72.0 / dpi;
        return new PdfImagePage(image, w, h, 0, 0, w, h);
    }

    /// <summary>The image scaled to fit inside a page of the given size with a margin, centred.</summary>
    public static PdfImagePage OnPage(IMagickImage<QuantumType> image, double pageWidth, double pageHeight, double margin)
    {
        var boxW = Math.Max(1, pageWidth - 2 * margin);
        var boxH = Math.Max(1, pageHeight - 2 * margin);
        var scale = Math.Min(boxW / image.Width, boxH / image.Height);
        var w = image.Width * scale;
        var h = image.Height * scale;
        return new PdfImagePage(image, pageWidth, pageHeight, (pageWidth - w) / 2, (pageHeight - h) / 2, w, h);
    }
}

/// <summary>
/// Writes a PDF 1.7 file with one image per page, streaming each page as it is added (so a long series of photos
/// never has to be in memory at once). Dependency-free: JPEGs can be embedded byte for byte, everything else is
/// stored losslessly (Flate) with a soft mask for transparency and the image's ICC profile.
/// </summary>
public sealed class PdfImageWriter
{
    const int CatalogId = 1, PagesId = 2, InfoId = 3;

    readonly ObjectWriter _writer;
    readonly List<int> _pageIds = [];
    bool _finished;

    public PdfImageWriter(Stream output)
    {
        _writer = new ObjectWriter(output);
        _writer.Raw("%PDF-1.7\n%âãÏÓ\n");
        _writer.Reserve(3);
    }

    public int PageCount => _pageIds.Count;

    /// <summary>Writes one page; the page's image may be disposed afterwards.</summary>
    public void AddPage(PdfImagePage page)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        _pageIds.Add(WritePage(_writer, page, PagesId));
    }

    /// <summary>Writes the page tree, document info and cross-reference table.</summary>
    public void Finish(string? title)
    {
        if (_finished)
            return;
        if (_pageIds.Count == 0)
            throw new InvalidOperationException("A PDF needs at least one page.");
        _finished = true;

        _writer.Begin(PagesId);
        _writer.Raw($"<< /Type /Pages /Count {_pageIds.Count} /Kids [{string.Join(" ", _pageIds.Select(id => $"{id} 0 R"))}] >>\n");
        _writer.End();

        _writer.Begin(CatalogId);
        _writer.Raw($"<< /Type /Catalog /Pages {PagesId} 0 R >>\n");
        _writer.End();

        _writer.Begin(InfoId);
        var created = DateTimeOffset.Now;
        var offset = created.Offset;
        var date = $"D:{created:yyyyMMddHHmmss}{(offset < TimeSpan.Zero ? '-' : '+')}{Math.Abs(offset.Hours):00}'{Math.Abs(offset.Minutes):00}'";
        _writer.Raw($"<< /Producer {TextString("Clementine")} /CreationDate ({date})");
        if (!string.IsNullOrWhiteSpace(title))
            _writer.Raw($" /Title {TextString(title)}");
        _writer.Raw(" >>\n");
        _writer.End();

        _writer.Finish(CatalogId, InfoId);
    }

    /// <summary>Writes a complete PDF with the given pages.</summary>
    public static void Write(Stream output, IReadOnlyList<PdfImagePage> pages, string? title, CancellationToken cancellationToken = default)
    {
        var writer = new PdfImageWriter(output);
        foreach (var page in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.AddPage(page);
        }
        writer.Finish(title);
    }

    static int WritePage(ObjectWriter writer, PdfImagePage page, int pagesId)
    {
        var image = page.Image;
        var gray = image.ColorSpace is ColorSpace.Gray or ColorSpace.LinearGray;
        var components = gray ? 1 : 3;

        int? iccId = null;
        if (image.GetColorProfile() is { } profile && profile.ColorSpace == (gray ? ColorSpace.Gray : ColorSpace.sRGB) && profile.ToByteArray() is { Length: > 0 } iccBytes)
        {
            iccId = writer.Next();
            var compressed = Deflate(iccBytes);
            writer.Begin(iccId.Value);
            writer.Raw($"<< /N {components} /Alternate /{(gray ? "DeviceGray" : "DeviceRGB")} /Filter /FlateDecode /Length {compressed.Length} >>\nstream\n");
            writer.Bytes(compressed);
            writer.Raw("\nendstream\n");
            writer.End();
        }
        var colorSpace = iccId is { } icc ? $"[/ICCBased {icc} 0 R]" : gray ? "/DeviceGray" : "/DeviceRGB";

        int? maskId = null;
        byte[] data;
        string filter;
        if (page.PassThroughJpeg is { } jpeg)
        {
            data = jpeg;
            filter = "/DCTDecode";
        }
        else if (page.Encoding == PdfImageEncoding.Jpeg && !image.HasAlpha)
        {
            using var copy = image.Clone();
            copy.Depth = 8;
            copy.Quality = 92;
            copy.Settings.SetDefines(new ImageMagick.Formats.JpegWriteDefines { SamplingFactor = ImageMagick.Formats.JpegSamplingFactor.Ratio444 });
            copy.Strip();
            data = copy.ToByteArray(MagickFormat.Jpeg);
            filter = "/DCTDecode";
        }
        else
        {
            using var pixels = image.GetPixelsUnsafe();
            data = Deflate(pixels.ToByteArray(gray ? "R" : "RGB") ?? []);
            filter = "/FlateDecode";
            if (image.HasAlpha && !image.IsOpaque)
            {
                var alpha = Deflate(pixels.ToByteArray("A") ?? []);
                maskId = writer.Next();
                writer.Begin(maskId.Value);
                writer.Raw($"<< /Type /XObject /Subtype /Image /Width {image.Width} /Height {image.Height} /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode /Length {alpha.Length} >>\nstream\n");
                writer.Bytes(alpha);
                writer.Raw("\nendstream\n");
                writer.End();
            }
        }

        var imageId = writer.Next();
        writer.Begin(imageId);
        writer.Raw($"<< /Type /XObject /Subtype /Image /Width {image.Width} /Height {image.Height} /ColorSpace {colorSpace} /BitsPerComponent 8 /Filter {filter}");
        if (maskId is { } mask)
            writer.Raw($" /SMask {mask} 0 R");
        writer.Raw($" /Length {data.Length} >>\nstream\n");
        writer.Bytes(data);
        writer.Raw("\nendstream\n");
        writer.End();

        var content = Encoding.ASCII.GetBytes($"q {N(page.DrawWidth)} 0 0 {N(page.DrawHeight)} {N(page.X)} {N(page.Y)} cm /Im0 Do Q\n");
        var contentId = writer.Next();
        writer.Begin(contentId);
        writer.Raw($"<< /Length {content.Length} >>\nstream\n");
        writer.Bytes(content);
        writer.Raw("endstream\n");
        writer.End();

        var pageId = writer.Next();
        writer.Begin(pageId);
        writer.Raw($"<< /Type /Page /Parent {pagesId} 0 R /MediaBox [0 0 {N(page.PageWidth)} {N(page.PageHeight)}] " +
                   $"/Resources << /XObject << /Im0 {imageId} 0 R >> >> /Contents {contentId} 0 R >>\n");
        writer.End();
        return pageId;
    }

    static byte[] Deflate(byte[] data)
    {
        using var buffer = new MemoryStream();
        using (var zlib = new ZLibStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(data);
        return buffer.ToArray();
    }

    static string N(double value) => Math.Round(value, 3).ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>A PDF text string: plain when ASCII-safe, UTF-16BE hex otherwise.</summary>
    static string TextString(string text)
    {
        if (text.All(c => c is >= ' ' and <= '~'))
            return "(" + text.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)") + ")";
        return "<FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(text)) + ">";
    }

    /// <summary>Tracks byte offsets of numbered objects and writes the cross-reference table.</summary>
    sealed class ObjectWriter(Stream output)
    {
        readonly Dictionary<int, long> _offsets = [];
        long _position;
        int _last;

        public void Reserve(int count) => _last = Math.Max(_last, count);

        public int Next() => ++_last;

        public void Begin(int id)
        {
            _offsets[id] = _position;
            Raw($"{id} 0 obj\n");
        }

        public void End() => Raw("endobj\n");

        public void Raw(string text) => Bytes(Encoding.Latin1.GetBytes(text));

        public void Bytes(byte[] data)
        {
            output.Write(data);
            _position += data.Length;
        }

        public void Finish(int rootId, int infoId)
        {
            var xref = _position;
            var builder = new StringBuilder();
            builder.Append(CultureInfo.InvariantCulture, $"xref\n0 {_last + 1}\n0000000000 65535 f \n");
            for (var id = 1; id <= _last; id++)
            {
                builder.Append(_offsets.TryGetValue(id, out var offset)
                    ? $"{offset.ToString("D10", CultureInfo.InvariantCulture)} 00000 n \n"
                    : "0000000000 65535 f \n");
            }
            builder.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {_last + 1} /Root {rootId} 0 R /Info {infoId} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            Raw(builder.ToString());
        }
    }
}
