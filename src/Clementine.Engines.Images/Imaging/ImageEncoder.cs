using Clementine.Engines.Images.Pdf;
using ImageMagick.Formats;

namespace Clementine.Engines.Images.Imaging;

/// <summary>Encoder knobs; zero/false means "the format's sensible default".</summary>
public sealed record EncodeOptions
{
    public static readonly EncodeOptions Default = new();

    /// <summary>1–100 for lossy formats.</summary>
    public int Quality { get; init; }

    /// <summary>WebP/AVIF/JXL lossless mode.</summary>
    public bool Lossless { get; init; }

    /// <summary>JPEG chroma: true = 4:4:4, false = 4:2:0, null = 4:4:4 from quality 90 up.</summary>
    public bool? FullChroma { get; init; }

    /// <summary>WebP only: let libwebp search its quantiser for this many bytes (multi-pass).</summary>
    public int TargetBytes { get; init; }

    /// <summary>WebP only: spatial noise shaping strength 0–100 (null = libwebp default, 50).</summary>
    public int? WebPNoiseShaping { get; init; }

    /// <summary>
    /// PNG only: spend extra time on zlib level 9 (the Compress tool). Conversions use the standard level, because
    /// level 9 makes a 1080p PNG ~8× slower and a 16-bit 10 MP one ~10× slower for a 2–5 % smaller file.
    /// </summary>
    public bool PngMaxCompression { get; init; }
}

/// <summary>
/// Turns a <see cref="LoadedImage"/> into a file of a given format, applying the conversion rules: animation only
/// where the format supports it, HDR tone-mapped for integer formats, transparency flattened onto white for
/// formats without alpha, 16-bit kept where the format can store it, ICC/EXIF kept.
/// </summary>
public static class ImageEncoder
{
    public const int DefaultJpegQuality = 92;
    public const int DefaultWebPQuality = 82;
    public const int DefaultAvifQuality = 60;
    public const int DefaultJxlQuality = 90;

    static readonly uint[] IconSizes = [256, 128, 64, 48, 32, 24, 16];

    /// <summary>Prepares and writes <paramref name="image"/> to <paramref name="path"/> (a new file).</summary>
    public static void Save(LoadedImage image, MagickFormat format, EncodeOptions options, string path, CancellationToken cancellationToken)
    {
        Prepare(image, format);
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
        Write(image, format, options, stream, cancellationToken);
    }

    /// <summary>Encodes an already prepared image into memory (used by size-targeting searches).</summary>
    public static byte[] Encode(LoadedImage image, MagickFormat format, EncodeOptions options, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        Write(image, format, options, stream, cancellationToken);
        return stream.ToArray();
    }

    /// <summary>Adapts the frames to what <paramref name="format"/> can store. Idempotent.</summary>
    public static void Prepare(LoadedImage image, MagickFormat format)
    {
        var traits = ImageTargets.Traits(format);
        var name = ImageTargets.DisplayName(format);
        try
        {
            if (image.IsAnimated && !traits.Animation)
            {
                image.KeepFirstFrameOnly();
                image.AddWarning($"{name} can't hold animation, so only the first frame was kept.");
            }

            if (image.IsHighDynamicRange && !traits.HighDynamicRange)
            {
                for (var i = 0; i < image.Frames.Count; i++)
                    image.ReplaceFrame(i, ColorPipeline.ToneMapToDisplay(image.Frames[i]));
                image.IsHighDynamicRange = false;
            }

            var dropPhotoshopResources = image.Source.HasExtension(ImageFormats.Psd) || format is not (MagickFormat.Jpeg or MagickFormat.Tiff);
            foreach (var frame in image.Frames)
            {
                // Display P3 (iPhone), Adobe RGB … would look washed out once the profile is lost.
                if (!traits.ColorProfile)
                    ColorPipeline.ToSrgbProfile(frame);
                if (!traits.Alpha && frame.HasAlpha)
                {
                    frame.BackgroundColor = MagickColors.White;
                    frame.Alpha(AlphaOption.Remove);
                    frame.Alpha(AlphaOption.Off);
                }
                if (!traits.SixteenBit && frame.Depth > 8)
                    frame.Depth = 8;
                else if (frame.Depth > 16 && !traits.HighDynamicRange)
                    frame.Depth = 16;
                if (dropPhotoshopResources && frame.HasProfile("8bim"))
                    frame.RemoveProfile("8bim");
            }

            if (format == MagickFormat.Ico)
                PrepareIcon(image);
            else if (format == MagickFormat.Gif)
                PrepareGif(image);
        }
        catch (MagickException ex)
        {
            throw MagickErrors.Writing(ex, name);
        }
    }

