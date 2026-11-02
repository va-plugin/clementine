using ImageMagick;
using ImageMagick.Drawing;

namespace Clementine.Engines.Images.Tests.Fixtures;

/// <summary>Small, deterministic test images generated with Magick.NET.</summary>
public static class ImageFixtures
{
    /// <summary>64×48: left half opaque red, right half fully transparent, an opaque blue 8×8 square at (0,0).</summary>
    public static string TransparentPng(string directory, string name = "alpha.png")
    {
        var path = Path.Combine(directory, name);
        using var image = new MagickImage(MagickColors.Transparent, 64, 48);
        image.Draw(new Drawables().FillColor(MagickColors.Red).Rectangle(0, 0, 31, 47));
        image.Draw(new Drawables().FillColor(MagickColors.Blue).Rectangle(0, 0, 7, 7));
        image.Depth = 8;
        image.Write(path, MagickFormat.Png);
        return path;
    }

    /// <summary>A photo-like (noisy fractal) image, deterministic across runs.</summary>
    public static string Photo(string directory, string name = "photo.jpg", uint width = 320, uint height = 240, uint quality = 92)
    {
        var path = Path.Combine(directory, name);
        MagickNET.SetRandomSeed(1234);
        using var image = new MagickImage("plasma:#3060c0-#f0a040", width, height);
        image.Depth = 8;
        image.Quality = quality;
        image.Write(path, FormatFor(name));
        return path;
    }

    /// <summary>
    /// 64×32 JPEG with EXIF orientation 6 (rotate 90° clockwise to display): left half red, right half blue.
    /// Displayed upright it is 32×64 with red on top. Carries GPS and camera tags.
    /// </summary>
    public static string RotatedJpeg(string directory, string name = "rotated.jpg")
    {
        var path = Path.Combine(directory, name);
        using var image = new MagickImage(MagickColors.Blue, 64, 32);
        image.Draw(new Drawables().FillColor(MagickColors.Red).Rectangle(0, 0, 31, 31));
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Make, "TestCam");
        exif.SetValue(ExifTag.Model, "Model 1");
        exif.SetValue(ExifTag.Orientation, (ushort)6);
        exif.SetValue(ExifTag.GPSLatitudeRef, "N");
        exif.SetValue(ExifTag.GPSLatitude, [new Rational(52, 1), new Rational(22, 1), new Rational(30, 1)]);
        exif.SetValue(ExifTag.GPSLongitudeRef, "E");
        exif.SetValue(ExifTag.GPSLongitude, [new Rational(4, 1), new Rational(53, 1), new Rational(0, 1)]);
        image.SetProfile(exif);
        image.Orientation = OrientationType.RightTop;
        image.Quality = 95;
        image.Write(path, MagickFormat.Jpeg);
        return path;
    }

    /// <summary>A JPEG with EXIF (camera + GPS) and XMP (with GPS), upright.</summary>
    public static string JpegWithLocation(string directory, string name = "located.jpg")
    {
        var path = Path.Combine(directory, name);
        MagickNET.SetRandomSeed(99);
        using var image = new MagickImage("plasma:", 120, 80);
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Make, "TestCam");
        exif.SetValue(ExifTag.Model, "Model 1");
        exif.SetValue(ExifTag.DateTimeOriginal, "2024:06:01 12:00:00");
        exif.SetValue(ExifTag.GPSLatitudeRef, "N");
        exif.SetValue(ExifTag.GPSLatitude, [new Rational(52, 1), new Rational(22, 1), new Rational(30, 1)]);
        exif.SetValue(ExifTag.GPSLongitudeRef, "E");
        exif.SetValue(ExifTag.GPSLongitude, [new Rational(4, 1), new Rational(53, 1), new Rational(0, 1)]);
        image.SetProfile(exif);
        var xmp = """
            <?xpacket begin="" id="W5M0MpCehiHzreSzNTczkc9d"?>
            <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
            <rdf:Description rdf:about="" xmlns:exif="http://ns.adobe.com/exif/1.0/" xmlns:xmp="http://ns.adobe.com/xap/1.0/"
             exif:GPSLatitude="52,22.5N" exif:GPSLongitude="4,53.0E" xmp:Rating="4"/>
            </rdf:RDF></x:xmpmeta>
            <?xpacket end="w"?>
            """;
        image.SetProfile(new XmpProfile(System.Text.Encoding.UTF8.GetBytes(xmp)));
        image.Comment = "secret comment";
        image.Quality = 90;
        image.Write(path, MagickFormat.Jpeg);
        return path;
    }

    /// <summary>A CMYK JPEG filled with pure cyan ink (C=100 %, M=Y=K=0), no embedded profile.</summary>
    public static string CmykJpeg(string directory, string name = "cmyk.jpg")
    {
        var path = Path.Combine(directory, name);
        using var image = new MagickImage(MagickColors.Cyan, 32, 32);
        image.ColorSpace = ColorSpace.CMYK;
        image.Quality = 95;
        image.Write(path, MagickFormat.Jpeg);
        return path;
    }

    /// <summary>100×50 SVG: transparent background, red 30×30 square at (10,10).</summary>
    public static string Svg(string directory, string name = "logo.svg")
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, "<svg xmlns='http://www.w3.org/2000/svg' width='100' height='50' viewBox='0 0 100 50'><rect x='10' y='10' width='30' height='30' fill='#ff0000'/></svg>");
        return path;
    }

    /// <summary>A 3-frame 40×30 animated GIF (red, green, blue), looping, 20 ms per frame.</summary>
    public static string AnimatedGif(string directory, string name = "anim.gif")
    {
        var path = Path.Combine(directory, name);
        using var frames = new MagickImageCollection();
        foreach (var color in new[] { MagickColors.Red, MagickColors.Lime, MagickColors.Blue })
        {
            var frame = new MagickImage(color, 40, 30) { AnimationDelay = 20 };
            frames.Add(frame);
        }
        frames[0].AnimationIterations = 0;
        frames.Write(path, MagickFormat.Gif);
        return path;
    }

    /// <summary>16-bit PNG whose every pixel is (0x1234, 0x8000, 0xFEDC).</summary>
    public static string SixteenBitPng(string directory, string name = "deep.png")
    {
        var path = Path.Combine(directory, name);
        using var image = new MagickImage(MagickColors.Black, 16, 16);
        image.Evaluate(Channels.Red, EvaluateOperator.Set, 0x1234);
        image.Evaluate(Channels.Green, EvaluateOperator.Set, 0x8000);
        image.Evaluate(Channels.Blue, EvaluateOperator.Set, 0xFEDC);
        image.Depth = 16;
        image.Write(path, MagickFormat.Png);
        return path;
    }

    /// <summary>A linear-light EXR with highlights far above 1.0 (sun = 50×) on a mid-grey sky.</summary>
    public static string HdrExr(string directory, string name = "scene.exr")
    {
        var path = Path.Combine(directory, name);
        var max = Quantum.Max;
        using var image = new MagickImage(MagickColors.Black, 32, 16);
        image.ColorSpace = ColorSpace.RGB;
        image.Evaluate(Channels.RGB, EvaluateOperator.Set, 0.18 * max);
        image.Evaluate(Channels.RGB, new MagickGeometry(0, 0, 8, 8), EvaluateOperator.Set, 50.0 * max);
        image.Write(path, MagickFormat.Exr);
        return path;
    }

    static MagickFormat FormatFor(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".png" => MagickFormat.Png,
        ".webp" => MagickFormat.WebP,
        ".tif" or ".tiff" => MagickFormat.Tiff,
        ".bmp" => MagickFormat.Bmp,
        ".gif" => MagickFormat.Gif,
        ".avif" => MagickFormat.Avif,
        _ => MagickFormat.Jpeg,
    };
}

