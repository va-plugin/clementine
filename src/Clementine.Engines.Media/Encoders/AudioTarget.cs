using Clementine.Engines.Media.FFmpeg;

namespace Clementine.Engines.Media.Encoders;

/// <summary>An audio output format: container extension, encoder and its settings.</summary>
public sealed record AudioTarget
{
    /// <summary>Output extension with a dot (".mp3").</summary>
    public required string Extension { get; init; }

    /// <summary>FFmpeg encoder ("libmp3lame", "pcm_s24le"…).</summary>
    public required string Encoder { get; init; }

    /// <summary>Encoder settings ("-b:a", "320k").</summary>
    public IReadOnlyList<string> EncoderArguments { get; init; } = [];

    /// <summary>Source codecs that can be stream-copied instead (when copying is allowed and nothing is filtered).</summary>
    public IReadOnlySet<string> CopyableCodecs { get; init; } = new HashSet<string>();

    /// <summary>Container holds embedded cover art (MP3, M4A, FLAC).</summary>
    public bool SupportsCoverArt { get; init; }

    /// <summary>Encoder channel limit (MP3/WMA: stereo).</summary>
    public int? MaxChannels { get; init; }

    /// <summary>Human name ("MP3 320 kbps").</summary>
    public required string DisplayName { get; init; }

    public static AudioTarget Mp3(int kbps) => new()
    {
        Extension = ".mp3",
        Encoder = "libmp3lame",
        EncoderArguments = ["-b:a", $"{kbps}k", "-id3v2_version", "3"],
        CopyableCodecs = Set("mp3"),
        SupportsCoverArt = true,
        MaxChannels = 2,
        DisplayName = $"MP3 {kbps} kbps",
    };

    public static AudioTarget Aac(int kbps, string extension = ".m4a") => new()
    {
        Extension = extension,
        Encoder = "aac",
        EncoderArguments = ["-b:a", $"{kbps}k"],
        CopyableCodecs = Set("aac"),
        SupportsCoverArt = extension is ".m4a" or ".m4b",
        DisplayName = $"AAC {kbps} kbps",
    };

    public static AudioTarget Alac() => new()
    {
        Extension = ".m4a",
        Encoder = "alac",
        CopyableCodecs = Set("alac"),
        SupportsCoverArt = true,
        DisplayName = "Apple Lossless",
    };

    public static AudioTarget Wav(int bits) => new()
    {
        Extension = ".wav",
        Encoder = bits >= 24 ? "pcm_s24le" : "pcm_s16le",
        CopyableCodecs = Set(bits >= 24 ? "pcm_s24le" : "pcm_s16le"),
        DisplayName = $"WAV {bits}-bit",
    };

    public static AudioTarget Aiff(int bits) => new()
    {
        Extension = ".aiff",
        Encoder = bits >= 24 ? "pcm_s24be" : "pcm_s16be",
        CopyableCodecs = Set(bits >= 24 ? "pcm_s24be" : "pcm_s16be"),
        DisplayName = $"AIFF {bits}-bit",
    };

    public static AudioTarget Flac() => new()
    {
        Extension = ".flac",
        Encoder = "flac",
        EncoderArguments = ["-compression_level", "5"],
        CopyableCodecs = Set("flac"),
        SupportsCoverArt = true,
        DisplayName = "FLAC",
    };

    public static AudioTarget OggVorbis() => new()
    {
        Extension = ".ogg",
        Encoder = "libvorbis",
        EncoderArguments = ["-q:a", "6"],
        CopyableCodecs = Set("vorbis"),
        DisplayName = "Ogg Vorbis",
    };

    public static AudioTarget Opus(int kbps, string extension = ".opus") => new()
    {
        Extension = extension,
        Encoder = "libopus",
        EncoderArguments = ["-b:a", $"{kbps}k"],
        CopyableCodecs = Set("opus"),
        DisplayName = $"Opus {kbps} kbps",
    };

    public static AudioTarget Wma() => new()
    {
        Extension = ".wma",
        Encoder = "wmav2",
        EncoderArguments = ["-b:a", "192k"],
        CopyableCodecs = Set("wmav2"),
        MaxChannels = 2,
        DisplayName = "WMA",
    };

    /// <summary>WAV at the source's precision: 24-bit for hi-res PCM/lossless sources, else 16-bit.</summary>
    public static AudioTarget WavFor(AudioStreamInfo audio) => Wav(IsHighResolution(audio) ? 24 : 16);

    /// <summary>
    /// The format a tool should write so the result matches the input ("trim an MP3 → MP3"): same container and
    /// codec family, at a quality at least as good as the source.
    /// </summary>
    public static AudioTarget MatchSource(MediaInfo source, string inputExtension)
    {
        var audio = source.Audio!;
        var family = MediaInputs.Family(inputExtension);
        return (family, audio.Codec) switch
        {
            (".mp3", _) => Mp3(StandardMp3Rate(audio.BitRate)),
            (".m4a" or ".m4b", "alac") => Alac() with { Extension = family },
            (".m4a" or ".m4b" or ".aac", _) => Aac(Math.Clamp(RoundKbps(audio.BitRate, 256), 128, 320), family),
            (".wav", _) => WavFor(audio),
            (".aiff", _) => Aiff(IsHighResolution(audio) ? 24 : 16),
            (".flac", _) => Flac(),
            (".ogg", "opus") => Opus(160, ".ogg"),
            (".ogg", _) => OggVorbis(),
            (".opus", _) => Opus(Math.Clamp(RoundKbps(audio.BitRate, 160), 64, 256)),
            (".wma", _) => Wma(),
            (_, "flac" or "alac" or "ape" or "wavpack" or "tta" or "truehd" or "mlp") => Flac(),
            _ when audio.IsPcm => WavFor(audio),
            _ => Aac(256),
        };
    }

    static bool IsHighResolution(AudioStreamInfo audio) =>
        audio.BitsPerSample > 16 || audio.Codec is "pcm_s24le" or "pcm_s24be" or "pcm_s32le" or "pcm_s32be" or "pcm_f32le" or "pcm_f32be" or "pcm_f64le";

    static int StandardMp3Rate(long? bitsPerSecond)
    {
        var kbps = RoundKbps(bitsPerSecond, 192);
        int[] rates = [128, 160, 192, 224, 256, 320];
        return rates.FirstOrDefault(r => r >= kbps, 320);
    }

    static int RoundKbps(long? bitsPerSecond, int fallback) =>
        bitsPerSecond is > 0 ? (int)Math.Round(bitsPerSecond.Value / 1000.0) : fallback;

    static IReadOnlySet<string> Set(params string[] codecs) => new HashSet<string>(codecs, StringComparer.Ordinal);
}
