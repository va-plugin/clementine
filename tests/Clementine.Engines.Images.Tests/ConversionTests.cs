using Clementine.Core;
using Clementine.Engines.Images.Imaging;
using Clementine.Engines.Images.Tests.Fixtures;
using Clementine.TestSupport;
using ImageMagick;

namespace Clementine.Engines.Images.Tests;

public class ConversionTests
{
    static readonly ImageOperationProvider Provider = new();

    static readonly EngineContext Context = new(new ToolLocator(() => new ClementineSettings()), () => new ClementineSettings(), NullLog.Instance);

    /// <summary>Formats that keep an alpha channel.</summary>
    static readonly HashSet<string> AlphaExtensions = [".png", ".webp", ".avif", ".tif", ".gif", ".ico", ".jxl", ".heic"];

    /// <summary>PNG's drawing sizes, offered for SVG inputs only.</summary>
    static readonly HashSet<string> SvgOnlyIds = ["image.png.1x", "image.png.2x", "image.png.4x"];

    public static TheoryData<string> ConvertIds()
    {
        var data = new TheoryData<string>();
        foreach (var op in Provider.CreateOperations(Context).Where(o => o.Info.Category == OperationCategory.Convert))
            data.Add(op.Info.Id);
        return data;
    }

    [Theory]
    [MemberData(nameof(ConvertIds))]
    public async Task Every_conversion_works_on_a_transparent_png(string id)
    {
        using var workspace = new TestWorkspace("conv-png");
        var input = ImageFixtures.TransparentPng(workspace.Root);
        var op = workspace.Operation(Provider, id);
        if (!op.Accepts(InputFile.FromPath(input)))
        {
            Assert.True(id.StartsWith("image.png", StringComparison.Ordinal), $"{id} should accept PNG");
            return;
        }

        var output = Assert.Single((await workspace.RunAsync(op, [input])).Outputs);
        AssertOutputBasics(op, output, "alpha");
        if (id == "image.pdf")
        {
            var pdf = PdfInspector.Inspect(output);
            Assert.Equal(1, pdf.Pages);
            Assert.True(Assert.Single(pdf.Images).HasMask, "transparency kept as a soft mask");
            return;
        }

        using var image = ReadLargest(output);
        var expectedHeight = id == "image.ico" ? 64u : 48u; // icons are padded square
        Assert.Equal(64u, image.Width);
        Assert.Equal(expectedHeight, image.Height);
        var yOffset = id == "image.ico" ? 8 : 0;
        var tolerance = id is "image.avif" or "image.jpg.small" ? 40 : 24;
        ImageAssert.Near(ImageAssert.Pixel(image, 20, 30 + yOffset), (255, 0, 0, 255), tolerance, $"{id} red half");
        if (AlphaExtensions.Contains(Path.GetExtension(output)))
            Assert.Equal(0, ImageAssert.Pixel(image, 50, 20 + yOffset).A);
        else
            ImageAssert.Near(ImageAssert.Pixel(image, 50, 20), (255, 255, 255, 255), tolerance, $"{id} transparency flattened onto white");
    }

    [Theory]
    [MemberData(nameof(ConvertIds))]
    public async Task Every_conversion_works_on_a_jpeg_photo(string id)
    {
        using var workspace = new TestWorkspace("conv-jpg");
        var input = ImageFixtures.Photo(workspace.Root, "photo.jpg", 160, 120);
        var op = workspace.Operation(Provider, id);
        if (!op.Accepts(InputFile.FromPath(input)))
        {
            Assert.True(id.StartsWith("image.jpg", StringComparison.Ordinal) || SvgOnlyIds.Contains(id), $"{id} should accept JPEG");
            return;
        }

        var output = Assert.Single((await workspace.RunAsync(op, [input])).Outputs);
        AssertOutputBasics(op, output, "photo");
        if (id == "image.pdf")
        {
            var pdf = PdfInspector.Inspect(output);
            var picture = Assert.Single(pdf.Images);
            Assert.Equal("DCTDecode", picture.Filter); // the JPEG is embedded untouched
            Assert.Equal((160.0, 120.0), Assert.Single(pdf.MediaBoxes));
            return;
        }
        using var image = ReadLargest(output);
        Assert.Equal(id == "image.ico" ? 256u : 160u, image.Width);
        using var original = new MagickImage(input);
        if (id != "image.ico" && id != "image.gif")
            Assert.True(image.Compare(original, ErrorMetric.RootMeanSquared) < 0.06, $"{id} looks like the original");
    }

