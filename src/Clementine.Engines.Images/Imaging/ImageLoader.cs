using System.Buffers.Binary;
using Clementine.Engines.Images.Psd;
using ImageMagick.Formats;

namespace Clementine.Engines.Images.Imaging;

/// <summary>How an input should be decoded.</summary>
public sealed record LoadOptions
{
    public static readonly LoadOptions Still = new();

    public static readonly LoadOptions Animated = new() { KeepAnimation = true };

    /// <summary>Keep every frame of animated GIF/WebP (coalesced to full frames). Otherwise only the first frame is kept.</summary>
    public bool KeepAnimation { get; init; }

    /// <summary>SVG rasterisation scale relative to the intrinsic size (96 DPI).</summary>
    public double SvgScale { get; init; } = 1;

    /// <summary>Apply the EXIF orientation to the pixels.</summary>
    public bool Orient { get; init; } = true;

    /// <summary>Convert CMYK/Lab/… to sRGB.</summary>
    public bool ToDisplayColor { get; init; } = true;
}

/// <summary>Decodes inputs into a normalised <see cref="LoadedImage"/>.</summary>
public static class ImageLoader
{
    public static LoadedImage Load(InputFile input, LoadOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= LoadOptions.Still;
        MagickRuntime.EnsureInitialized();
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(input.Path))
            throw new OperationException($"“{input.Name}” is no longer there.");

        EnsureNotTooLarge(input, ReadSettings(input, options, firstFrameOnly: true));
        cancellationToken.ThrowIfCancellationRequested();

        LoadedImage image;
        try
        {
            image = Decode(input, options, cancellationToken);
        }
        catch (MagickException ex)
        {
            throw MagickErrors.Reading(ex, input);
        }

        try
        {
            if (image.Frames.Count == 0)
                throw new OperationException("This file doesn't contain an image.");
            Normalize(image, options, cancellationToken);
            return image;
        }
        catch (MagickException ex)
        {
            image.Dispose();
            throw MagickErrors.Reading(ex, input);
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Reads only the header (ImageMagick "ping", a few milliseconds) and refuses images above
    /// <see cref="MagickRuntime.MaxPixels"/> before any pixel memory is allocated.
    /// </summary>
    public static void EnsureNotTooLarge(InputFile input, MagickReadSettings settings)
    {
        long width, height;
        try
        {
            using var header = new MagickImage();
            header.Ping(input.Path, settings);
            (width, height) = (header.Width, header.Height);
        }
        catch (MagickException ex)
        {
            if (MagickErrors.IsOverLimit(ex))
                throw MagickErrors.Reading(ex, input); // a side beyond MagickRuntime.MaxSide is refused by the header too
            return; // the full read reports what is wrong with the file
        }
        var pixels = width * height;
        if (pixels > MagickRuntime.MaxPixels)
        {
            throw new OperationException(
                $"This image is too large to convert: {width:N0} × {height:N0} px ({pixels / 1_000_000:N0} megapixels). This computer can handle up to about {MagickRuntime.MaxPixels / 1_000_000:N0} megapixels.");
        }
    }

    /// <summary>Read settings for <paramref name="input"/>; frame limits are optional.</summary>
    public static MagickReadSettings ReadSettings(InputFile input, LoadOptions options, bool firstFrameOnly)
    {
        var settings = new MagickReadSettings();
        if (ImageFormats.ReadFormatFor(input.Extension) is { } format)
            settings.Format = format;
        if (firstFrameOnly)
        {
            settings.FrameIndex = 0;
            settings.FrameCount = 1;
        }
        if (input.HasExtension(ImageFormats.Svg))
        {
            settings.Density = new Density(96 * Math.Clamp(options.SvgScale, 0.05, 32));
            settings.BackgroundColor = MagickColors.None;
        }
        if (input.HasExtension(ImageFormats.CameraRaw))
            settings.SetDefines(new DngReadDefines { UseCameraWhiteBalance = true });
        return settings;
    }

    static LoadedImage Decode(InputFile input, LoadOptions options, CancellationToken cancellationToken)
    {
        if (input.HasExtension(ImageFormats.Psd))
            return PsdImageReader.ReadFlattened(input, options, cancellationToken);

        if (input.HasExtension(ImageFormats.Icon))
            return new LoadedImage(input, LargestFrame(ReadAll(input, options)));

        if (input.HasExtension(".xcf"))
        {
            using var layers = ReadAll(input, options);
            return new LoadedImage(input, [(MagickImage)layers.Flatten(MagickColors.Transparent)]);
        }

        if (input.HasExtension(ImageFormats.MaybeAnimated))
        {
            var all = ReadAll(input, options);
            if (all.Count > 1)
            {
                all.Coalesce();
                var animated = new LoadedImage(input, all);
                if (!options.KeepAnimation)
                {
                    animated.KeepFirstFrameOnly();
                    animated.AddWarning("Only the first frame of the animation was kept.");
                }
                return animated;
            }
            return new LoadedImage(input, ExpandToCanvas(all));
        }

        var frames = new MagickImageCollection();
        frames.Read(input.Path, ReadSettings(input, options, firstFrameOnly: true));
        var image = new LoadedImage(input, frames);
        if (input.HasExtension(ImageFormats.AnimatedPng) && ApngFrameCount(input.Path) > 1)
            image.AddWarning("Only the first frame of the animation was kept.");
        return image;
    }

    static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The frame count an animated PNG declares (its acTL chunk, which precedes the image data), else 1.</summary>
    static uint ApngFrameCount(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> chunk = stackalloc byte[8];
            if (stream.ReadAtLeast(chunk, 8, throwOnEndOfStream: false) < 8 || !chunk.SequenceEqual(PngSignature))
                return 1;
            while (stream.ReadAtLeast(chunk, 8, throwOnEndOfStream: false) == 8)
            {
                var length = BinaryPrimitives.ReadUInt32BigEndian(chunk);
                if (chunk[4..].SequenceEqual("acTL"u8))
                    return length >= 8 && stream.ReadAtLeast(chunk, 8, throwOnEndOfStream: false) == 8 ? BinaryPrimitives.ReadUInt32BigEndian(chunk) : 1;
                if (chunk[4..].SequenceEqual("IDAT"u8))
                    return 1;
                stream.Seek(length + 4L, SeekOrigin.Current); // data + CRC
            }
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 1;
        }
    }

