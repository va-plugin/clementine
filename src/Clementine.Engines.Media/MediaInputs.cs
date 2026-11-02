using Clementine.Core;

namespace Clementine.Engines.Media;

/// <summary>Extension-only checks for what media operations accept (never opens the file).</summary>
public static class MediaInputs
{
    /// <summary>Animated image formats that video conversions accept.</summary>
    public static readonly IReadOnlySet<string> AnimatedImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".gif", ".apng" };

    /// <summary>Stills that "Image sequence → video" accepts.</summary>
    public static readonly IReadOnlySet<string> SequenceImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".bmp", ".exr", ".tga", ".webp",
    };

    /// <summary>Subtitle files that can be burned into a video.</summary>
    public static readonly IReadOnlySet<string> BurnableSubtitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".srt", ".ass", ".ssa", ".vtt" };

    public static bool IsVideo(InputFile file) => !file.IsDirectory && file.HasExtension(FileTypes.Video);

    /// <summary>Videos plus animated GIF/APNG (for format conversions).</summary>
    public static bool IsVideoOrAnimation(InputFile file) => IsVideo(file) || (!file.IsDirectory && file.HasExtension(AnimatedImages));

    public static bool IsAudio(InputFile file) => !file.IsDirectory && file.HasExtension(FileTypes.Audio);

    public static bool IsSequenceImage(InputFile file) => !file.IsDirectory && file.HasExtension(SequenceImages);

    public static bool IsBurnableSubtitle(InputFile file) => !file.IsDirectory && file.HasExtension(BurnableSubtitles);

    /// <summary>Normalises extension aliases so "same format" checks work (".aif" ≡ ".aiff", ".wave" ≡ ".wav").</summary>
    public static string Family(string extension) => extension.ToLowerInvariant() switch
    {
        ".aif" or ".aifc" => ".aiff",
        ".wave" => ".wav",
        ".oga" => ".ogg",
        ".qt" => ".mov",
        ".m4v" => ".mp4",
        var other => other,
    };
}