    [Theory]
    [MemberData(nameof(ConvertIds))]
    public async Task Every_conversion_works_on_a_layered_psd(string id)
    {
        using var workspace = new TestWorkspace("conv-psd");
        var input = PsdFixtures.Write(workspace.Path("design.psd"), PsdTests.TransparentDesign());
        var op = workspace.Operation(Provider, id);
        if (!op.Accepts(InputFile.FromPath(input)))
        {
            Assert.True(SvgOnlyIds.Contains(id), $"{id} should accept PSD");
            return;
        }

        var output = Assert.Single((await workspace.RunAsync(op, [input])).Outputs);
        AssertOutputBasics(op, output, "design");
        if (id == "image.pdf")
        {
            Assert.Equal(1, PdfInspector.Inspect(output).Pages);
            return;
        }
        using var image = ReadLargest(output);
        var square = id == "image.ico";
        Assert.Equal(64u, image.Width);
        Assert.Equal(square ? 64u : 48u, image.Height);
        var dy = square ? 8 : 0;
        var tolerance = id is "image.avif" or "image.jpg.small" ? 40 : 24;
        ImageAssert.Near(ImageAssert.Pixel(image, 5, 5 + dy), (255, 0, 0, 255), tolerance, $"{id} red layer");
        if (AlphaExtensions.Contains(Path.GetExtension(output)))
            Assert.Equal(0, ImageAssert.Pixel(image, 60, 2 + dy).A);
        else
            ImageAssert.Near(ImageAssert.Pixel(image, 60, 2), (255, 255, 255, 255), tolerance, $"{id} transparent area on white");
    }

    [Fact]
    public void A_conversion_to_the_inputs_own_format_is_not_offered()
    {
        using var workspace = new TestWorkspace("same");
        var png = InputFile.FromPath(ImageFixtures.TransparentPng(workspace.Root));
        var jpeg = InputFile.FromPath(ImageFixtures.Photo(workspace.Root, "p.jpeg"));
        var ops = workspace.Operations(Provider).Values.Where(o => o.Info.Category == OperationCategory.Convert).ToList();
        Assert.DoesNotContain(ops, o => o.Accepts(png) && o.Info.OutputExtension == ".png");
        Assert.DoesNotContain(ops, o => o.Accepts(jpeg) && o.Info.OutputExtension == ".jpg");
        Assert.Contains(ops, o => o.Accepts(jpeg) && o.Info.Id == "image.png");
    }

    [Fact]
    public void Heic_is_offered_only_when_this_build_can_encode_it()
    {
        var ids = Provider.CreateOperations(Context).Select(o => o.Info.Id).ToHashSet();
        Assert.Equal(MagickRuntime.CanEncode(MagickFormat.Heic), ids.Contains("image.heic"));
        Assert.Equal(MagickRuntime.CanEncode(MagickFormat.Avif), ids.Contains("image.avif"));
        Assert.Equal(MagickRuntime.CanEncode(MagickFormat.Jxl), ids.Contains("image.jxl"));
    }

    [Fact]
    public async Task Exif_orientation_is_applied_then_reset_and_camera_tags_are_kept()
    {
        using var workspace = new TestWorkspace("orient");
        var input = ImageFixtures.RotatedJpeg(workspace.Root);
        var output = Assert.Single((await workspace.RunAsync(workspace.Operation(Provider, "image.png"), [input])).Outputs);
        using var image = new MagickImage(output);
        Assert.Equal(32u, image.Width);
        Assert.Equal(64u, image.Height);
        ImageAssert.Near(ImageAssert.Pixel(image, 16, 8), (255, 0, 0, 255), 30, "top is red");
        ImageAssert.Near(ImageAssert.Pixel(image, 16, 56), (0, 0, 255, 255), 30, "bottom is blue");
        Assert.True(image.Orientation is OrientationType.TopLeft or OrientationType.Undefined);
        var exif = image.GetExifProfile();
        Assert.NotNull(exif);
        Assert.Equal("TestCam", exif.GetValue(ExifTag.Make)?.Value);
        var orientation = exif.GetValue(ExifTag.Orientation)?.Value;
        Assert.True(orientation is null or 1, $"EXIF orientation reset (was {orientation})");
    }

