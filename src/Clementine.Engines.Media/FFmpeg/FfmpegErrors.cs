namespace Clementine.Engines.Media.FFmpeg;

/// <summary>Maps FFmpeg's stderr to a short, friendly sentence for the job toast (the raw text goes to "Details").</summary>
public static class FfmpegErrors
{
    static readonly (string[] Needles, string Message)[] Rules =
    [
        (["No space left on device"], "The disk is full. Free some space and try again."),
        (["Permission denied", "Access is denied"], "Windows refused access to the file or folder. Check that it isn't read-only or open in another app."),
        (["No such file or directory"], "FFmpeg couldn't find the file — it may have been moved or renamed."),
        (["moov atom not found"], "This video is incomplete (the recording may have been interrupted), so it can't be read."),
        (["Invalid data found when processing input", "could not find codec parameters", "Error opening input"], "This file looks damaged or isn't a format FFmpeg can read."),
        (["Decoder (codec", "Decoding requested, but no decoder found", "decoder not found"], "FFmpeg can't decode this file's codec."),
        (["Unknown encoder", "Encoder not found", "Unrecognized option"], "This FFmpeg build is missing a required encoder. Install a full FFmpeg build (for example gyan.dev's) and try again."),
        (["is not multiple of", "Invalid picture size", "dimensions not set"], "The encoder rejected this picture size."),
        (["Could not find tag for codec", "not currently supported in container", "codec not currently supported"], "That container can't hold this codec. Choose a conversion that re-encodes instead."),
        (["matches no streams", "does not contain any stream", "Output file is empty"], "There was nothing to write — the file has no stream this conversion can use."),
        (["Cannot allocate memory", "Out of memory"], "FFmpeg ran out of memory. Try a shorter or smaller clip."),
        (["Error while opening encoder", "Could not open encoder"], "The encoder couldn't start with these settings."),
        (["Subtitle encoding currently only possible from text to text", "bitmap to text"], "Picture-based subtitles (DVD/Blu-ray) can't be converted to text."),
        (["Error initializing filter", "Error reinitializing filters", "Failed to configure"], "FFmpeg couldn't set up the video processing for this file."),
        (["Invalid argument"], "FFmpeg rejected the conversion settings for this file."),
    ];

    /// <summary>The friendliest message whose pattern appears in <paramref name="stderr"/>, or a generic one.</summary>
    public static string Describe(string? stderr, string fallback = "FFmpeg couldn't process this file.")
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return fallback;
        foreach (var (needles, message) in Rules)
        {
            if (needles.Any(n => stderr.Contains(n, StringComparison.OrdinalIgnoreCase)))
                return message;
        }
        return fallback;
    }
}
