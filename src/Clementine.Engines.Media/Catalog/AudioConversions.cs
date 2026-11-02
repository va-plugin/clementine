using Clementine.Core;
using Clementine.Engines.Media.Audio;
using Clementine.Engines.Media.Encoders;

namespace Clementine.Engines.Media.Catalog;

/// <summary>
/// The Convert-wheel entries for audio files. Conversions to the input's own format are not offered. The first variant
/// of a format gets the plain name ("song.mp3"); the others are labelled ("song (128 kbps).mp3", "song (ALAC).m4a") so
/// converting one file several ways never yields an ambiguous "song (2).mp3".
/// </summary>
internal static class AudioConversions
{
    public static IEnumerable<IOperation> Create(MediaToolkit kit)
    {
        yield return Convert(kit, "audio.mp3.320", "MP3", "320 kbps", 10, "MP3 · 320 kbps", AudioTarget.Mp3(320), "Best-quality MP3; plays everywhere.");
        yield return Convert(kit, "audio.mp3.192", "MP3", "192 kbps", 11, "MP3 · 192 kbps", AudioTarget.Mp3(192), "Good-quality MP3 at about two-thirds the size.", "192 kbps");
        yield return Convert(kit, "audio.mp3.128", "MP3", "128 kbps", 12, "MP3 · 128 kbps", AudioTarget.Mp3(128), "Small MP3 for speech and previews.", "128 kbps");
        yield return Convert(kit, "audio.m4a.aac", "M4A", "AAC 256k", 20, "M4A · AAC 256 kbps", AudioTarget.Aac(256), "AAC in M4A — the Apple Music / iTunes standard.");
        yield return Convert(kit, "audio.m4a.alac", "M4A", "ALAC lossless", 21, "M4A · Apple Lossless", AudioTarget.Alac(), "Apple Lossless: bit-perfect and about half the size of WAV.", "ALAC");
        yield return Convert(kit, "audio.wav.16", "WAV", "16-bit", 30, "WAV · 16-bit", AudioTarget.Wav(16), "Uncompressed 16-bit WAV (CD quality) for any audio software.");
        yield return Convert(kit, "audio.wav.24", "WAV", "24-bit", 31, "WAV · 24-bit", AudioTarget.Wav(24), "Uncompressed 24-bit WAV for production work.", "24-bit");
        yield return Convert(kit, "audio.flac", "FLAC", null, 40, "FLAC", AudioTarget.Flac(), "Lossless FLAC: bit-perfect and about half the size of WAV.");
        yield return Convert(kit, "audio.ogg", "OGG", null, 50, "Ogg Vorbis", AudioTarget.OggVorbis(), "Open Ogg Vorbis at high quality (q6).");
        yield return Convert(kit, "audio.opus", "Opus", null, 60, "Opus · 160 kbps", AudioTarget.Opus(160), "Modern Opus: transparent quality at small sizes.");
        yield return Convert(kit, "audio.aiff.16", "AIFF", "16-bit", 70, "AIFF · 16-bit", AudioTarget.Aiff(16), "Uncompressed 16-bit AIFF for Mac audio software.");
        yield return Convert(kit, "audio.aiff.24", "AIFF", "24-bit", 71, "AIFF · 24-bit", AudioTarget.Aiff(24), "Uncompressed 24-bit AIFF for production work.", "24-bit");
    }

    static AudioConvertOperation Convert(MediaToolkit kit, string id, string group, string? variant, int order, string title, AudioTarget target, string description, string? nameLabel = null) =>
        new(new OperationInfo
        {
            Id = id,
            Category = OperationCategory.Convert,
            Group = group,
            Variant = variant,
            Title = title,
            Description = description,
            IconKey = "music",
            OutputExtension = target.Extension,
            Order = order,
        }, kit, target, AudioSourceKind.AudioFile, nameLabel: nameLabel);
}
