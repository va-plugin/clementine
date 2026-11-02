using Clementine.Core;
using Clementine.Engines.Images.Metadata;
using Clementine.Engines.Images.Tests.Fixtures;
using Clementine.TestSupport;
using ImageMagick;
using ImageMagick.Drawing;

namespace Clementine.Engines.Images.Tests;

public class ToolTests
{
    static readonly ImageOperationProvider Provider = new();

    static async Task<string> RunSingle(TestWorkspace workspace, string id, string input, IReadOnlyDictionary<string, object?>? options = null)
    {
        var result = await workspace.RunAsync(workspace.Operation(Provider, id), [input], options);
        return Assert.Single(result.Outputs);
    }

    // ---------- Resize ----------

    [Theory]
    [InlineData("image.resize.50", 160u, 120u, "photo (50%).jpg")]
    [InlineData("image.resize.25", 80u, 60u, "photo (25%).jpg")]
    public async Task Resize_presets_scale_and_keep_jpeg_quality(string id, uint width, uint height, string name)
    {
        using var workspace = new TestWorkspace("resize");
        var input = ImageFixtures.Photo(workspace.Root, "photo.jpg", 320, 240, quality: 88);
        var output = await RunSingle(workspace, id, input);
        Assert.Equal(name, Path.GetFileName(output));
        using var image = new MagickImage(output);
        Assert.Equal((width, height), (image.Width, image.Height));
        Assert.InRange(image.Quality, 86u, 90u);
    }

