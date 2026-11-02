using System.Buffers.Binary;
using System.Text;
using Clementine.Core;
using Clementine.Engines.Images.Imaging;
using Clementine.Engines.Images.Tests.Fixtures;
using Clementine.TestSupport;
using ImageMagick;

namespace Clementine.Engines.Images.Tests;

/// <summary>Regressions found by reviewing real-world files (Photoshop PSDs, wide-gamut photos, giant images).</summary>
public class ReviewRegressionTests
{
    static readonly ImageOperationProvider Provider = new();

    // ---------- PNG speed: zlib level ----------

    [Fact]
    public async Task Sixteen_bit_png_conversions_use_a_fast_zlib_level_not_level_9()
    {
        using var workspace = new TestWorkspace("png-level");
        var input = workspace.Path("source.tif");
        using (var image = new MagickImage("gradient:#FF8A1F-#202060", 64, 64))
        {
            image.Depth = 16;
            image.Write(input, MagickFormat.Tiff);
        }
        var output = Assert.Single((await workspace.RunAsync(workspace.Operation(Provider, "image.png"), [input])).Outputs);
        Assert.Equal(1, ZlibLevelOfFirstIdat(output)); // zlib level 4 → FLEVEL "fast" (level 9 was "maximum", 3)
    }

    [Theory]
    [InlineData(8, false, "7")]
    [InlineData(16, false, "4")]
    [InlineData(8, true, "9")]
    public void Png_zlib_level_trades_size_for_speed_sensibly(int depth, bool maxCompression, string expected)
    {
        // Level 7 and 9 share the same zlib header bits, so check the define the PNG writer receives.
        using var image = new MagickImage(MagickColors.Orange, 4, 4) { Depth = (uint)depth };
        ImageEncoder.ConfigurePng(image, maxCompression);
        Assert.Equal(expected, image.Settings.GetDefine(MagickFormat.Png, "compression-level"));
    }

    [Fact]
    public async Task Compress_smaller_still_squeezes_png_at_level_9()
    {
        using var workspace = new TestWorkspace("png-level-compress");
        var input = ImageFixtures.TransparentPng(workspace.Root);
        var output = Assert.Single((await workspace.RunAsync(workspace.Operation(Provider, "image.compress.smaller"), [input])).Outputs);
        Assert.Equal(3, ZlibLevelOfFirstIdat(output));
    }

