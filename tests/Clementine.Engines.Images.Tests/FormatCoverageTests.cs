using Clementine.Core;
using Clementine.Engines.Images.Imaging;
using Clementine.Engines.Images.Tests.Fixtures;
using Clementine.TestSupport;
using ImageMagick;
using ImageMagick.Drawing;

namespace Clementine.Engines.Images.Tests;

/// <summary>Real decodes of every input family the engine advertises (not just the format table).</summary>
public class FormatCoverageTests
{
    static readonly ImageOperationProvider Provider = new();

    [Theory]
    [InlineData("webp", MagickFormat.WebP, 4)]
    [InlineData("avif", MagickFormat.Avif, 40)]
    [InlineData("jxl", MagickFormat.Jxl, 16)]
    [InlineData("tif", MagickFormat.Tiff, 0)]
    [InlineData("bmp", MagickFormat.Bmp, 0)]
    [InlineData("gif", MagickFormat.Gif, 0)]
    [InlineData("tga", MagickFormat.Tga, 0)]
    [InlineData("qoi", MagickFormat.Qoi, 0)]
    [InlineData("jp2", MagickFormat.Jp2, 16)]
    [InlineData("ico", MagickFormat.Ico, 0)]
    [InlineData("dds", MagickFormat.Dds, 24)]
    [InlineData("pcx", MagickFormat.Pcx, 0)]
    [InlineData("ppm", MagickFormat.Ppm, 0)]
    [InlineData("exr", MagickFormat.Exr, 24)]
    public async Task Input_format_decodes_to_png(string extension, MagickFormat format, int tolerance)
    {
        using var workspace = new TestWorkspace($"read-{extension}");
        var input = workspace.Path($"pair.{extension}");
        using (var image = new MagickImage(MagickColors.Blue, 48, 32))
        {
            image.Draw(new Drawables().FillColor(MagickColors.Red).Rectangle(0, 0, 23, 31));
            image.Depth = 8;
            image.Write(input, format);
        }
        Assert.Contains(InputFile.FromPath(input).Extension, ImageFormats.Readable);
        var output = Assert.Single((await workspace.RunAsync(workspace.Operation(Provider, "image.png"), [input])).Outputs);
        using var png = new MagickImage(output);
        Assert.Equal((48u, 32u), (png.Width, png.Height));
        ImageAssert.Near(ImageAssert.Pixel(png, 6, 16), (255, 0, 0, 255), tolerance, $"{extension} red");
        ImageAssert.Near(ImageAssert.Pixel(png, 40, 16), (0, 0, 255, 255), tolerance, $"{extension} blue");
    }

    [Fact]
    public async Task Camera_raw_linear_dng_is_decoded_by_libraw()
    {
        using var workspace = new TestWorkspace("raw");
        var input = RawFixtures.LinearDng(workspace.Path("IMG_0001.dng"));
        var output = Assert.Single((await workspace.RunAsync(workspace.Operation(Provider, "image.jpg"), [input])).Outputs);
        Assert.Equal("IMG_0001.jpg", Path.GetFileName(output));
        using var image = new MagickImage(output);
        Assert.Equal((64u, 48u), (image.Width, image.Height));
        var left = ImageAssert.Pixel(image, 10, 24);
        var right = ImageAssert.Pixel(image, 54, 24);
        Assert.True(left.R > left.G + 60 && left.R > left.B + 60, $"left half is red ({left})");
        Assert.True(right.G > right.R + 60 && right.G > right.B + 60, $"right half is green ({right})");
    }

    [Fact]
    public async Task Heic_is_decoded()
    {
        using var workspace = new TestWorkspace("heic");
        var input = await HeicFixture.CreateAsync(workspace.Root);
        var output = Assert.Single((await workspace.RunAsync(workspace.Operation(Provider, "image.jpg"), [input])).Outputs);
        using var image = new MagickImage(output);
        Assert.Equal((64u, 48u), (image.Width, image.Height));
        ImageAssert.Near(ImageAssert.Pixel(image, 10, 24), (255, 0, 0, 255), 40, "red half");
        ImageAssert.Near(ImageAssert.Pixel(image, 54, 24), (0, 0, 255, 255), 40, "blue half");
        Assert.False(workspace.Operation(Provider, "image.jpg").Accepts(InputFile.FromPath(output)), "no JPEG → JPEG");
    }

