using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Clementine.App.Branding;

/// <summary>Renders <see cref="IconArt"/> to bitmaps and writes the multi-size .ico and PNG set.</summary>
public static class IconRenderer
{
    /// <summary>Sizes stored in clementine.ico (Explorer, taskbar, tray and Alt-Tab pick from these).</summary>
    public static readonly int[] IcoSizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];

    /// <summary>Renders the mark at <paramref name="size"/> × <paramref name="size"/> pixels (straight-alpha BGRA, frozen).</summary>
    public static BitmapSource Render(int size)
    {
        var drawing = IconArt.Create(simplified: size <= 24);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(size / IconArt.CanvasSize, size / IconArt.CanvasSize));
            dc.DrawDrawing(drawing);
        }
        RenderOptions.SetEdgeMode(visual, EdgeMode.Unspecified);
        var target = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        var straight = new FormatConvertedBitmap(target, PixelFormats.Bgra32, null, 0);
        var copy = new WriteableBitmap(straight);
        copy.Freeze();
        return copy;
    }

    /// <summary>Writes clementine.ico and clementine-&lt;size&gt;.png files into <paramref name="directory"/>; returns the paths.</summary>
    public static IReadOnlyList<string> Export(string directory)
    {
        Directory.CreateDirectory(directory);
        var written = new List<string>();
        var images = IcoSizes.Select(Render).ToList();

        var ico = Path.Combine(directory, "clementine.ico");
        using (var stream = File.Create(ico))
            IcoWriter.Write(stream, images);
        written.Add(ico);

        foreach (var size in IcoSizes.Append(512))
        {
            var path = Path.Combine(directory, $"clementine-{size}.png");
            SavePng(Render(size), path);
            written.Add(path);
        }
        return written;
    }

    public static void SavePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    public static byte[] EncodePng(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}

/// <summary>
/// Minimal .ico writer: 32-bit DIB entries for sizes below 256 (best compatibility with GDI, the tray and
/// System.Drawing) and a PNG entry for 256 px (what Explorer expects for large icons).
/// </summary>
public static class IcoWriter
{
    public static void Write(Stream stream, IReadOnlyList<BitmapSource> images)
    {
        var payloads = images.Select(image => image.PixelWidth >= 256 ? IconRenderer.EncodePng(image) : EncodeDib(image)).ToList();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)images.Count);

        var offset = 6 + 16 * images.Count;
        for (var i = 0; i < images.Count; i++)
        {
            var size = images[i].PixelWidth;
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0); // palette colours
            writer.Write((byte)0); // reserved
            writer.Write((ushort)1); // planes
            writer.Write((ushort)32); // bits per pixel
            writer.Write((uint)payloads[i].Length);
            writer.Write((uint)offset);
            offset += payloads[i].Length;
        }
        foreach (var payload in payloads)
            writer.Write(payload);
    }

    /// <summary>BITMAPINFOHEADER + bottom-up BGRA rows + an all-zero AND mask (alpha does the masking).</summary>
    static byte[] EncodeDib(BitmapSource image)
    {
        var source = image.Format == PixelFormats.Bgra32 ? image : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var width = source.PixelWidth;
        var height = source.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        source.CopyPixels(pixels, stride, 0);
        var maskStride = (width + 31) / 32 * 4;

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(40);
        writer.Write(width);
        writer.Write(height * 2); // colour + mask
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(0); // BI_RGB
        writer.Write(stride * height + maskStride * height);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        for (var y = height - 1; y >= 0; y--)
            writer.Write(pixels, y * stride, stride);
        writer.Write(new byte[maskStride * height]);
        writer.Flush();
        return stream.ToArray();
    }
}