    /// <summary>The FLEVEL bits of the zlib stream header in the first IDAT (0 fastest … 3 maximum).</summary>
    static int ZlibLevelOfFirstIdat(string png)
    {
        var bytes = File.ReadAllBytes(png);
        var at = 8;
        while (at + 8 <= bytes.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(at));
            var type = Encoding.ASCII.GetString(bytes, at + 4, 4);
            if (type == "IDAT")
                return bytes[at + 9] >> 6;
            at += 12 + length;
        }
        throw new InvalidDataException("No IDAT chunk.");
    }

    // ---------- PSD composite ----------

    [Fact]
    public async Task A_uniform_composite_that_photoshop_marks_as_real_is_trusted()
    {
        using var workspace = new TestWorkspace("psd-real-uniform");
        // A white composite over coloured layers, with "version info" saying the merged data is real: what
        // Photoshop shows (e.g. a white fill or adjustment layer on top), so it must not be "rebuilt" into the layers.
        var spec = new PsdSpec(32, 32,
        [
            new LayerSpec("Red", "#FF0000", 0, 0, 32, 32),
            new LayerSpec("Blue", "#0000FF", 8, 8, 8, 8),
        ]) { BlankComposite = true, MarkRealMergedData = true };
        var path = PsdFixtures.Write(workspace.Path("artboard.psd"), spec);
        var result = await workspace.RunAsync(workspace.Operation(Provider, "image.png"), [path]);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("rebuilt", StringComparison.Ordinal));
        using var image = new MagickImage(Assert.Single(result.Outputs));
        ImageAssert.Near(ImageAssert.Pixel(image, 2, 2), (255, 255, 255, 255), 0, "the real composite");
        ImageAssert.Near(ImageAssert.Pixel(image, 10, 10), (255, 255, 255, 255), 0, "the real composite");
    }

    [Fact]
    public async Task A_flat_psd_with_a_saved_selection_channel_stays_opaque()
    {
        // Photoshop: a flattened document (Background only) with a saved selection "Alpha 1" has 4 channels and no
        // layer section. The extra channel is a mask, not transparency: Photoshop shows the image opaque.
        using var workspace = new TestWorkspace("psd-flat-alpha");
        var path = workspace.Path("flat.psd");
        File.WriteAllBytes(path, FlatRgbPsdWithExtraChannel(16, 16));
        var output = Assert.Single((await workspace.RunAsync(workspace.Operation(Provider, "image.png"), [path])).Outputs);
        using var image = new MagickImage(output);
        Assert.True(!image.HasAlpha || image.IsOpaque, "the saved selection must not punch holes into the PNG");
        ImageAssert.Near(ImageAssert.Pixel(image, 1, 1), (200, 100, 50, 255), 0, "pixel inside the unselected half");
        ImageAssert.Near(ImageAssert.Pixel(image, 14, 14), (200, 100, 50, 255), 0, "pixel inside the selected half");
    }

    /// <summary>Hand-written PSD: RGB 8-bit, raw image data, 4 channels (R, G, B, "Alpha 1"), no layers.</summary>
    static byte[] FlatRgbPsdWithExtraChannel(int width, int height)
    {
        using var stream = new MemoryStream();
        void U16(int v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v); stream.Write(b); }
        void U32(long v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, (uint)v); stream.Write(b); }
        stream.Write("8BPS"u8);
        U16(1);
        stream.Write(new byte[6]);
        U16(4);         // channels
        U32(height);
        U32(width);
        U16(8);         // depth
        U16(3);         // RGB
        U32(0);         // colour mode data
        // Image resources: alpha channel names (1006) = "Alpha 1", version info (1057) with hasRealMergedData = 1.
        using var resources = new MemoryStream();
        void Resource(int id, byte[] data)
        {
            resources.Write("8BIM"u8);
            resources.WriteByte((byte)(id >> 8));
            resources.WriteByte((byte)id);
            resources.Write([0, 0]); // empty name, padded
            Span<byte> size = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(size, (uint)data.Length);
            resources.Write(size);
            resources.Write(data);
            if (data.Length % 2 == 1)
                resources.WriteByte(0);
        }
        Resource(1006, [7, .. "Alpha 1"u8]);
        Resource(1057, [0, 0, 0, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0]);
        U32(resources.Length);
        resources.WriteTo(stream);
        U32(0);         // no layer and mask section: a flat document
        U16(0);         // raw image data
        var plane = width * height;
        foreach (var value in new byte[] { 200, 100, 50 })
            stream.Write(Enumerable.Repeat(value, plane).ToArray());
        for (var i = 0; i < plane; i++)
            stream.WriteByte(i < plane / 2 ? (byte)0 : (byte)255); // the saved selection: top half unselected
        return stream.ToArray();
    }

    // ---------- colour profiles for formats that can't keep them ----------

    [Theory]
    [InlineData("image.bmp")]
    [InlineData("image.ico")]
    [InlineData("image.gif")]
    public async Task Wide_gamut_pixels_are_converted_to_srgb_for_formats_without_icc(string id)
    {
        using var workspace = new TestWorkspace("wide-gamut");
        var input = workspace.Path("adobe.png");
        using (var image = new MagickImage(new MagickColor("#1EC878"), 32, 32))
        {
            image.SetProfile(ColorProfiles.AdobeRGB1998);
            image.Write(input, MagickFormat.Png);
        }
        (byte R, byte G, byte B, byte A) expected;
        using (var reference = new MagickImage(input))
        {
            reference.TransformColorSpace(ColorProfiles.SRGB);
            expected = ImageAssert.Pixel(reference, 8, 8);
        }
        Assert.True(Math.Abs(expected.R - 0x1E) > 8 || Math.Abs(expected.G - 0xC8) > 8, "the fixture colour must differ between Adobe RGB and sRGB");

        var output = Assert.Single((await workspace.RunAsync(workspace.Operation(Provider, id), [input])).Outputs);
        using var result = new MagickImage(output);
        ImageAssert.Near(ImageAssert.Pixel(result, 8, 8), expected, 3, "converted to sRGB before the profile is lost");
    }

    // ---------- HEIC ----------

    [Fact]
    public async Task Probed_size_of_a_portrait_iphone_heic_is_portrait()
    {
        // The Resize/Crop dialogs take their defaults from this; libheif already applies "irot".
        using var workspace = new TestWorkspace("heic-probe");
        var input = await HeicFixture.CreateAsync(workspace.Root, "portrait.heic", rotatedLikeAnIPhone: true);
        Assert.Equal(((uint, uint)?)(48u, 64u), ImageProbe.Size(InputFile.FromPath(input)));
    }

    // ---------- giant images ----------

    [Theory]
    [InlineData("image.jpg")]
    [InlineData("image.tiff")]
    [InlineData("image.webp")]
    [InlineData("image.resize.50")]
    [InlineData("image.rotate.right")]
    public async Task A_decompression_bomb_fails_fast_with_a_friendly_message(string id)
    {
        using var workspace = new TestWorkspace("bomb");
        var input = workspace.Path("bomb.png");
        File.WriteAllBytes(input, BombBytes.Value); // 1.6 gigapixels in ~200 KB
        Assert.True(new FileInfo(input).Length < 2_000_000);
        Assert.True(40_000L * 40_000 > MagickRuntime.MaxPixels);

        var started = DateTime.UtcNow;
        var error = await Assert.ThrowsAsync<OperationException>(() => workspace.RunAsync(workspace.Operation(Provider, id), [input]));
        Assert.Contains("too large", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Magick", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10), "refused from the header, before decoding");
        Assert.Equal(["bomb.png"], Directory.GetFiles(workspace.Root).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData(false, "no transparency")]
    [InlineData(true, "with an alpha channel")]
    public async Task Info_on_a_decompression_bomb_reports_its_size_without_decoding_it(bool transparent, string colour)
    {
        using var workspace = new TestWorkspace("bomb-info");
        var input = workspace.Path("bomb.png");
        File.WriteAllBytes(input, transparent ? TransparentBombBytes.Value : BombBytes.Value);

        var started = DateTime.UtcNow;
        var text = (await workspace.RunAsync(workspace.Operation(Provider, "image.info"), [input])).Text!;
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10), "the header is enough");
        Assert.Contains("40000 × 40000 px", text, StringComparison.Ordinal);
        Assert.Contains(colour, text, StringComparison.Ordinal);
    }

    [Fact]
    public void ImageMagick_resource_limits_are_set_once_at_initialisation()
    {
        using var workspace = new TestWorkspace("limits");
        MagickRuntime.EnsureInitialized();
        _ = new ImageOperationProvider().CreateOperations(workspace.Context).ToList(); // initialises again: no change
        Assert.Equal(MagickRuntime.MaxSide, ResourceLimits.Width);
        Assert.Equal(MagickRuntime.MaxSide, ResourceLimits.Height);
        Assert.Equal((ulong)MagickRuntime.MaxPixels, ResourceLimits.Area);
        Assert.Equal(MagickRuntime.DiskLimit, ResourceLimits.Disk);
        var memory = Assert.NotNull(MagickRuntime.MemoryLimit); // .NET knows how much RAM this computer has
        Assert.Equal(memory, ResourceLimits.Memory);

        var ram = (ulong)GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        Assert.InRange(memory, ram / 2, ram * 7 / 10);
        // Room for two full copies of the largest image allowed, yet never the tens of GB a bomb asks for.
        Assert.Equal((ulong)MagickRuntime.MaxPixels * 32, MagickRuntime.DiskLimit);
        Assert.InRange(MagickRuntime.DiskLimit, 8UL << 30, 32UL << 30);
    }

    [Fact]
    public void A_canvas_the_header_check_never_sees_is_refused_by_imagemagick_before_any_cache_is_written()
    {
        // 100 000 × 100 000 RGB is 120 GB of pixel cache: over Area (so no memory) and over Disk on any computer.
        Assert.True(ResourceLimits.Disk < 100_000UL * 100_000 * 12, "precondition: such a cache can never be allowed");
        var started = DateTime.UtcNow;
        var error = Assert.ThrowsAny<MagickException>(() => new MagickImage(MagickColors.Black, 100_000, 100_000));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5), "refused up front");
        Assert.True(MagickErrors.IsOverLimit(error), error.Message);
        using var workspace = new TestWorkspace("bomb-canvas");
        var input = InputFile.FromPath(workspace.WriteText("canvas.png", ""));
        Assert.Equal("This image is too large to process on this computer.", MagickErrors.Reading(error, input).Message);
    }

    [Theory]
    [InlineData(400_000, 1)]
    [InlineData(1, 400_000)]
    public async Task A_side_longer_than_imagemagicks_limit_is_refused_with_a_friendly_message(int width, int height)
    {
        // Only 400 000 pixels (far under MaxPixels), so ResourceLimits.Width/Height is what refuses it.
        using var workspace = new TestWorkspace("bomb-side");
        var input = workspace.Path("strip.png");
        File.WriteAllBytes(input, PngFixtures.Bomb(width, height));
        Assert.True((ulong)Math.Max(width, height) > MagickRuntime.MaxSide);

        var error = await Assert.ThrowsAsync<OperationException>(() => workspace.RunAsync(workspace.Operation(Provider, "image.jpg"), [input]));
        Assert.Equal("This image is too large to process on this computer.", error.Message);
        Assert.Equal(["strip.png"], Directory.GetFiles(workspace.Root).Select(Path.GetFileName));
    }

    [Fact]
    public void A_100_megapixel_photo_is_within_the_limit_on_any_computer() =>
        Assert.True(MagickRuntime.MaxPixels >= 250_000_000);

    static readonly Lazy<byte[]> BombBytes = new(() => PngFixtures.Bomb(40_000, 40_000));

    static readonly Lazy<byte[]> TransparentBombBytes = new(() => PngFixtures.Bomb(40_000, 40_000, transparent: true));
}
