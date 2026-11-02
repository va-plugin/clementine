namespace Clementine.Engines.Media.Encoders;

/// <summary>
/// The FFmpeg encoders a fixed target can't work without, checked against the build's encoder list
/// (<see cref="FFmpeg.FfmpegEncoders"/>). PCM is part of every build and isn't listed.
/// </summary>
public static class EncoderRequirements
{
    /// <summary>The software video encoder (the GPU path falls back to it) plus the audio encoder, if the target has sound.</summary>
    public static IReadOnlyList<string> For(VideoTarget target)
    {
        var encoders = new List<string> { VideoEncodePlanner.SoftwareEncoderName(target) };
        if (target.SupportsAudio && AudioEncoder(target.Audio) is { } audio)
            encoders.Add(audio);
        return encoders;
    }

    public static IReadOnlyList<string> For(AudioTarget target) =>
        target.Encoder.StartsWith("pcm_", StringComparison.Ordinal) ? [] : [target.Encoder];

    static string? AudioEncoder(VideoAudioCodec codec) => codec switch
    {
        VideoAudioCodec.Aac => "aac",
        VideoAudioCodec.Opus => "libopus",
        VideoAudioCodec.Mp3 => "libmp3lame",
        VideoAudioCodec.Wma => "wmav2",
        _ => null,
    };
}
