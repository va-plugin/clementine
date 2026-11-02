using System.Text.RegularExpressions;

namespace Clementine.Engines.Media.FFmpeg;

/// <summary>Facts about FFmpeg pixel format names ("yuva444p10le", "rgba", "pal8").</summary>
public static partial class PixelFormats
{
    static readonly string[] AlphaPrefixes = ["yuva", "rgba", "bgra", "argb", "abgr", "gbrap", "ya", "ayuv", "vuya"];

    /// <summary>Whether the format carries an alpha channel (pal8 may, via a transparent palette entry, as in GIFs).</summary>
    public static bool HasAlpha(string? pixelFormat)
    {
        if (string.IsNullOrEmpty(pixelFormat))
            return false;
        if (pixelFormat is "pal8")
            return true;
        if (pixelFormat.StartsWith("rgbx", StringComparison.Ordinal) || pixelFormat.StartsWith("bgrx", StringComparison.Ordinal))
            return false;
        return AlphaPrefixes.Any(p => pixelFormat.StartsWith(p, StringComparison.Ordinal));
    }

    /// <summary>RGB-family (or palette/grey) formats need no YUV matrix when they are decoded.</summary>
    public static bool IsRgb(string? pixelFormat)
    {
        if (string.IsNullOrEmpty(pixelFormat))
            return false;
        return pixelFormat.StartsWith("rgb", StringComparison.Ordinal)
            || pixelFormat.StartsWith("bgr", StringComparison.Ordinal)
            || pixelFormat.StartsWith("gbr", StringComparison.Ordinal)
            || pixelFormat.StartsWith("argb", StringComparison.Ordinal)
            || pixelFormat.StartsWith("abgr", StringComparison.Ordinal)
            || pixelFormat.StartsWith("0rgb", StringComparison.Ordinal)
            || pixelFormat.StartsWith("0bgr", StringComparison.Ordinal)
            || pixelFormat.StartsWith("x2rgb", StringComparison.Ordinal)
            || pixelFormat.StartsWith("x2bgr", StringComparison.Ordinal)
            || pixelFormat.StartsWith("gray", StringComparison.Ordinal)
            || pixelFormat.StartsWith("ya", StringComparison.Ordinal)
            || pixelFormat.StartsWith("pal", StringComparison.Ordinal)
            || pixelFormat.StartsWith("monow", StringComparison.Ordinal)
            || pixelFormat.StartsWith("monob", StringComparison.Ordinal);
    }

    /// <summary>"yuvj420p" style formats are full range by definition.</summary>
    public static bool IsFullRangeYuv(string? pixelFormat) => pixelFormat?.StartsWith("yuvj", StringComparison.Ordinal) == true;

    /// <summary>Bits per component guessed from the name ("yuv422p10le" → 10, "rgba64le" → 16, "rgba" → 8).</summary>
    public static int BitDepth(string? pixelFormat)
    {
        if (string.IsNullOrEmpty(pixelFormat))
            return 8;
        if (pixelFormat.Contains("64", StringComparison.Ordinal) || pixelFormat.Contains("48", StringComparison.Ordinal))
            return 16;
        if (pixelFormat.Contains("f32", StringComparison.Ordinal) || pixelFormat.Contains("f16", StringComparison.Ordinal))
            return 16;
        var match = DepthPattern().Match(pixelFormat);
        return match.Success && int.TryParse(match.Groups[1].Value, out var bits) ? bits : 8;
    }

    [GeneratedRegex(@"p(9|10|12|14|16)(le|be)?$")]
    private static partial Regex DepthPattern();
}