    [Fact]
    public async Task Portrait_iphone_heic_is_rotated_exactly_once()
    {
        using var workspace = new TestWorkspace("heic-rot");
        var input = await HeicFixture.CreateAsync(workspace.Root, "portrait.heic", rotatedLikeAnIPhone: true);
        var output = Assert.Single((await workspace.RunAsync(workspace.Operation(Provider, "image.png"), [input])).Outputs);
        using var image = new MagickImage(output);
        Assert.Equal((48u, 64u), (image.Width, image.Height));
        ImageAssert.Near(ImageAssert.Pixel(image, 24, 10), (255, 0, 0, 255), 40, "red on top");
        ImageAssert.Near(ImageAssert.Pixel(image, 24, 54), (0, 0, 255, 255), 40, "blue at the bottom");
        var orientation = image.GetExifProfile()?.GetValue(ExifTag.Orientation)?.Value;
        Assert.True(orientation is null or 1, $"EXIF orientation reset (was {orientation})");
    }

    [Fact]
    public async Task Compressed_svg_and_jfif_are_read()
    {
        using var workspace = new TestWorkspace("svgz");
        var svg = File.ReadAllBytes(ImageFixtures.Svg(workspace.Root));
        var svgz = workspace.Path("logo.svgz");
        using (var file = File.Create(svgz))
        using (var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionLevel.Optimal))
            gzip.Write(svg);
        using (var png = new MagickImage(Assert.Single((await workspace.RunAsync(workspace.Operation(Provider, "image.png.2x"), [svgz])).Outputs)))
            Assert.Equal((200u, 100u), (png.Width, png.Height));

        var jfif = Path.ChangeExtension(ImageFixtures.Photo(workspace.Root, "scan.jpg", 40, 30), ".jfif");
        File.Move(Path.ChangeExtension(jfif, ".jpg"), jfif);
        Assert.False(workspace.Operation(Provider, "image.jpg").Accepts(InputFile.FromPath(jfif)), "JFIF already is JPEG");
        using var webp = new MagickImage(Assert.Single((await workspace.RunAsync(workspace.Operation(Provider, "image.webp"), [jfif])).Outputs));
        Assert.Equal((40u, 30u), (webp.Width, webp.Height));
    }

    [Fact]
    public async Task Misnamed_files_are_decoded_by_their_content()
    {
        using var workspace = new TestWorkspace("misnamed");
        var input = workspace.Path("actually-a-png.jpg");
        using (var image = new MagickImage(MagickColors.Red, 20, 10))
            image.Write(input, MagickFormat.Png);
        var output = Assert.Single((await workspace.RunAsync(workspace.Operation(Provider, "image.webp"), [input])).Outputs);
        using var webp = new MagickImage(output);
        Assert.Equal((20u, 10u), (webp.Width, webp.Height));
    }

    [Fact]
    public async Task Psd_in_images_to_pdf_and_info_work_together()
    {
        using var workspace = new TestWorkspace("psd-pdf");
        var psd = PsdFixtures.Write(workspace.Path("art.psd"), PsdTests.TransparentDesign() with { BlankComposite = true, MarkNoRealMergedData = true });
        var result = await workspace.RunAsync(workspace.Operation(Provider, "image.pdf.combine"), [psd], new Dictionary<string, object?> { ["paper"] = "fit" });
        var pdf = PdfInspector.Inspect(Assert.Single(result.Outputs));
        Assert.Equal(1, pdf.Pages);
        Assert.True(Assert.Single(pdf.Images).HasMask);
        Assert.Contains(result.Warnings, w => w.Contains("rebuilt", StringComparison.Ordinal));
    }
}