/// <summary>Reads results back with Magick.NET for assertions.</summary>
public static class ImageAssert
{
    public static MagickImage Read(string path) => new(path);

    /// <summary>8-bit RGBA of a pixel.</summary>
    public static (byte R, byte G, byte B, byte A) Pixel(string path, int x, int y)
    {
        using var image = new MagickImage(path);
        return Pixel(image, x, y);
    }

    public static (byte R, byte G, byte B, byte A) Pixel(IMagickImage<float> image, int x, int y)
    {
        var color = image.GetPixels().GetPixel(x, y).ToColor()!;
        static byte B8(float v) => (byte)Math.Clamp(Math.Round(v / 257.0), 0, 255);
        var alpha = image.HasAlpha ? B8(color.A) : (byte)255;
        return (B8(color.R), B8(color.G), B8(color.B), alpha);
    }

    public static void Near((byte R, byte G, byte B, byte A) actual, (byte R, byte G, byte B, byte A) expected, int tolerance = 6, string? what = null)
    {
        var ok = Math.Abs(actual.R - expected.R) <= tolerance && Math.Abs(actual.G - expected.G) <= tolerance
            && Math.Abs(actual.B - expected.B) <= tolerance && Math.Abs(actual.A - expected.A) <= tolerance;
        Assert.True(ok, $"{what ?? "pixel"}: expected {expected}, got {actual}");
    }

    /// <summary>The format ImageMagick detects from the file's content.</summary>
    public static MagickFormat DetectedFormat(string path)
    {
        using var image = new MagickImage();
        image.Ping(path); // signature first, extension only for formats without one (ICO)
        return image.Format;
    }
}
