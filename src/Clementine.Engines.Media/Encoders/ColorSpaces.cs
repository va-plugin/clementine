using Clementine.Engines.Media.FFmpeg;

namespace Clementine.Engines.Media.Encoders;

/// <summary>
/// Colour-matrix decisions for YUV ⇄ RGB conversions. Untagged sources follow the usual convention:
/// BT.709 for HD (≥ 720 lines or ≥ 1280 wide), BT.601 below.
/// </summary>
public static class ColorSpaces
{
    /// <summary>swscale matrix name ("bt709", "bt601", "bt2020"…) for an ffprobe color_space, or null when untagged/RGB.</summary>
    public static string? MatrixOf(string? colorSpace) => colorSpace switch
    {
        "bt709" => "bt709",
        "smpte170m" or "bt470bg" => "bt601",
        "bt2020nc" or "bt2020c" => "bt2020",
        "fcc" => "fcc",
        "smpte240m" => "smpte240m",
        _ => null,
    };

    /// <summary>The matrix the source's YUV was made with: its tag, or the HD/SD convention when untagged.</summary>
    public static string SourceMatrix(VideoStreamInfo video) =>
        MatrixOf(video.ColorSpace) ?? (video.Height >= 720 || video.Width >= 1280 ? "bt709" : "bt601");

    /// <summary>Whether the source matrix came from a tag (true) or was inferred (false).</summary>
    public static bool IsTagged(VideoStreamInfo video) => MatrixOf(video.ColorSpace) is not null;

    /// <summary>swscale range name of the source ("full" for JPEG-range YUV, otherwise "limited").</summary>
    public static string SourceRange(VideoStreamInfo video) =>
        video.ColorRange == "pc" || PixelFormats.IsFullRangeYuv(video.PixelFormat) ? "full" : "limited";

    /// <summary>The matrix YUV outputs use: the source's tag, else BT.709.</summary>
    public static string OutputMatrix(VideoStreamInfo video) => MatrixOf(video.ColorSpace) ?? "bt709";

    /// <summary>"-colorspace" value for a swscale matrix name.</summary>
    public static string TagOf(string matrix) => matrix switch
    {
        "bt601" => "smpte170m",
        "bt2020" => "bt2020nc",
        _ => matrix,
    };

    /// <summary>Primaries/transfer to tag alongside a matrix when the source doesn't say.</summary>
    public static (string Primaries, string Transfer) DefaultsFor(string matrix) => matrix switch
    {
        "bt601" => ("smpte170m", "smpte170m"),
        "bt2020" => ("bt2020", "bt709"),
        "smpte240m" => ("smpte240m", "smpte240m"),
        _ => ("bt709", "bt709"),
    };
}
