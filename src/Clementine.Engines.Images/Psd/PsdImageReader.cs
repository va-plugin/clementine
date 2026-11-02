using Clementine.Engines.Images.Imaging;

namespace Clementine.Engines.Images.Psd;

/// <summary>
/// Reads PSD/PSB files: the merged composite when it is real, otherwise the document rebuilt from its layers
/// (files saved without "Maximize compatibility" carry a blank composite).
/// </summary>
public static class PsdImageReader
{
    public static LoadedImage ReadFlattened(InputFile input, LoadOptions options, CancellationToken cancellationToken)
    {
        var document = PsdDocument.TryRead(input.Path);
        var frames = new MagickImageCollection();
        frames.Read(input.Path, ImageLoader.ReadSettings(input, options, firstFrameOnly: true));
        var composite = frames[0];
        var layerCount = document?.PixelLayers.Count() ?? 0;

        var compositeMissing = document?.HasRealMergedData == false;
        // Photoshop says whether its composite is real ("version info" resource); only when that is absent (other
        // apps) is a perfectly uniform composite taken as a placeholder. A real one can be uniform: a white
        // artboard, or layers hidden behind a fill or adjustment layer the rebuild couldn't reproduce.
        var compositeBlank = layerCount > 0 && document?.HasRealMergedData is null && IsUniform(composite);
        if (layerCount == 0 || !(compositeMissing || compositeBlank))
            return new LoadedImage(input, frames) { LayerCount = layerCount };

        cancellationToken.ThrowIfCancellationRequested();
        using var all = new MagickImageCollection();
        all.Read(input.Path, ImageLoader.ReadSettings(input, options, firstFrameOnly: false));
        var layerFrames = PsdLayers.LayerFrames(all);
        if (layerFrames.Count == 0)
            return new LoadedImage(input, frames) { LayerCount = layerCount };

        var plan = PsdLayers.Plan(document, layerFrames);
        var rebuilt = PsdLayers.Composite(plan, composite.Width, composite.Height, composite, cancellationToken);
        if (compositeBlank && IsUniform(rebuilt))
        {
            rebuilt.Dispose(); // a genuinely plain document
            return new LoadedImage(input, frames) { LayerCount = layerCount };
        }

        frames.Dispose();
        var image = new LoadedImage(input, [rebuilt]) { LayerCount = layerCount, RebuiltFromLayers = true };
        image.AddWarning(compositeMissing
            ? "This PSD was saved without “Maximize compatibility”, so it was rebuilt from its layers (adjustment layers and layer effects aren't applied)."
            : "This PSD's preview image was blank, so it was rebuilt from its layers (adjustment layers and layer effects aren't applied).");
        return image;
    }

    /// <summary>Every pixel identical (e.g. the all-white composite Photoshop writes without "Maximize compatibility").</summary>
    public static bool IsUniform(IMagickImage<QuantumType> image)
    {
        var statistics = image.Statistics();
        foreach (var channel in image.Channels)
        {
            if (statistics.GetChannel(channel) is { } s && s.StandardDeviation > Quantum.Max / 2000.0)
                return false;
        }
        return true;
    }
}