    [Fact]
    public async Task Max_1920_shrinks_the_longest_side_and_leaves_small_images_alone()
    {
        using var workspace = new TestWorkspace("resize-max");
        var big = ImageFixtures.Photo(workspace.Root, "wide.png", 3000, 1000);
        using (var image = new MagickImage(await RunSingle(workspace, "image.resize.max1920", big)))
            Assert.Equal((1920u, 640u), (image.Width, image.Height));

        var small = ImageFixtures.Photo(workspace.Root, "small.png", 800, 600);
        var result = await workspace.RunAsync(workspace.Operation(Provider, "image.resize.max1920"), [small]);
        using (var image = new MagickImage(Assert.Single(result.Outputs)))
            Assert.Equal((800u, 600u), (image.Width, image.Height));
        Assert.Contains(result.Warnings, w => w.Contains("unchanged", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("width", 100, 0, 100u, 75u)]
    [InlineData("height", 0, 60, 80u, 60u)]
    [InlineData("fit", 100, 100, 100u, 75u)]
    [InlineData("exact", 50, 50, 50u, 50u)]
    public async Task Custom_resize_modes(string mode, double width, double height, uint expectedW, uint expectedH)
    {
        using var workspace = new TestWorkspace("resize-custom");
        var input = ImageFixtures.Photo(workspace.Root, "p.png", 320, 240);
        var output = await RunSingle(workspace, "image.resize.custom", input,
            new Dictionary<string, object?> { ["mode"] = mode, ["width"] = width, ["height"] = height });
        using var image = new MagickImage(output);
        Assert.Equal((expectedW, expectedH), (image.Width, image.Height));
    }

    [Fact]
    public async Task Resize_defaults_come_from_the_image_and_animation_survives()
    {
        using var workspace = new TestWorkspace("resize-anim");
        var gif = ImageFixtures.AnimatedGif(workspace.Root);
        var defaults = await workspace.Operation(Provider, "image.resize.custom").GetDynamicDefaultsAsync([InputFile.FromPath(gif)], default);
        Assert.Equal(20.0, defaults.GetDouble("width", 0));
        using var frames = new MagickImageCollection(await RunSingle(workspace, "image.resize.50", gif));
        Assert.Equal(3, frames.Count);
        Assert.All(frames, f => Assert.Equal((20u, 15u), (f.Width, f.Height)));
    }

    [Fact]
    public async Task Tools_on_formats_we_cannot_write_fall_back_to_png_with_a_warning()
    {
        using var workspace = new TestWorkspace("resize-psd");
        var psd = PsdFixtures.Write(workspace.Path("design.psd"), PsdTests.TransparentDesign());
        var result = await workspace.RunAsync(workspace.Operation(Provider, "image.resize.50"), [psd]);
        var output = Assert.Single(result.Outputs);
        Assert.Equal("design (50%).png", Path.GetFileName(output));
        Assert.Contains(result.Warnings, w => w.Contains("PNG", StringComparison.Ordinal));
        using var image = new MagickImage(output);
        Assert.Equal((32u, 24u), (image.Width, image.Height));
        Assert.True(image.HasAlpha);
    }

    // ---------- Compress ----------

    [Fact]
    public async Task Compress_smaller_makes_jpeg_and_png_smaller()
    {
        using var workspace = new TestWorkspace("compress");
        var jpeg = ImageFixtures.Photo(workspace.Root, "photo.jpg", 640, 480, quality: 95);
        var smallerJpeg = await RunSingle(workspace, "image.compress.smaller", jpeg);
        Assert.Equal("photo (compressed).jpg", Path.GetFileName(smallerJpeg));
        Assert.True(new FileInfo(smallerJpeg).Length < new FileInfo(jpeg).Length * 0.8);

        var png = ImageFixtures.Photo(workspace.Root, "photo.png", 320, 240);
        var smallerPng = await RunSingle(workspace, "image.compress.smaller", png);
        Assert.True(new FileInfo(smallerPng).Length < new FileInfo(png).Length * 0.8);
        using var image = new MagickImage(smallerPng);
        Assert.True(image.TotalColors <= 256);
        Assert.Equal((320u, 240u), (image.Width, image.Height));
    }

    [Theory]
    [InlineData("photo.jpg", 0.35)]
    [InlineData("photo.jpg", 0.12)]
    [InlineData("photo.webp", 0.4)]
    [InlineData("photo.webp", 0.6)]
    [InlineData("photo.avif", 0.5)]
    public async Task Compress_to_target_size_lands_within_ten_percent(string name, double fraction)
    {
        using var workspace = new TestWorkspace("compress-target");
        var input = ImageFixtures.Photo(workspace.Root, name, 640, 480, quality: 95);
        var targetKb = Math.Round(new FileInfo(input).Length * fraction / 1024);
        var output = await RunSingle(workspace, "image.compress.target", input, new Dictionary<string, object?> { ["size"] = targetKb });
        var actualKb = new FileInfo(output).Length / 1024.0;
        Assert.True(actualKb >= targetKb * 0.9 && actualKb <= targetKb, $"target {targetKb} KB, got {actualKb:0.0} KB");
        Assert.Equal(Path.GetExtension(name), Path.GetExtension(output));
        using var image = new MagickImage(output);
        Assert.Equal((640u, 480u), (image.Width, image.Height));
    }

    [Fact]
    public async Task Compress_png_to_target_size_quantises_and_then_shrinks()
    {
        using var workspace = new TestWorkspace("compress-png");
        var input = ImageFixtures.Photo(workspace.Root, "photo.png", 640, 480);
        var lossless = new FileInfo(input).Length;
        var halfKb = Math.Round(lossless * 0.5 / 1024);
        var output = await RunSingle(workspace, "image.compress.target", input, new Dictionary<string, object?> { ["size"] = halfKb });
        Assert.InRange(new FileInfo(output).Length / 1024.0, halfKb * 0.3, halfKb);
        using (var image = new MagickImage(output))
            Assert.True(image.TotalColors <= 256);

        var tinyKb = Math.Round(lossless * 0.05 / 1024);
        var result = await workspace.RunAsync(workspace.Operation(Provider, "image.compress.target"), [input], new Dictionary<string, object?> { ["size"] = tinyKb });
        Assert.True(new FileInfo(Assert.Single(result.Outputs)).Length / 1024.0 <= tinyKb);
        Assert.Contains(result.Warnings, w => w.Contains("dimensions were reduced", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Compress_to_a_size_larger_than_the_file_explains_itself()
    {
        using var workspace = new TestWorkspace("compress-already");
        var input = ImageFixtures.Photo(workspace.Root, "tiny.jpg", 64, 48);
        var error = await Assert.ThrowsAsync<OperationException>(() =>
            workspace.RunAsync(workspace.Operation(Provider, "image.compress.target"), [input], new Dictionary<string, object?> { ["size"] = 5000.0 }));
        Assert.Contains("already smaller", error.Message, StringComparison.Ordinal);
    }

    // ---------- Crop ----------

    [Theory]
    [InlineData("image.crop.square", 240u, 240u, "1x1")]
    [InlineData("image.crop.16x9", 320u, 180u, "16x9")]
    [InlineData("image.crop.9x16", 135u, 240u, "9x16")]
    [InlineData("image.crop.4x5", 192u, 240u, "4x5")]
    public async Task Aspect_crops_keep_the_largest_centred_area(string id, uint width, uint height, string suffix)
    {
        using var workspace = new TestWorkspace("crop");
        var input = ImageFixtures.Photo(workspace.Root, "p.png", 320, 240);
        var output = await RunSingle(workspace, id, input);
        Assert.Equal($"p ({suffix}).png", Path.GetFileName(output));
        using var image = new MagickImage(output);
        Assert.Equal((width, height), (image.Width, image.Height));
        Assert.Equal(0, image.Page.X);
    }

    [Fact]
    public async Task Trim_borders_removes_plain_margins_and_says_so_when_there_are_none()
    {
        using var workspace = new TestWorkspace("trim");
        var path = workspace.Path("scan.png");
        using (var image = new MagickImage(MagickColors.White, 100, 80))
        {
            image.Draw(new Drawables().FillColor(MagickColors.Red).Rectangle(20, 10, 69, 49));
            image.Write(path);
        }
        using (var trimmed = new MagickImage(await RunSingle(workspace, "image.crop.trim", path)))
        {
            Assert.Equal((50u, 40u), (trimmed.Width, trimmed.Height));
            ImageAssert.Near(ImageAssert.Pixel(trimmed, 0, 0), (255, 0, 0, 255), 2);
        }

        var full = ImageFixtures.Photo(workspace.Root, "full.png", 64, 64);
        var error = await Assert.ThrowsAsync<OperationException>(() => workspace.RunAsync(workspace.Operation(Provider, "image.crop.trim"), [full]));
        Assert.Contains("no plain borders", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Custom_crop_to_exact_size_with_anchor()
    {
        using var workspace = new TestWorkspace("crop-custom");
        var input = ImageFixtures.RotatedJpeg(workspace.Root); // displayed 32×64, red top half
        var output = await RunSingle(workspace, "image.crop.custom", input,
            new Dictionary<string, object?> { ["mode"] = "size", ["width"] = 32.0, ["height"] = 20.0, ["anchor"] = "top" });
        using var image = new MagickImage(output);
        Assert.Equal((32u, 20u), (image.Width, image.Height));
        ImageAssert.Near(ImageAssert.Pixel(image, 16, 10), (255, 0, 0, 255), 30, "top area kept");
    }

    // ---------- Rotate ----------

    [Theory]
    [InlineData("image.rotate.right", 32u, 64u, 16, 8, 255, 0)]      // red (left half) ends up on top
    [InlineData("image.rotate.left", 32u, 64u, 16, 56, 255, 0)]      // red at the bottom
    [InlineData("image.rotate.180", 64u, 32u, 56, 16, 255, 0)]       // red on the right
    [InlineData("image.rotate.flip-horizontal", 64u, 32u, 56, 16, 255, 0)]
    [InlineData("image.rotate.flip-vertical", 64u, 32u, 8, 16, 255, 0)]
    public async Task Rotate_and_flip(string id, uint width, uint height, int x, int y, byte red, byte blue)
    {
        using var workspace = new TestWorkspace("rotate");
        var path = workspace.Path("pair.png");
        using (var image = new MagickImage(MagickColors.Blue, 64, 32))
        {
            image.Draw(new Drawables().FillColor(MagickColors.Red).Rectangle(0, 0, 31, 31));
            image.Write(path);
        }
        using var output = new MagickImage(await RunSingle(workspace, id, path));
        Assert.Equal((width, height), (output.Width, output.Height));
        var pixel = ImageAssert.Pixel(output, x, y);
        Assert.Equal(red, pixel.R);
        Assert.Equal(blue, pixel.B);
    }

    // ---------- Adjust ----------

    [Fact]
    public async Task Black_and_white_auto_enhance_and_custom_adjustments()
    {
        using var workspace = new TestWorkspace("adjust");
        var input = ImageFixtures.Photo(workspace.Root, "p.png", 120, 90);
        using (var bw = new MagickImage(await RunSingle(workspace, "image.adjust.bw", input)))
        {
            var (r, g, b, _) = ImageAssert.Pixel(bw, 30, 30);
            Assert.True(r == g && g == b, "greyscale");
        }

        var flat = workspace.Path("flat.png");
        using (var image = new MagickImage("gradient:#606060-#a0a0a0", 64, 64))
            image.Write(flat);
        using (var original = new MagickImage(flat))
        using (var enhanced = new MagickImage(await RunSingle(workspace, "image.adjust.auto", flat)))
        {
            Assert.True(enhanced.Statistics().Composite().StandardDeviation > original.Statistics().Composite().StandardDeviation * 1.5, "contrast stretched");
        }

        using (var original = new MagickImage(input))
        using (var brighter = new MagickImage(await RunSingle(workspace, "image.adjust.custom", input, new Dictionary<string, object?> { ["brightness"] = 40.0 })))
            Assert.True(brighter.Statistics().Composite().Mean > original.Statistics().Composite().Mean * 1.1);
    }

    // ---------- Transparent background ----------

    [Fact]
    public async Task Transparent_background_clears_only_the_edge_connected_background()
    {
        using var workspace = new TestWorkspace("transparent");
        var path = workspace.Path("product.jpg");
        using (var image = new MagickImage(MagickColors.White, 80, 80))
        {
            image.Draw(new Drawables().FillColor(MagickColors.Red).Circle(40, 40, 40, 15));
            image.Draw(new Drawables().FillColor(MagickColors.White).Circle(40, 40, 40, 36)); // white highlight inside
            image.Quality = 90;
            image.Write(path);
        }
        var output = await RunSingle(workspace, "image.background.remove", path);
        Assert.Equal("product (transparent).png", Path.GetFileName(output));
        using var result = new MagickImage(output);
        Assert.Equal(0, ImageAssert.Pixel(result, 2, 2).A);
        Assert.Equal(0, ImageAssert.Pixel(result, 77, 77).A);
        ImageAssert.Near(ImageAssert.Pixel(result, 40, 25), (255, 0, 0, 255), 30, "subject kept");
        Assert.Equal(255, ImageAssert.Pixel(result, 40, 40).A); // white inside the subject stays
    }

    // ---------- Metadata ----------

    [Fact]
    public async Task Remove_all_metadata_from_jpeg_is_lossless()
    {
        using var workspace = new TestWorkspace("meta-all");
        var input = ImageFixtures.JpegWithLocation(workspace.Root);
        var output = await RunSingle(workspace, "image.metadata.remove", input);
        Assert.Equal("located (no metadata).jpg", Path.GetFileName(output));
        using var cleaned = new MagickImage(output);
        using var original = new MagickImage(input);
        Assert.Null(cleaned.GetExifProfile());
        Assert.Null(cleaned.GetXmpProfile());
        Assert.True(string.IsNullOrEmpty(cleaned.Comment));
        Assert.Equal(0, cleaned.Compare(original, ErrorMetric.Absolute)); // identical pixels: not re-encoded
        Assert.True(new FileInfo(output).Length < new FileInfo(input).Length);
    }

    [Fact]
    public async Task Remove_location_keeps_everything_else()
    {
        using var workspace = new TestWorkspace("meta-gps");
        var input = ImageFixtures.JpegWithLocation(workspace.Root);
        var output = await RunSingle(workspace, "image.metadata.location", input);
        Assert.Equal("located (no location).jpg", Path.GetFileName(output));
        using var cleaned = new MagickImage(output);
        using var original = new MagickImage(input);
        var exif = cleaned.GetExifProfile();
        Assert.NotNull(exif);
        Assert.False(LocationScrubber.HasGps(exif));
        Assert.Equal("TestCam", exif.GetValue(ExifTag.Make)?.Value);
        Assert.Equal("2024:06:01 12:00:00", exif.GetValue(ExifTag.DateTimeOriginal)?.Value);
        var xmp = System.Text.Encoding.UTF8.GetString(cleaned.GetXmpProfile()!.ToByteArray()!);
        Assert.DoesNotContain("GPS", xmp, StringComparison.Ordinal);
        Assert.Contains("Rating", xmp, StringComparison.Ordinal);
        Assert.Equal("secret comment", cleaned.Comment);
        Assert.Equal(0, cleaned.Compare(original, ErrorMetric.Absolute));
    }

    [Fact]
    public async Task Remove_metadata_turns_rotated_photos_upright_first()
    {
        using var workspace = new TestWorkspace("meta-rotated");
        var input = ImageFixtures.RotatedJpeg(workspace.Root);
        using var cleaned = new MagickImage(await RunSingle(workspace, "image.metadata.remove", input));
        Assert.Equal((32u, 64u), (cleaned.Width, cleaned.Height));
        Assert.Null(cleaned.GetExifProfile());
        ImageAssert.Near(ImageAssert.Pixel(cleaned, 16, 8), (255, 0, 0, 255), 30, "still upright");
    }

    [Fact]
    public async Task Remove_metadata_from_png_and_webp()
    {
        using var workspace = new TestWorkspace("meta-png");
        foreach (var (name, format) in new[] { ("tagged.png", MagickFormat.Png), ("tagged.webp", MagickFormat.WebP) })
        {
            var path = workspace.Path(name);
            using (var image = new MagickImage(MagickColors.Orange, 40, 30))
            {
                var exif = new ExifProfile();
                exif.SetValue(ExifTag.Make, "TestCam");
                exif.SetValue(ExifTag.GPSLatitudeRef, "N");
                exif.SetValue(ExifTag.GPSLatitude, [new Rational(10, 1), new Rational(0, 1), new Rational(0, 1)]);
                image.SetProfile(exif);
                image.Write(path, format);
            }
            using (var check = new MagickImage(path))
                Assert.NotNull(check.GetExifProfile());

            using var located = new MagickImage(await RunSingle(workspace, "image.metadata.location", path));
            Assert.False(LocationScrubber.HasGps(located.GetExifProfile()));
            Assert.Equal("TestCam", located.GetExifProfile()?.GetValue(ExifTag.Make)?.Value);

            using var cleaned = new MagickImage(await RunSingle(workspace, "image.metadata.remove", path));
            Assert.Null(cleaned.GetExifProfile());
            Assert.Equal((40u, 30u), (cleaned.Width, cleaned.Height));
        }
    }

    [Fact]
    public async Task Remove_location_explains_when_there_is_none()
    {
        using var workspace = new TestWorkspace("meta-none");
        var input = ImageFixtures.Photo(workspace.Root, "plain.jpg");
        var error = await Assert.ThrowsAsync<OperationException>(() => workspace.RunAsync(workspace.Operation(Provider, "image.metadata.location"), [input]));
        Assert.Contains("no location", error.Message, StringComparison.Ordinal);
    }

    // ---------- Info ----------

    [Fact]
    public async Task Info_reports_size_orientation_camera_and_location()
    {
        using var workspace = new TestWorkspace("info");
        var input = ImageFixtures.RotatedJpeg(workspace.Root);
        var result = await workspace.RunAsync(workspace.Operation(Provider, "image.info"), [input]);
        Assert.Empty(result.Outputs);
        var text = result.Text!;
        Assert.Contains("32 × 64 px", text, StringComparison.Ordinal);
        Assert.Contains("rotated 90° clockwise", text, StringComparison.Ordinal);
        Assert.Contains("Camera: TestCam Model 1", text, StringComparison.Ordinal);
        Assert.Contains("Location: 52.375000° N, 4.883333° E", text, StringComparison.Ordinal);
        Assert.Contains("JPEG quality: about 95", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Info_lists_psd_layers()
    {
        using var workspace = new TestWorkspace("info-psd");
        var spec = new PsdSpec(64, 48,
        [
            new LayerSpec("Background", "#FFFFFF", 0, 0, 64, 48),
            new LayerSpec("Logo", "#FF0000", 0, 0, 10, 10) { Hidden = true, Opacity = 128 },
            new LayerSpec("Shade", "#000000", 0, 0, 10, 10) { BlendKey = "mul " },
        ]) { MarkNoRealMergedData = true, BlankComposite = true };
        var input = PsdFixtures.Write(workspace.Path("doc.psd"), spec);
        var text = (await workspace.RunAsync(workspace.Operation(Provider, "image.info"), [input])).Text!;
        Assert.Contains("Layers: 3", text, StringComparison.Ordinal);
        Assert.Contains("Logo (hidden, 50 % opacity)", text, StringComparison.Ordinal);
        Assert.Contains("Shade (Multiply)", text, StringComparison.Ordinal);
        Assert.Contains("Maximize compatibility", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Info_reports_transparency_only_when_a_pixel_is_see_through()
    {
        using var workspace = new TestWorkspace("info-alpha");
        async Task<string> Colour(string path)
        {
            var text = (await workspace.RunAsync(workspace.Operation(Provider, "image.info"), [path])).Text!;
            return text.Split('\n').Single(l => l.StartsWith("Colour:", StringComparison.Ordinal)).Trim();
        }

        // A PSD whose composite carries an alpha channel that is opaque everywhere.
        var opaquePsd = PsdFixtures.Write(workspace.Path("opaque.psd"), new PsdSpec(20, 20, [new LayerSpec("White", "#FFFFFF", 0, 0, 20, 20)]));
        using (var header = new MagickImage())
        {
            header.Ping(opaquePsd);
            Assert.True(header.HasAlpha, "precondition: the composite has an alpha channel");
        }
        Assert.EndsWith("no transparency", await Colour(opaquePsd), StringComparison.Ordinal);

        // The same for an RGBA PNG of a fully opaque picture.
        var opaquePng = workspace.Path("opaque.png");
        using (var image = new MagickImage(MagickColors.Orange, 16, 16))
        {
            image.Alpha(AlphaOption.Set);
            image.Write(opaquePng, MagickFormat.Png32); // always RGBA
        }
        using (var header = new MagickImage())
        {
            header.Ping(opaquePng);
            Assert.True(header.HasAlpha, "precondition: the PNG is RGBA");
        }
        Assert.EndsWith("no transparency", await Colour(opaquePng), StringComparison.Ordinal);

        Assert.EndsWith("with transparency", await Colour(PsdFixtures.Write(workspace.Path("design.psd"), PsdTests.TransparentDesign())), StringComparison.Ordinal);
        Assert.EndsWith("with transparency", await Colour(ImageFixtures.TransparentPng(workspace.Root)), StringComparison.Ordinal);
        Assert.EndsWith("no transparency", await Colour(ImageFixtures.Photo(workspace.Root, "photo.jpg")), StringComparison.Ordinal);
    }

    // ---------- QR / barcode ----------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Qr_code_round_trip(bool transparent)
    {
        using var workspace = new TestWorkspace("qr");
        const string payload = "https://example.com/clementine?ünï=東京&n=42";
        var path = workspace.Path("code.png");
        var writer = new ZXing.BarcodeWriterPixelData
        {
            Format = ZXing.BarcodeFormat.QR_CODE,
            Options = new ZXing.QrCode.QrCodeEncodingOptions { Width = 240, Height = 240, Margin = 2, CharacterSet = "UTF-8" },
        };
        var pixels = writer.Write(payload);
        using (var image = new MagickImage())
        {
            image.ReadPixels(pixels.Pixels, new PixelReadSettings((uint)pixels.Width, (uint)pixels.Height, StorageType.Char, PixelMapping.BGRA));
            if (transparent)
                image.Transparent(MagickColors.White);
            image.Write(path, MagickFormat.Png);
        }
        var result = await workspace.RunAsync(workspace.Operation(Provider, "image.barcode.read"), [path]);
        Assert.Equal(payload, result.Text);
        Assert.Equal("QR code contents", result.TextTitle);
    }

    [Fact]
    public async Task No_code_found_is_a_friendly_failure()
    {
        using var workspace = new TestWorkspace("qr-none");
        var input = ImageFixtures.Photo(workspace.Root, "nothing.png", 200, 150);
        var error = await Assert.ThrowsAsync<OperationException>(() => workspace.RunAsync(workspace.Operation(Provider, "image.barcode.read"), [input]));
        Assert.Equal("No QR code or barcode was found in this image.", error.Message);
    }

    // ---------- Images → PDF ----------

    [Fact]
    public async Task Images_to_pdf_sorts_naturally_and_lays_out_pages()
    {
        using var workspace = new TestWorkspace("topdf");
        var first = ImageFixtures.Photo(workspace.Root, "img1.jpg", 100, 50);
        var second = ImageFixtures.TransparentPng(workspace.Root, "img2.png"); // 64×48
        var tenth = ImageFixtures.Photo(workspace.Root, "img10.png", 80, 80);
        var op = workspace.Operation(Provider, "image.pdf.combine");
        Assert.Equal(InputMode.Combined, op.InputMode);

        var result = await workspace.RunAsync(op, [tenth, first, second], new Dictionary<string, object?> { ["paper"] = "a4", ["margin"] = 10.0 });
        var output = Assert.Single(result.Outputs);
        Assert.Equal("img1.pdf", Path.GetFileName(output));
        var pdf = PdfInspector.Inspect(output);
        Assert.Equal(3, pdf.Pages);
        Assert.Equal([(100, 50), (64, 48), (80, 80)], pdf.Images.Select(i => (i.Width, i.Height)).ToArray());
        Assert.Equal("DCTDecode", pdf.Images[0].Filter);
        Assert.True(pdf.Images[1].HasMask);
        Assert.Equal((841.89, 595.276), pdf.MediaBoxes[0]); // landscape image → landscape page
        Assert.Equal((595.276, 841.89), pdf.MediaBoxes[2]);

        var fit = await workspace.RunAsync(op, [first, second], new Dictionary<string, object?> { ["paper"] = "fit" });
        var fitted = PdfInspector.Inspect(Assert.Single(fit.Outputs));
        Assert.Equal([(100.0, 50.0), (64.0, 48.0)], fitted.MediaBoxes.ToArray());
    }

    // ---------- Collage ----------

    [Fact]
    public async Task Collage_builds_an_even_grid()
    {
        using var workspace = new TestWorkspace("collage");
        var inputs = new[] { "#FF0000", "#00FF00", "#0000FF", "#FFFF00" }.Select((color, i) =>
        {
            var path = workspace.Path($"tile{i + 1}.png");
            using var image = new MagickImage(new MagickColor(color), 100, 100);
            image.Write(path);
            return path;
        }).ToList();
        var op = workspace.Operation(Provider, "image.collage");
        var result = await workspace.RunAsync(op, inputs, new Dictionary<string, object?> { ["cell"] = 100.0, ["spacing"] = 10.0 });
        var output = Assert.Single(result.Outputs);
        Assert.Equal("tile1 (collage).jpg", Path.GetFileName(output));
        using var collage = new MagickImage(output);
        Assert.Equal((230u, 230u), (collage.Width, collage.Height));
        ImageAssert.Near(ImageAssert.Pixel(collage, 5, 5), (255, 255, 255, 255), 8, "spacing");
        ImageAssert.Near(ImageAssert.Pixel(collage, 60, 60), (255, 0, 0, 255), 12, "tile 1");
        ImageAssert.Near(ImageAssert.Pixel(collage, 170, 60), (0, 255, 0, 255), 12, "tile 2");
        ImageAssert.Near(ImageAssert.Pixel(collage, 60, 170), (0, 0, 255, 255), 12, "tile 3");
        ImageAssert.Near(ImageAssert.Pixel(collage, 170, 170), (255, 255, 0, 255), 12, "tile 4");
        Assert.False(op.AcceptsAll([InputFile.FromPath(inputs[0])]), "a collage needs at least two images");
    }

    // ---------- Export layers ----------

    [Fact]
    public async Task Export_layers_writes_numbered_full_canvas_pngs()
    {
        using var workspace = new TestWorkspace("layers");
        var spec = new PsdSpec(64, 48,
        [
            new LayerSpec("Background", "#FFFFFF", 0, 0, 64, 48),
            new LayerSpec("Red half", "#FF0000", 0, 0, 32, 24) { Opacity = 128 },
            new LayerSpec("Hidden: green?", "#00FF00", 5, 30, 10, 10) { Hidden = true },
        ]);
        var input = PsdFixtures.Write(workspace.Path("art.psd"), spec);
        var result = await workspace.RunAsync(workspace.Operation(Provider, "image.layers.export"), [input]);
        var folder = Assert.Single(result.Outputs);
        Assert.Equal("art (layers)", Path.GetFileName(folder));
        var files = Directory.GetFiles(folder).Select(f => Path.GetFileName(f)!).Order().ToArray();
        Assert.Equal(["01 Background.png", "02 Red half.png", "03 Hidden_ green_.png"], files);
        Assert.Contains(result.Warnings, w => w.Contains("hidden", StringComparison.Ordinal));

        using var red = new MagickImage(Path.Combine(folder, "02 Red half.png"));
        Assert.Equal((64u, 48u), (red.Width, red.Height));
        var pixel = ImageAssert.Pixel(red, 5, 5);
        Assert.Equal(255, pixel.R);
        Assert.InRange(pixel.A, 120, 136);
        Assert.Equal(0, ImageAssert.Pixel(red, 40, 40).A);

        using var green = new MagickImage(Path.Combine(folder, "03 Hidden_ green_.png"));
        ImageAssert.Near(ImageAssert.Pixel(green, 8, 33), (0, 255, 0, 255), 0, "green layer in place");
    }

    [Fact]
    public async Task Export_layers_trimmed_keeps_each_layer_at_its_own_size()
    {
        using var workspace = new TestWorkspace("layers-trim");
        var input = PsdFixtures.Write(workspace.Path("art.psd"), PsdTests.TransparentDesign());
        var folder = await RunSingle(workspace, "image.layers.export.trimmed", input);
        using var red = new MagickImage(Path.Combine(folder, "01 Red.png"));
        Assert.Equal((32u, 24u), (red.Width, red.Height));
        Assert.Equal(0, red.Page.X);
    }

    [Fact]
    public async Task Export_layers_explains_when_there_are_none_and_exports_tiff_pages()
    {
        using var workspace = new TestWorkspace("layers-none");
        var flat = ImageFixtures.Photo(workspace.Root, "flat.tif", 40, 30);
        var error = await Assert.ThrowsAsync<OperationException>(() => workspace.RunAsync(workspace.Operation(Provider, "image.layers.export"), [flat]));
        Assert.Contains("no layers", error.Message, StringComparison.Ordinal);

        var pages = workspace.Path("pages.tif");
        using (var collection = new MagickImageCollection())
        {
            collection.Add(new MagickImage(MagickColors.Red, 20, 20));
            collection.Add(new MagickImage(MagickColors.Blue, 30, 30));
            collection.Write(pages, MagickFormat.Tiff);
        }
        var folder = await RunSingle(workspace, "image.layers.export", pages);
        Assert.Equal(["01 Page 1.png", "02 Page 2.png"], Directory.GetFiles(folder).Select(f => Path.GetFileName(f)!).Order().ToArray());
    }
}
