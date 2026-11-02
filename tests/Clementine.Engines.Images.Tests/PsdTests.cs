using Clementine.Core;
using Clementine.Engines.Images.Imaging;
using Clementine.Engines.Images.Psd;
using Clementine.Engines.Images.Tests.Fixtures;
using Clementine.TestSupport;
using ImageMagick;

namespace Clementine.Engines.Images.Tests;

public class PsdTests
{
    static readonly ImageOperationProvider Provider = new();

    /// <summary>64×48 transparent document: red top-left quadrant, blue bottom-right quadrant.</summary>
    public static PsdSpec TransparentDesign() => new(64, 48,
    [
        new LayerSpec("Red", "#FF0000", 0, 0, 32, 24),
        new LayerSpec("Blue", "#0000FF", 32, 24, 32, 24),
    ]);

    /// <summary>White background, a 50 % red layer, a hidden green layer and a yellow Multiply layer.</summary>
    static PsdSpec Semantics() => new(64, 48,
    [
        new LayerSpec("Background", "#FFFFFF", 0, 0, 64, 48),
        new LayerSpec("Red half", "#FF0000", 0, 0, 32, 24) { Opacity = 128 },
        new LayerSpec("Hidden green", "#00FF00", 5, 30, 10, 10) { Hidden = true },
        new LayerSpec("Yellow multiply", "#FFFF00", 20, 10, 16, 16) { BlendKey = "mul " },
    ]);

    [Fact]
    public void Structure_is_parsed_from_the_file()
    {
        using var workspace = new TestWorkspace("psd-parse");
        var spec = Semantics() with { Layers = [.. Semantics().Layers, new LayerSpec("Ünïcødé 東京", "#000000", 1, 1, 2, 2)], MarkRealMergedData = true };
        var path = PsdFixtures.Write(workspace.Path("doc.psd"), spec);
        var document = PsdDocument.TryRead(path);
        Assert.NotNull(document);
        Assert.Equal(64, document.Width);
        Assert.Equal(48, document.Height);
        Assert.Equal(PsdColorMode.Rgb, document.ColorMode);
        Assert.True(document.HasRealMergedData);
        Assert.Equal(["Background", "Red half", "Hidden green", "Yellow multiply", "Ünïcødé 東京"], document.Layers.Select(l => l.Name).ToArray());
        Assert.Equal(128, document.Layers[1].Opacity);
        Assert.True(document.Layers[2].Hidden);
        Assert.Equal("mul ", document.Layers[3].BlendKey);
        Assert.Equal((20, 10, 16, 16), (document.Layers[3].Left, document.Layers[3].Top, document.Layers[3].Width, document.Layers[3].Height));
    }

    [Fact]
    public async Task Psd_to_png_uses_the_merged_composite_and_keeps_transparency()
    {
        using var workspace = new TestWorkspace("psd-composite");
        var path = PsdFixtures.Write(workspace.Path("design.psd"), TransparentDesign());
        var result = await workspace.RunAsync(workspace.Operation(Provider, "image.png"), [path]);
        Assert.Empty(result.Warnings);
        using var image = new MagickImage(Assert.Single(result.Outputs));
        Assert.Equal((64u, 48u), (image.Width, image.Height));
        ImageAssert.Near(ImageAssert.Pixel(image, 5, 5), (255, 0, 0, 255), 0, "red");
        ImageAssert.Near(ImageAssert.Pixel(image, 50, 40), (0, 0, 255, 255), 0, "blue");
        Assert.Equal(0, ImageAssert.Pixel(image, 50, 5).A);
        Assert.Equal(0, ImageAssert.Pixel(image, 5, 40).A);
    }

    [Theory]
    [InlineData(false, true)]   // "Maximize compatibility" off: blank composite + version info says no real merged data
    [InlineData(true, false)]   // blank composite written by a tool that doesn't add the version info resource
    public async Task Missing_or_blank_composite_is_rebuilt_from_layers(bool blankOnly, bool markMissing)
    {
        using var workspace = new TestWorkspace("psd-rebuild");
        var spec = Semantics() with { BlankComposite = true, MarkNoRealMergedData = markMissing };
        var path = PsdFixtures.Write(workspace.Path("nocompat.psd"), spec);
        var result = await workspace.RunAsync(workspace.Operation(Provider, "image.png"), [path]);
        Assert.Contains(result.Warnings, w => w.Contains("rebuilt from its layers", StringComparison.Ordinal));
        using var image = new MagickImage(Assert.Single(result.Outputs));
        ImageAssert.Near(ImageAssert.Pixel(image, 5, 5), (255, 127, 127, 255), 3, "50 % red over white");
        ImageAssert.Near(ImageAssert.Pixel(image, 8, 33), (255, 255, 255, 255), 0, "hidden layer is not drawn");
        ImageAssert.Near(ImageAssert.Pixel(image, 25, 15), (255, 127, 0, 255), 3, "multiply over the red layer");
        ImageAssert.Near(ImageAssert.Pixel(image, 34, 15), (255, 255, 0, 255), 3, "multiply over white");
        Assert.True(blankOnly || markMissing);
    }