    static MagickImageCollection ReadAll(InputFile input, LoadOptions options)
    {
        var frames = new MagickImageCollection();
        frames.Read(input.Path, ReadSettings(input, options, firstFrameOnly: false));
        return frames;
    }

    static MagickImageCollection LargestFrame(MagickImageCollection frames)
    {
        using (frames)
        {
            var best = frames.OrderByDescending(f => (long)f.Width * f.Height).ThenByDescending(f => f.Depth).First();
            return [best.Clone()];
        }
    }

    /// <summary>A single GIF/WebP frame may be smaller than its logical screen; place it on a full canvas.</summary>
    static MagickImageCollection ExpandToCanvas(MagickImageCollection frames)
    {
        var frame = frames[0];
        var page = frame.Page;
        if (page.Width <= frame.Width && page.Height <= frame.Height && page.X == 0 && page.Y == 0)
        {
            frame.ResetPage();
            return frames;
        }
        var canvas = new MagickImage(MagickColors.Transparent, Math.Max(page.Width, frame.Width), Math.Max(page.Height, frame.Height));
        canvas.Composite(frame, page.X, page.Y, CompositeOperator.Over);
        foreach (var name in frame.ProfileNames)
        {
            if (frame.GetProfile(name) is { } profile)
                canvas.SetProfile(profile);
        }
        frames.Dispose();
        return [canvas];
    }

    static void Normalize(LoadedImage image, LoadOptions options, CancellationToken cancellationToken)
    {
        image.OriginalColorSpace = image.First.ColorSpace;
        image.OriginalOrientation = image.First.Orientation;
        image.IsHighDynamicRange = image.Source.HasExtension(ImageFormats.HighDynamicRange);
        foreach (var frame in image.Frames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (options.Orient)
                ColorPipeline.Orient(frame);
            if (options.ToDisplayColor && !image.IsHighDynamicRange)
                ColorPipeline.ToDisplayColorSpace(frame);
            frame.ResetPage();
        }
    }
}
