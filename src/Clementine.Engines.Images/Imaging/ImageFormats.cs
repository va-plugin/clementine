namespace Clementine.Engines.Images.Imaging;

/// <summary>Extension sets and read hints for the image formats Clementine understands.</summary>
public static class ImageFormats
{
    /// <summary>Every image extension this engine accepts (all of <see cref="FileTypes.Image"/>; each is readable by the bundled ImageMagick).</summary>
    public static readonly IReadOnlySet<string> Readable = FileTypes.Image;

    public static readonly IReadOnlySet<string> Jpeg = Set(".jpg", ".jpeg", ".jpe", ".jfif");

    public static readonly IReadOnlySet<string> Psd = Set(".psd", ".psb");

    public static readonly IReadOnlySet<string> Svg = Set(".svg", ".svgz");

    /// <summary>Formats whose files may hold an animation that conversions to GIF/WebP keep.</summary>
    public static readonly IReadOnlySet<string> MaybeAnimated = Set(".gif", ".webp");

    /// <summary>
    /// Animated PNG. Only its first frame is decoded here, so the animated targets (GIF/WebP) leave it to the media
    /// engine; the still targets take that first frame.
    /// </summary>
    public static readonly IReadOnlySet<string> AnimatedPng = Set(".apng");

    /// <summary>High dynamic range sources that are tone-mapped for integer outputs.</summary>
    public static readonly IReadOnlySet<string> HighDynamicRange = Set(".exr", ".hdr");

    /// <summary>Icon containers: several sizes per file, we use the largest.</summary>
    public static readonly IReadOnlySet<string> Icon = Set(".ico", ".cur");

    /// <summary>Files that can carry layers for the Export layers tool.</summary>
    public static readonly IReadOnlySet<string> Layered = Set(".psd", ".psb", ".tif", ".tiff");

    public static readonly IReadOnlySet<string> CameraRaw = Set(
        ".dng", ".cr2", ".cr3", ".crw", ".nef", ".nrw", ".arw", ".srf", ".sr2", ".orf", ".rw2", ".raf", ".pef",
        ".srw", ".x3f", ".3fr", ".iiq", ".erf", ".mrw", ".kdc", ".dcr");

    /// <summary>Lossy photo formats; tool results from these that we can't write again become JPEG.</summary>
    public static readonly IReadOnlySet<string> PhotoLike = Set(".heic", ".heif", ".avif", ".jxl", ".jp2", ".j2k")
        .Concat(CameraRaw).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Formats tools may write back unchanged (same extension), when the encoder works.</summary>
    static readonly Dictionary<string, MagickFormat> RewritableFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = MagickFormat.Jpeg,
        [".jpeg"] = MagickFormat.Jpeg,
        [".jpe"] = MagickFormat.Jpeg,
        [".jfif"] = MagickFormat.Jpeg,
        [".png"] = MagickFormat.Png,
        [".webp"] = MagickFormat.WebP,
        [".avif"] = MagickFormat.Avif,
        [".heic"] = MagickFormat.Heic,
        [".heif"] = MagickFormat.Heic,
        [".jxl"] = MagickFormat.Jxl,
        [".gif"] = MagickFormat.Gif,
        [".tif"] = MagickFormat.Tiff,
        [".tiff"] = MagickFormat.Tiff,
        [".bmp"] = MagickFormat.Bmp,
        [".dib"] = MagickFormat.Bmp,
        [".ico"] = MagickFormat.Ico,
        [".tga"] = MagickFormat.Tga,
        [".qoi"] = MagickFormat.Qoi,
        [".exr"] = MagickFormat.Exr,
        [".hdr"] = MagickFormat.Hdr,
        [".jp2"] = MagickFormat.Jp2,
        [".pcx"] = MagickFormat.Pcx,
        [".ppm"] = MagickFormat.Ppm,
        [".pgm"] = MagickFormat.Pgm,
        [".pbm"] = MagickFormat.Pbm,
    };

    /// <summary>
    /// Explicit decoder for extensions ImageMagick wouldn't recognise on its own (null = let ImageMagick detect
    /// the format from the file's signature, which also copes with misnamed files).
    /// </summary>
    public static MagickFormat? ReadFormatFor(string extension)
    {
        if (CameraRaw.Contains(extension) && !IsRegisteredExtension(extension))
            return MagickFormat.Dng; // LibRaw identifies the actual camera format from the content
        return extension switch
        {
            ".jfif" or ".jpe" => MagickFormat.Jpeg,
            ".apng" => MagickFormat.Png, // first frame; animated PNG belongs to the video engine
            ".dib" => MagickFormat.Bmp,
            ".tga" => MagickFormat.Tga,
            ".svgz" => MagickFormat.Svgz,
            _ => null,
        };
    }

    /// <summary>Where a tool writes its result: the input's own format when we can encode it, else PNG or JPEG.</summary>
    public static ToolOutputFormat ToolOutputFor(InputFile input, bool hasAlpha)
    {
        if (RewritableFormats.TryGetValue(input.Extension, out var format) && MagickRuntime.CanEncode(format))
            return new ToolOutputFormat(format, input.Extension, IsFallback: false);
        if (PhotoLike.Contains(input.Extension) && !hasAlpha)
            return new ToolOutputFormat(MagickFormat.Jpeg, ".jpg", IsFallback: true);
        return new ToolOutputFormat(MagickFormat.Png, ".png", IsFallback: true);
    }

    static bool IsRegisteredExtension(string extension)
    {
        MagickRuntime.EnsureInitialized();
        var name = extension.TrimStart('.');
        return MagickNET.SupportedFormats.Any(f => f.SupportsReading && string.Equals(f.Format.ToString(), name, StringComparison.OrdinalIgnoreCase));
    }

    static IReadOnlySet<string> Set(params string[] extensions) => new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
}

/// <summary>The format a tool writes, and whether it differs from the input's (worth a warning).</summary>
public sealed record ToolOutputFormat(MagickFormat Format, string Extension, bool IsFallback)
{
    public string Name => ImageTargets.DisplayName(Format);
}
