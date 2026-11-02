namespace Clementine.Engines.Images.Imaging;

/// <summary>A conversion target on the Convert wheel ("PNG", "JPEG" …).</summary>
public sealed record ImageTarget
{
    /// <summary>Id segment after "image." ("png", "jpg").</summary>
    public required string Key { get; init; }

    /// <summary>Inner-ring label.</summary>
    public required string Group { get; init; }

    public required string Extension { get; init; }

    public required MagickFormat Format { get; init; }

    /// <summary>Input extensions that already are this format (no conversion offered for them).</summary>
    public IReadOnlySet<string> SameFormat { get; init; } = new HashSet<string>();

    public int Order { get; init; }

    public string IconKey { get; init; } = "image";

    public OperationWeight Weight { get; init; } = OperationWeight.Light;

    /// <summary>Only offered when a real encode round trip succeeds on this machine.</summary>
    public bool RequiresEncodeProbe { get; init; }
}

/// <summary>What a format can store, so the encoder knows when to flatten, drop frames or reduce depth.</summary>
/// <param name="ColorProfile">Embeds an ICC profile that viewers honour; otherwise wide-gamut pixels are converted to sRGB first.</param>
public readonly record struct FormatTraits(bool Alpha, bool Animation, bool SixteenBit, bool HighDynamicRange, bool ColorProfile = true);

/// <summary>The conversion targets and per-format traits.</summary>
public static class ImageTargets
{
    public static readonly ImageTarget Png = new()
    {
        Key = "png", Group = "PNG", Extension = ".png", Format = MagickFormat.Png, Order = 10,
        SameFormat = Set(".png"),
    };

    public static readonly ImageTarget Jpeg = new()
    {
        Key = "jpg", Group = "JPEG", Extension = ".jpg", Format = MagickFormat.Jpeg, Order = 20,
        SameFormat = ImageFormats.Jpeg,
    };

    public static readonly ImageTarget WebP = new()
    {
        Key = "webp", Group = "WebP", Extension = ".webp", Format = MagickFormat.WebP, Order = 30,
        SameFormat = Set(".webp"),
    };

    public static readonly ImageTarget Avif = new()
    {
        Key = "avif", Group = "AVIF", Extension = ".avif", Format = MagickFormat.Avif, Order = 40,
        SameFormat = Set(".avif"), Weight = OperationWeight.Heavy, RequiresEncodeProbe = true,
    };

    /// <summary>Offered only if this ImageMagick build can encode HEVC (the bundled libheif can't, so it's omitted today).</summary>
    public static readonly ImageTarget Heic = new()
    {
        Key = "heic", Group = "HEIC", Extension = ".heic", Format = MagickFormat.Heic, Order = 45,
        SameFormat = Set(".heic", ".heif"), Weight = OperationWeight.Heavy, RequiresEncodeProbe = true,
    };

    public static readonly ImageTarget Pdf = new()
    {
        Key = "pdf", Group = "PDF", Extension = ".pdf", Format = MagickFormat.Pdf, Order = 50, IconKey = "pdf",
    };

    public static readonly ImageTarget Tiff = new()
    {
        Key = "tiff", Group = "TIFF", Extension = ".tif", Format = MagickFormat.Tiff, Order = 60,
        SameFormat = Set(".tif", ".tiff"),
    };

    public static readonly ImageTarget Gif = new()
    {
        Key = "gif", Group = "GIF", Extension = ".gif", Format = MagickFormat.Gif, Order = 70, IconKey = "gif",
        SameFormat = Set(".gif"),
    };

    public static readonly ImageTarget Ico = new()
    {
        Key = "ico", Group = "ICO", Extension = ".ico", Format = MagickFormat.Ico, Order = 210,
        SameFormat = Set(".ico"),
    };

    public static readonly ImageTarget Bmp = new()
    {
        Key = "bmp", Group = "BMP", Extension = ".bmp", Format = MagickFormat.Bmp3, Order = 220,
        SameFormat = Set(".bmp", ".dib"),
    };

    public static readonly ImageTarget Jxl = new()
    {
        Key = "jxl", Group = "JPEG XL", Extension = ".jxl", Format = MagickFormat.Jxl, Order = 230,
        SameFormat = Set(".jxl"), RequiresEncodeProbe = true,
    };

    public static readonly IReadOnlyList<ImageTarget> All = [Png, Jpeg, WebP, Avif, Heic, Pdf, Tiff, Gif, Ico, Bmp, Jxl];

    public static FormatTraits Traits(MagickFormat format) => format switch
    {
        MagickFormat.Png or MagickFormat.Png64 or MagickFormat.Png48 => new(Alpha: true, Animation: false, SixteenBit: true, HighDynamicRange: false),
        MagickFormat.Jpeg or MagickFormat.Jpg => new(false, false, false, false),
        MagickFormat.WebP => new(true, true, false, false),
        MagickFormat.Avif or MagickFormat.Heic => new(true, false, false, false),
        MagickFormat.Jxl => new(true, false, true, false),
        MagickFormat.Tiff or MagickFormat.Tif => new(true, false, true, false),
        // GIF's ICC extension is ignored by browsers and players; ICO, BMP3, TGA, QOI and PPM can't carry one.
        MagickFormat.Gif => new(true, true, false, false, ColorProfile: false),
        MagickFormat.Ico => new(true, false, false, false, ColorProfile: false),
        MagickFormat.Bmp or MagickFormat.Bmp3 => new(false, false, false, false, ColorProfile: false),
        MagickFormat.Pdf => new(true, false, false, false),
        MagickFormat.Tga or MagickFormat.Qoi => new(true, false, false, false, ColorProfile: false),
        MagickFormat.Jp2 => new(true, false, true, false),
        MagickFormat.Exr => new(true, false, true, true),
        MagickFormat.Hdr => new(false, false, true, true, ColorProfile: false),
        MagickFormat.Ppm or MagickFormat.Pgm => new(false, false, true, false, ColorProfile: false),
        _ => new(false, false, false, false, ColorProfile: false),
    };

    public static string DisplayName(MagickFormat format) => format switch
    {
        MagickFormat.Jpeg or MagickFormat.Jpg => "JPEG",
        MagickFormat.WebP => "WebP",
        MagickFormat.Jxl => "JPEG XL",
        MagickFormat.Tiff or MagickFormat.Tif => "TIFF",
        MagickFormat.Bmp or MagickFormat.Bmp3 => "BMP",
        _ => format.ToString().ToUpperInvariant(),
    };

    static IReadOnlySet<string> Set(params string[] extensions) => new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
}