    static void Write(LoadedImage image, MagickFormat format, EncodeOptions options, Stream stream, CancellationToken cancellationToken)
    {
        var name = ImageTargets.DisplayName(format);
        // Long native encodes (a 50 MP AVIF, say) poll this and stop when the job is cancelled.
        EventHandler<ProgressEventArgs> cancelWhenAsked = (_, e) => e.Cancel = cancellationToken.IsCancellationRequested;
        foreach (var frame in image.Frames)
            frame.Progress += cancelWhenAsked;
        try
        {
            if (format == MagickFormat.Pdf)
            {
                PdfImageWriter.Write(stream, [PdfPages.FitToImage(image)], image.Source.BaseName, cancellationToken);
                return;
            }

            foreach (var frame in image.Frames)
                Configure(frame, format, options);
            if (image.Frames.Count > 1)
                image.Frames.Write(stream, format);
            else
                image.First.Write(stream, format);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (MagickException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (MagickException ex)
        {
            throw MagickErrors.Writing(ex, name);
        }
        finally
        {
            foreach (var frame in image.Frames)
                frame.Progress -= cancelWhenAsked;
        }
    }

    static void Configure(IMagickImage<QuantumType> frame, MagickFormat format, EncodeOptions options)
    {
        switch (format)
        {
            case MagickFormat.Jpeg or MagickFormat.Jpg:
                var jpegQuality = Quality(options, DefaultJpegQuality);
                frame.Quality = (uint)jpegQuality;
                frame.Settings.SetDefines(new JpegWriteDefines
                {
                    SamplingFactor = (options.FullChroma ?? jpegQuality >= 90) ? JpegSamplingFactor.Ratio444 : JpegSamplingFactor.Ratio420,
                    OptimizeCoding = true,
                });
                break;
            case MagickFormat.WebP:
                frame.Quality = (uint)(options.Lossless ? 100 : Quality(options, DefaultWebPQuality));
                frame.Settings.SetDefines(options.TargetBytes > 0
                    ? new WebPWriteDefines { Lossless = false, Method = 4, TargetSize = options.TargetBytes, Pass = 6, SnsStrength = options.WebPNoiseShaping }
                    : new WebPWriteDefines { Lossless = options.Lossless, Method = options.Lossless ? 5 : 4 });
                break;
            case MagickFormat.Avif or MagickFormat.Heic:
                frame.Quality = (uint)(options.Lossless ? 100 : Quality(options, DefaultAvifQuality));
                frame.Settings.SetDefine(MagickFormat.Heic, "speed", "7");
                if (options.Lossless)
                    frame.Settings.SetDefine(MagickFormat.Heic, "lossless", "true");
                break;
            case MagickFormat.Jxl:
                frame.Quality = (uint)(options.Lossless ? 100 : Quality(options, DefaultJxlQuality));
                frame.Settings.SetDefines(new JxlWriteDefines { Effort = 7 });
                break;
            case MagickFormat.Tiff or MagickFormat.Tif:
                // Full-colour samples (palette TIFFs with alpha are poorly supported, and the predictor needs 8/16-bit).
                var gray = frame.ColorSpace is ColorSpace.Gray or ColorSpace.LinearGray;
                frame.ColorType = (gray, frame.HasAlpha) switch
                {
                    (true, true) => ColorType.GrayscaleAlpha,
                    (true, false) => ColorType.Grayscale,
                    (false, true) => ColorType.TrueColorAlpha,
                    _ => ColorType.TrueColor,
                };
                if (frame.Depth < 8)
                    frame.Depth = 8;
                frame.Settings.Compression = CompressionMethod.LZW;
                frame.Settings.SetDefines(new TiffWriteDefines { Predictor = 2, Alpha = TiffAlpha.Unassociated });
                break;
            case MagickFormat.Png:
                ConfigurePng(frame, options.PngMaxCompression);
                break;
            case MagickFormat.Ico:
                // Every standard size up to the first one that covers the image (a 160 px logo gets 256 too).
                var side = Math.Max(frame.Width, frame.Height);
                var largest = IconSizes.Where(s => s >= side).DefaultIfEmpty(IconSizes[0]).Min();
                var sizes = IconSizes.Where(s => s <= largest).ToArray();
                frame.Settings.SetDefine("icon:auto-resize", string.Join(",", sizes));
                break;
        }
    }

    /// <summary>
    /// PNG writer settings shared by every PNG Clementine writes. zlib level 7 (ImageMagick's default) for 8-bit:
    /// level 8–9 search chains of up to 4096 matches, which on flat VJ artwork made a 1080p PSD take 8–16 s instead of
    /// ~2 s for a 5–25 % smaller file. Level 4 for 16-bit, whose noisy low bytes make higher levels several times
    /// slower for the same size (3156×3046 16-bit PSD: level 9 ≈ 120 s, 6 ≈ 24 s, 4 ≈ 12 s, all within 4 %).
    /// Level 9 only when asked for (Compress).
    /// </summary>
    public static void ConfigurePng(IMagickImage<QuantumType> frame, bool maxCompression = false)
    {
        var level = maxCompression ? 9 : frame.Depth > 8 ? 4 : 7;
        // No tEXt "date:create/modify" chunks: they only leak the conversion time.
        frame.Settings.SetDefines(new PngWriteDefines { CompressionLevel = (uint)level, ExcludeChunks = PngChunkFlags.date });
    }

    static int Quality(EncodeOptions options, int fallback) => options.Quality is > 0 and <= 100 ? options.Quality : fallback;

    static void PrepareIcon(LoadedImage image)
    {
        var frame = image.First;
        var side = Math.Max(frame.Width, frame.Height);
        if (frame.Width != frame.Height)
        {
            frame.Alpha(AlphaOption.Set);
            frame.BackgroundColor = MagickColors.Transparent;
            frame.Extent(side, side, Gravity.Center, MagickColors.Transparent);
        }
        if (side > 256)
            frame.Resize(256, 256);
        else if (side < 16)
            frame.Resize(16, 16);
        frame.Depth = 8;
    }

    static void PrepareGif(LoadedImage image)
    {
        if (image.First.ColormapSize > 0 && image.First.ColormapSize <= 256 && !image.IsAnimated)
            return; // already palette-based (e.g. prepared before)
        var settings = new QuantizeSettings { Colors = 256, DitherMethod = DitherMethod.FloydSteinberg };
        if (image.IsAnimated)
        {
            image.Frames.Quantize(settings);
            image.Frames.Optimize();
            image.Frames.OptimizeTransparency();
        }
        else
        {
            image.First.Quantize(settings);
        }
    }
}