    [Fact]
    public async Task Clipping_masks_are_honoured_when_rebuilding()
    {
        using var workspace = new TestWorkspace("psd-clip");
        var spec = new PsdSpec(64, 48,
        [
            new LayerSpec("Base", "#0000FF", 10, 10, 20, 20),
            new LayerSpec("Clipped texture", "#FF0000", 0, 0, 64, 48) { Clipped = true },
        ]) { BlankComposite = true, MarkNoRealMergedData = true };
        var path = PsdFixtures.Write(workspace.Path("clip.psd"), spec);
        var output = Assert.Single((await workspace.RunAsync(workspace.Operation(Provider, "image.png"), [path])).Outputs);
        using var image = new MagickImage(output);
        ImageAssert.Near(ImageAssert.Pixel(image, 15, 15), (255, 0, 0, 255), 2, "clipped layer inside its base");
        Assert.Equal(0, ImageAssert.Pixel(image, 45, 40).A);
        Assert.Equal(0, ImageAssert.Pixel(image, 2, 2).A);
    }

    [Fact]
    public async Task Sixteen_bit_psd_keeps_its_precision_in_png()
    {
        using var workspace = new TestWorkspace("psd-16");
        var spec = new PsdSpec(16, 16, [new LayerSpec("Deep", "#000000", 0, 0, 16, 16) { RawRgb16 = [0x1234, 0x8000, 0xFEDC] }]) { Depth = 16 };
        var path = PsdFixtures.Write(workspace.Path("deep.psd"), spec);
        var output = Assert.Single((await workspace.RunAsync(workspace.Operation(Provider, "image.png"), [path])).Outputs);
        using var image = new MagickImage(output);
        Assert.Equal(16u, image.Depth);
        var pixel = image.GetPixels().GetPixel(4, 4);
        Assert.Equal(0x1234, pixel.GetChannel(0), 2.0);
        Assert.Equal(0x8000, pixel.GetChannel(1), 2.0);
        Assert.Equal(0xFEDC, pixel.GetChannel(2), 2.0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cmyk_psd_comes_out_in_correct_srgb(bool blankComposite)
    {
        using var workspace = new TestWorkspace("psd-cmyk");
        var spec = new PsdSpec(32, 32, [new LayerSpec("Cyan ink", "#00FFFF", 0, 0, 32, 32)]) { Cmyk = true, BlankComposite = blankComposite, MarkNoRealMergedData = blankComposite };
        var path = PsdFixtures.Write(workspace.Path("print.psd"), spec);
        Assert.Equal(PsdColorMode.Cmyk, PsdDocument.TryRead(path)!.ColorMode);
        var output = Assert.Single((await workspace.RunAsync(workspace.Operation(Provider, "image.jpg"), [path])).Outputs);
        using var image = new MagickImage(output);
        Assert.Equal(ColorSpace.sRGB, image.ColorSpace);
        var (r, g, b, _) = ImageAssert.Pixel(image, 16, 16);
        Assert.InRange(r, 0, 40);
        Assert.InRange(g, 140, 200);
        Assert.InRange(b, 200, 255);
    }

    [Fact]
    public async Task Psb_large_document_format_is_read()
    {
        using var workspace = new TestWorkspace("psb");
        var path = PsdFixtures.Write(workspace.Path("big.psb"), TransparentDesign() with { Large = true, BlankComposite = true, MarkNoRealMergedData = true });
        var document = PsdDocument.TryRead(path);
        Assert.True(document!.IsLargeDocument);
        Assert.Equal(2, document.Layers.Count);
        var output = Assert.Single((await workspace.RunAsync(workspace.Operation(Provider, "image.png"), [path])).Outputs);
        using var image = new MagickImage(output);
        ImageAssert.Near(ImageAssert.Pixel(image, 5, 5), (255, 0, 0, 255), 0, "red");
        Assert.Equal(0, ImageAssert.Pixel(image, 50, 5).A);
    }

    [Fact]
    public void A_genuinely_plain_document_is_not_rebuilt()
    {
        using var workspace = new TestWorkspace("psd-plain");
        var path = PsdFixtures.Write(workspace.Path("plain.psd"), new PsdSpec(20, 20, [new LayerSpec("White", "#FFFFFF", 0, 0, 20, 20)]));
        using var image = ImageLoader.Load(InputFile.FromPath(path));
        Assert.False(image.RebuiltFromLayers);
        Assert.Equal(1, image.LayerCount);
    }
}