    [Theory]
    [InlineData("image.png")]
    [InlineData("image.webp")]
    [InlineData("image.tiff")]
    [InlineData("image.avif")]
    public async Task Colour_profiles_and_exif_are_kept(string id)
    {
        using var workspace = new TestWorkspace("icc");
        var input = workspace.Path("wide.jpg");
        using (var image = new MagickImage(MagickColors.Orange, 32, 32))
        {
            image.SetProfile(ColorProfiles.AdobeRGB1998);
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Make, "TestCam");
            image.SetProfile(exif);
            image.Write(input, MagickFormat.Jpeg);
        }
        var output = Assert.Single((await workspace.RunAsync(workspace.Operation(Provider, id), [input])).Outputs);
        using var result = new MagickImage(output);
        Assert.Contains("Adobe RGB", result.GetColorProfile()?.Description ?? "", StringComparison.Ordinal);
        // ImageMagick's TIFF writer doesn't store EXIF; libheif (AVIF) drops it too.
        if (id is not ("image.avif" or "image.tiff"))
            Assert.Equal("TestCam", result.GetExifProfile()?.GetValue(ExifTag.Make)?.Value);
    }

    [Fact]
    public async Task Cmyk_jpeg_is_converted_to_srgb_with_profiles()
    {
        using var workspace = new TestWorkspace("cmyk");
        var input = ImageFixtures.CmykJpeg(workspace.Root);
        var output = Assert.Single((await workspace.RunAsync(workspace.Operation(Provider, "image.png"), [input])).Outputs);
        using var image = new MagickImage(output);
        Assert.Equal(ColorSpace.sRGB, image.ColorSpace);
        var (r, g, b, _) = ImageAssert.Pixel(image, 10, 10);
        // SWOP cyan renders as roughly (0, 174, 239) in sRGB — not the naive (0, 255, 255).
        Assert.InRange(r, 0, 40);
        Assert.InRange(g, 140, 200);
        Assert.InRange(b, 200, 255);
    }

    [Fact]
    public async Task Sixteen_bit_is_kept_by_lossless_formats_and_reduced_for_jpeg()
    {
        using var workspace = new TestWorkspace("deep");
        var input = ImageFixtures.SixteenBitPng(workspace.Root);
        var tiff = Assert.Single((await workspace.RunAsync(workspace.Operation(Provider, "image.tiff"), [input])).Outputs);
        using (var image = new MagickImage(tiff))
        {
            Assert.Equal(16u, image.Depth);
            var pixel = image.GetPixels().GetPixel(3, 3);
            Assert.Equal(0x1234, pixel.GetChannel(0), 1.0);
            Assert.Equal(CompressionMethod.LZW, image.Compression);
        }
        var jpeg = Assert.Single((await workspace.RunAsync(workspace.Operation(Provider, "image.jpg"), [input])).Outputs);
        using (var image = new MagickImage(jpeg))
            Assert.Equal(8u, image.Depth);
    }

    [Fact]
    public async Task Animated_gif_stays_animated_as_webp_and_gives_its_first_frame_as_png()
    {
        using var workspace = new TestWorkspace("anim");
        var input = ImageFixtures.AnimatedGif(workspace.Root);

        var webp = Assert.Single((await workspace.RunAsync(workspace.Operation(Provider, "image.webp"), [input])).Outputs);
        using (var frames = new MagickImageCollection(webp))
        {
            Assert.Equal(3, frames.Count);
            ImageAssert.Near(ImageAssert.Pixel(frames[2], 5, 5), (0, 0, 255, 255), 30, "third frame blue");
        }

        var back = Assert.Single((await workspace.RunAsync(workspace.Operation(Provider, "image.gif"), [webp])).Outputs);
        using (var frames = new MagickImageCollection(back))
        {
            Assert.Equal(3, frames.Count);
            Assert.Equal(20u, frames[1].AnimationDelay);
        }

        var result = await workspace.RunAsync(workspace.Operation(Provider, "image.png"), [input]);
        using (var still = new MagickImageCollection(Assert.Single(result.Outputs)))
        {
            var frame = Assert.Single(still);
            ImageAssert.Near(ImageAssert.Pixel(frame, 5, 5), (255, 0, 0, 255), 4, "first frame red");
        }
        Assert.Contains(result.Warnings, w => w.Contains("first frame", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("image.png.1x", 1, null)]
    [InlineData("image.png.2x", 2, " (2x)")]
    [InlineData("image.png.4x", 4, " (4x)")]
    public async Task Svg_is_rasterised_at_its_intrinsic_size_and_multiples(string id, int scale, string? suffix)
    {
        using var workspace = new TestWorkspace("svg");
        var input = ImageFixtures.Svg(workspace.Root);
        var output = Assert.Single((await workspace.RunAsync(workspace.Operation(Provider, id), [input])).Outputs);
        Assert.Equal($"logo{suffix}.png", Path.GetFileName(output));
        using var image = new MagickImage(output);
        Assert.Equal((uint)(100 * scale), image.Width);
        Assert.Equal((uint)(50 * scale), image.Height);
        Assert.Equal(0, ImageAssert.Pixel(image, 1, 1).A);
        ImageAssert.Near(ImageAssert.Pixel(image, 20 * scale, 20 * scale), (255, 0, 0, 255), 2, "red square");
    }

    [Fact]
    public void Svg_multiples_are_only_offered_for_svg()
    {
        using var workspace = new TestWorkspace("svg-only");
        var png = InputFile.FromPath(ImageFixtures.Photo(workspace.Root, "a.jpg"));
        Assert.False(workspace.Operation(Provider, "image.png.2x").Accepts(png));
        Assert.True(workspace.Operation(Provider, "image.png.2x").Accepts(InputFile.FromPath(ImageFixtures.Svg(workspace.Root))));
    }

    [Fact]
    public void Png_is_one_plain_entry_for_raster_images_and_three_drawing_sizes_for_svg()
    {
        using var workspace = new TestWorkspace("png-wheel");
        var psd = InputFile.FromPath(PsdFixtures.Write(workspace.Path("design.psd"), PsdTests.TransparentDesign()));
        var jpeg = InputFile.FromPath(ImageFixtures.Photo(workspace.Root, "a.jpg"));
        var svg = InputFile.FromPath(ImageFixtures.Svg(workspace.Root));
        var png = workspace.Operations(Provider).Values.Where(o => o.Info.Category == OperationCategory.Convert && o.Info.Group == "PNG").ToList();

        foreach (var raster in new[] { psd, jpeg })
        {
            var plain = Assert.Single(png, o => o.Accepts(raster));
            Assert.Equal("image.png", plain.Info.Id);
            Assert.Null(plain.Info.Variant);
            Assert.Equal("PNG", plain.Info.Title);
            Assert.Equal("Lossless, keeps transparency and 16-bit colour.", plain.Info.Description);
        }

        var sizes = png.Where(o => o.Accepts(svg)).OrderBy(o => o.Info.Order).ToList();
        Assert.Equal(["image.png.1x", "image.png.2x", "image.png.4x"], sizes.Select(o => o.Info.Id));
        Assert.Equal(["1×", "2×", "4×"], sizes.Select(o => o.Info.Variant));
        Assert.All(sizes, o => Assert.Contains("SVG", o.Info.Description ?? "", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_wheel_shows_png_without_variants_for_a_psd_and_with_sizes_for_svgs_and_mixed_selections_get_the_plain_png()
    {
        using var workspace = new TestWorkspace("png-catalog");
        var psdPath = PsdFixtures.Write(workspace.Path("design.psd"), PsdTests.TransparentDesign());
        var svgPath = ImageFixtures.Svg(workspace.Root);
        var catalog = new OperationCatalog(workspace.Context, [new ImageOperationProvider()]);
        OperationGroup PngGroup(params string[] paths) =>
            Assert.Single(OperationCatalog.Group(catalog.ForInputs([.. paths.Select(InputFile.FromPath)], OperationCategory.Convert)), g => g.Name == "PNG");

        var forPsd = PngGroup(psdPath);
        Assert.False(forPsd.HasVariants);
        Assert.Equal("image.png", forPsd.Default.Info.Id);

        var forSvg = PngGroup(svgPath);
        Assert.Equal(["image.png.1x", "image.png.2x", "image.png.4x"], forSvg.Operations.Select(o => o.Info.Id));
        Assert.Equal("image.png.1x", forSvg.Default.Info.Id);

        var forBoth = PngGroup(svgPath, psdPath);
        Assert.Equal("image.png", Assert.Single(forBoth.Operations).Info.Id);
        var outputs = (await workspace.RunAsync(forBoth.Default, [svgPath, psdPath])).Outputs;
        Assert.Equal(["design.png", "logo.png"], outputs.Select(Path.GetFileName).Order());
        using var drawn = new MagickImage(outputs.Single(o => o.EndsWith("logo.png", StringComparison.Ordinal)));
        Assert.Equal((100u, 50u), (drawn.Width, drawn.Height)); // the SVG at its own size
    }

    [Fact]
    public async Task Animated_png_leaves_gif_and_webp_to_the_media_engine_and_gets_its_first_frame_as_a_still()
    {
        using var workspace = new TestWorkspace("apng");
        var apng = PngFixtures.Apng(workspace.Root);
        var file = InputFile.FromPath(apng);
        var convert = workspace.Operations(Provider).Values.Where(o => o.Info.Category == OperationCategory.Convert).ToList();

        Assert.DoesNotContain(convert, o => o.Accepts(file) && o.Info.Group is "GIF" or "WebP");
        foreach (var id in new[] { "image.png", "image.jpg", "image.tiff", "image.pdf", "image.ico", "image.bmp" })
            Assert.True(workspace.Operation(Provider, id).Accepts(file), $"{id} should take an APNG's first frame");
        Assert.DoesNotContain(convert, o => o.Accepts(file) && (o.Info.Description ?? "").Contains("frame", StringComparison.OrdinalIgnoreCase));
        // GIF/WebP still take other inputs, animated or not.
        var gif = InputFile.FromPath(ImageFixtures.AnimatedGif(workspace.Root));
        Assert.True(workspace.Operation(Provider, "image.webp").Accepts(gif));
        Assert.True(workspace.Operation(Provider, "image.gif").Accepts(InputFile.FromPath(ImageFixtures.TransparentPng(workspace.Root))));

        var result = await workspace.RunAsync(workspace.Operation(Provider, "image.png"), [apng]);
        var output = Assert.Single(result.Outputs);
        Assert.Equal("anim.png", Path.GetFileName(output));
        using (var still = new MagickImageCollection(output))
            ImageAssert.Near(ImageAssert.Pixel(Assert.Single(still), 8, 8), (255, 0, 0, 255), 2, "first frame red");
        Assert.Contains(result.Warnings, w => w.Contains("first frame", StringComparison.Ordinal));

        var plain = workspace.Path("plain.apng");
        File.WriteAllBytes(plain, File.ReadAllBytes(ImageFixtures.TransparentPng(workspace.Root, "plain.png")));
        Assert.DoesNotContain((await workspace.RunAsync(workspace.Operation(Provider, "image.jpg"), [plain])).Warnings,
            w => w.Contains("first frame", StringComparison.Ordinal)); // a single image named .apng isn't an animation
    }

    [Fact]
    public async Task Ico_contains_every_size_up_to_the_source()
    {
        using var workspace = new TestWorkspace("ico");
        var input = ImageFixtures.Photo(workspace.Root, "big.png", 300, 200);
        var output = Assert.Single((await workspace.RunAsync(workspace.Operation(Provider, "image.ico"), [input])).Outputs);
        using var icon = new MagickImageCollection(output);
        var sizes = icon.Select(f => f.Width).OrderDescending().ToArray();
        Assert.Equal([256u, 128u, 64u, 48u, 32u, 24u, 16u], sizes);
        Assert.All(icon, f => Assert.Equal(f.Width, f.Height));
    }

    [Fact]
    public async Task Hdr_exr_is_tone_mapped_for_8_bit_outputs()
    {
        using var workspace = new TestWorkspace("hdr");
        var input = ImageFixtures.HdrExr(workspace.Root);
        var output = Assert.Single((await workspace.RunAsync(workspace.Operation(Provider, "image.png"), [input])).Outputs);
        using var image = new MagickImage(output);
        var sun = ImageAssert.Pixel(image, 2, 2);
        var sky = ImageAssert.Pixel(image, 20, 12);
        Assert.True(sun.R > 200, $"highlight stays bright ({sun})");
        Assert.InRange(sky.R, 60, 220); // mid-grey is neither crushed to black nor blown out
        Assert.True(sun.R > sky.R + 30, "highlights remain brighter than mid-tones");
    }

    [Fact]
    public async Task Unicode_paths_with_spaces_work()
    {
        using var workspace = new TestWorkspace("unicode");
        var folder = workspace.Path("Fötö – 東京 folder/x");
        var input = ImageFixtures.TransparentPng(Path.GetDirectoryName(folder)!, "Fötö – 東京 01.png");
        var output = Assert.Single((await workspace.RunAsync(workspace.Operation(Provider, "image.webp"), [input])).Outputs);
        Assert.Equal("Fötö – 東京 01.webp", Path.GetFileName(output));
        Assert.Equal(MagickFormat.WebP, ImageAssert.DetectedFormat(output));
    }

    [Fact]
    public async Task Existing_files_are_never_overwritten_and_the_input_is_untouched()
    {
        using var workspace = new TestWorkspace("nooverwrite");
        var input = ImageFixtures.TransparentPng(workspace.Root);
        var before = File.ReadAllBytes(input);
        File.WriteAllText(Path.Combine(workspace.Root, "alpha.jpg"), "not an image");
        var output = Assert.Single((await workspace.RunAsync(workspace.Operation(Provider, "image.jpg"), [input])).Outputs);
        Assert.Equal("alpha (2).jpg", Path.GetFileName(output));
        Assert.Equal("not an image", File.ReadAllText(Path.Combine(workspace.Root, "alpha.jpg")));
        Assert.Equal(before, File.ReadAllBytes(input));
    }

    [Fact]
    public async Task Cancellation_leaves_no_output()
    {
        using var workspace = new TestWorkspace("cancel");
        var input = ImageFixtures.Photo(workspace.Root, "c.png");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            workspace.RunAsync(workspace.Operation(Provider, "image.jpg"), [input], cancellationToken: cts.Token));
        Assert.Equal(["c.png"], Directory.GetFiles(workspace.Root).Select(f => Path.GetFileName(f)!).ToArray());
    }

    [Fact]
    public async Task Cancelling_a_long_encode_stops_it_and_leaves_no_partial_file()
    {
        using var workspace = new TestWorkspace("cancel-mid");
        var input = workspace.Path("big.png");
        using (var image = new MagickImage("gradient:#203060-#f0a040", 2400, 1600))
        {
            image.AddNoise(NoiseType.Gaussian);
            image.Write(input);
        }
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            workspace.RunAsync(workspace.Operation(Provider, "image.avif"), [input], cancellationToken: cts.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"stopped promptly ({watch.Elapsed})");
        Assert.Equal(["big.png"], Directory.GetFiles(workspace.Root).Select(f => Path.GetFileName(f)!).ToArray());
    }

    [Fact]
    public async Task Damaged_images_fail_with_a_friendly_message()
    {
        using var workspace = new TestWorkspace("broken");
        var input = workspace.WriteText("broken.png", "definitely not a png");
        var error = await Assert.ThrowsAsync<OperationException>(() => workspace.RunAsync(workspace.Operation(Provider, "image.jpg"), [input]));
        Assert.DoesNotContain("Magick", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFiles(workspace.Root, "*.jpg"));
    }

    static void AssertOutputBasics(IOperation op, string output, string baseName)
    {
        Assert.True(File.Exists(output), output);
        Assert.Equal(op.Info.OutputExtension, Path.GetExtension(output));
        Assert.StartsWith(baseName, Path.GetFileName(output), StringComparison.Ordinal);
        Assert.DoesNotContain(".partial", output, StringComparison.Ordinal);
        if (op.Info.Id != "image.pdf")
        {
            var detected = ImageAssert.DetectedFormat(output);
            var expected = ImageTargets.All.Single(t => t.Extension == op.Info.OutputExtension).Format;
            Assert.True(detected == expected || (expected == MagickFormat.Bmp3 && detected is MagickFormat.Bmp or MagickFormat.Bmp3)
                || (expected == MagickFormat.Ico && detected is MagickFormat.Icon or MagickFormat.Ico)
                || (expected == MagickFormat.Avif && detected is MagickFormat.Avif or MagickFormat.Heic)
                || (expected == MagickFormat.Tiff && detected is MagickFormat.Tiff or MagickFormat.Tif),
                $"{op.Info.Id}: expected {expected}, file is {detected}");
        }
    }

    static MagickImage ReadLargest(string path)
    {
        using var frames = new MagickImageCollection(path);
        return (MagickImage)frames.OrderByDescending(f => f.Width * f.Height).First().Clone();
    }
}
