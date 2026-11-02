namespace Clementine.Engines.Images.Imaging;

/// <summary>
/// An input decoded and normalised for processing: upright (EXIF orientation applied), in sRGB or grey
/// (CMYK/Lab converted with colour profiles), PSD composite verified, animation coalesced when kept.
/// Owned by one run; dispose it when done.
/// </summary>
public sealed class LoadedImage : IDisposable
{
    readonly List<string> _warnings = [];

    internal LoadedImage(InputFile source, MagickImageCollection frames)
    {
        Source = source;
        Frames = frames;
        SourceDepth = frames.Count > 0 ? frames[0].Depth : 8;
        SourceQuality = frames.Count > 0 && frames[0].Quality is > 0 and <= 100 ? frames[0].Quality : null;
        SourceFormat = frames.Count > 0 ? frames[0].Format : MagickFormat.Unknown;
    }

    public InputFile Source { get; }

    /// <summary>One frame for stills; every (full-canvas) frame for kept animations.</summary>
    public MagickImageCollection Frames { get; private set; }

    public IMagickImage<QuantumType> First => Frames[0];

    public bool IsAnimated => Frames.Count > 1;

    /// <summary>Bits per channel of the decoded source (8, 16, 32).</summary>
    public uint SourceDepth { get; }

    /// <summary>The decoder ImageMagick actually used (detects misnamed files).</summary>
    public MagickFormat SourceFormat { get; }

    /// <summary>EXIF orientation before the pixels were turned upright.</summary>
    public OrientationType OriginalOrientation { get; internal set; } = OrientationType.Undefined;

    /// <summary>Encoder quality ImageMagick estimated from the source (JPEG quantisation tables), if any.</summary>
    public uint? SourceQuality { get; }

    /// <summary>Linear high-dynamic-range data (EXR/HDR) that needs tone mapping for 8/16-bit outputs.</summary>
    public bool IsHighDynamicRange { get; internal set; }

    /// <summary>The PSD's composite was missing or blank and the image was rebuilt from its layers.</summary>
    public bool RebuiltFromLayers { get; internal set; }

    /// <summary>Number of pixel layers (PSD/PSB), 0 otherwise.</summary>
    public int LayerCount { get; internal set; }

    /// <summary>The colour model before normalisation (e.g. CMYK), for messages and reports.</summary>
    public ColorSpace OriginalColorSpace { get; internal set; } = ColorSpace.sRGB;

    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Whether any frame has pixels that aren't fully opaque.</summary>
    public bool HasTransparency => Frames.Any(f => f.HasAlpha && !f.IsOpaque);

    public void AddWarning(string warning)
    {
        if (!_warnings.Contains(warning))
            _warnings.Add(warning);
    }

    /// <summary>Drops every frame after the first.</summary>
    public void KeepFirstFrameOnly()
    {
        while (Frames.Count > 1)
        {
            var last = Frames[^1];
            Frames.RemoveAt(Frames.Count - 1);
            last.Dispose();
        }
    }

    /// <summary>Replaces frame <paramref name="index"/> (the old one is disposed).</summary>
    public void ReplaceFrame(int index, IMagickImage<QuantumType> image)
    {
        var old = Frames[index];
        if (ReferenceEquals(old, image))
            return;
        Frames[index] = image;
        old.Dispose();
    }

    /// <summary>Swaps in a whole new set of frames (the old ones are disposed).</summary>
    public void ReplaceAllFrames(MagickImageCollection frames)
    {
        var old = Frames;
        Frames = frames;
        old.Dispose();
    }

    /// <summary>Applies <paramref name="transform"/> to every frame.</summary>
    public void ForEachFrame(Action<IMagickImage<QuantumType>> transform)
    {
        foreach (var frame in Frames)
            transform(frame);
    }

    public void Dispose() => Frames.Dispose();
}
