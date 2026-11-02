using Clementine.Core;
using Clementine.Engines.Media.Audio;
using Clementine.Engines.Media.Encoders;
using Clementine.Engines.Media.Video;

namespace Clementine.Engines.Media.Catalog;

/// <summary>The Convert-wheel entries for videos (and animated GIF/APNG): everyday formats, VJ codecs, sound-track extraction.</summary>
internal static class VideoConversions
{
    const string NoDxvAlpha = "No alpha — use HAP Alpha or ProRes 4444 for transparency.";

    /// <summary>Name label = the target's own codec name ("DXV", "HAP Alpha", "ProRes 422 HQ").</summary>
    static readonly string? CodecName = null;

    public static IEnumerable<IOperation> Create(MediaToolkit kit)
    {
        // MP4
        yield return Convert(kit, "video.mp4.h264", "MP4", "H.264", 10, "MP4 · H.264", VideoTarget.H264(".mp4") with { WebSafeAudio = true }, "H.264",
            "Plays everywhere: H.264 video with AAC audio, optimised for the web.");
        yield return Convert(kit, "video.mp4.h264.noaudio", "MP4", "H.264 · no audio", 11, "MP4 · H.264 · no audio", VideoTarget.H264(".mp4", audio: false), "H.264",
            "H.264 MP4 without the sound track.", accepts: RealVideo);
        yield return Convert(kit, "video.mp4.h265", "MP4", "H.265", 12, "MP4 · H.265 (HEVC)", VideoTarget.Hevc(".mp4") with { WebSafeAudio = true }, "H.265",
            "About half the size of H.264 at the same quality; needs a newer player.", label: true);
        yield return Convert(kit, "video.mp4.h265.noaudio", "MP4", "H.265 · no audio", 13, "MP4 · H.265 (HEVC) · no audio", VideoTarget.Hevc(".mp4", audio: false), "H.265",
            "H.265 MP4 without the sound track.", accepts: RealVideo, label: true);

        // MOV
        yield return Convert(kit, "video.mov.h264", "MOV", "H.264", 20, "MOV · H.264", VideoTarget.H264(".mov"), "H.264",
            "H.264 with AAC audio in a QuickTime container, handy for editors and Apple apps.", label: true);
        yield return new RemuxOperation(Info("video.mov.remux", "MOV", "Remux (no re-encode)", 21, "MOV · remux (no re-encode)",
            "Rewraps the existing streams as MOV in seconds — no quality loss.", ".mov"), kit, RemuxContainer.Mov);

        // MKV
        yield return new RemuxOperation(Info("video.mkv.remux", "MKV", "Remux (no re-encode)", 30, "MKV · remux (no re-encode)",
            "Rewraps every stream — all audio tracks and subtitles — as MKV without re-encoding.", ".mkv"), kit, RemuxContainer.Mkv);
        yield return Convert(kit, "video.mkv.h264", "MKV", "H.264", 31, "MKV · H.264", VideoTarget.H264(".mkv"), "H.264",
            "Re-encodes to H.264 in a Matroska container.", accepts: RealVideo, label: true);

        // WebM
        yield return Convert(kit, "video.webm.vp9", "WebM", "VP9", 40, "WebM · VP9", VideoTarget.Vp9(), "VP9",
            "Open web format: VP9 video with Opus audio.");
        yield return Convert(kit, "video.webm.vp9.alpha", "WebM", "VP9 · alpha", 41, "WebM · VP9 · alpha", VideoTarget.Vp9(alpha: true), "VP9 alpha",
            "VP9 with a transparency channel, for web overlays.", label: true);
        yield return Convert(kit, "video.webm.av1", "WebM", "AV1", 42, "WebM · AV1", VideoTarget.Av1(), "AV1",
            "The smallest files of all; slower to encode and needs a modern browser.", label: true);

        // GIF / WebP
        yield return Convert(kit, "video.gif.hq", "GIF", "High quality", 50, "GIF · high quality", VideoTarget.Gif(maxWidth: 1280), "GIF",
            "Looping GIF with an optimised palette at 15 fps, up to 1280 px wide.", "gif", NotGif);
        yield return Convert(kit, "video.gif.small", "GIF", "Small (480 px)", 51, "GIF · small (480 px)", VideoTarget.Gif(maxWidth: 480), "480 px",
            "Looping GIF scaled to 480 px wide — good for chat and email.", "gif", NotGif, label: true);
        yield return Convert(kit, "video.webp.animated", "WebP", "Animated", 60, "Animated WebP", VideoTarget.WebP(), "WebP",
            "Looping animated WebP: far smaller than GIF and keeps transparency.", "gif", NotGif);

        // Sound track
        yield return new AudioConvertOperation(Info("video.audio.mp3", "Audio", "MP3", 80, "Extract audio · MP3",
            "Saves the sound track as a 320 kbps MP3.", ".mp3", "audio", OperationWeight.Light), kit, AudioTarget.Mp3(320), AudioSourceKind.VideoSoundTrack);
        yield return new AudioConvertOperation(Info("video.audio.wav", "Audio", "WAV", 81, "Extract audio · WAV",
            "Saves the sound track as uncompressed WAV.", ".wav", "audio", OperationWeight.Light), kit, AudioTarget.Wav(16), AudioSourceKind.VideoSoundTrack, AudioTarget.WavFor);
        yield return new AudioConvertOperation(Info("video.audio.m4a", "Audio", "M4A", 82, "Extract audio · M4A",
            "Saves the sound track as AAC in M4A (copied untouched when it already is AAC).", ".m4a", "audio", OperationWeight.Light), kit, AudioTarget.Aac(256), AudioSourceKind.VideoSoundTrack);
        yield return new AudioConvertOperation(Info("video.audio.flac", "Audio", "FLAC", 83, "Extract audio · FLAC",
            "Saves the sound track as lossless FLAC.", ".flac", "audio", OperationWeight.Light), kit, AudioTarget.Flac(), AudioSourceKind.VideoSoundTrack);

        // DXV (Resolume)
        yield return Convert(kit, "video.dxv.audio", "DXV", "With audio", 100, "DXV3 · with audio", VideoTarget.DxvOf(audio: true), CodecName,
            $"Resolume DXV3 Normal Quality with 16-bit PCM audio. {NoDxvAlpha}", "vj", label: true);
        yield return Convert(kit, "video.dxv.noaudio", "DXV", "No audio", 101, "DXV3 · no audio", VideoTarget.DxvOf(audio: false), CodecName,
            $"Resolume DXV3 Normal Quality, video only. {NoDxvAlpha}", "vj", accepts: RealVideo, label: true);

        // HAP
        yield return Convert(kit, "video.hap.audio", "HAP", "HAP", 110, "HAP · with audio", VideoTarget.HapOf(HapFormat.Hap, audio: true), CodecName,
            "GPU-decoded HAP with 16-bit PCM audio — very light on the CPU in Resolume.", "vj", label: true);
        yield return Convert(kit, "video.hap.noaudio", "HAP", "HAP · no audio", 111, "HAP · no audio", VideoTarget.HapOf(HapFormat.Hap, audio: false), CodecName,
            "GPU-decoded HAP, video only.", "vj", accepts: RealVideo, label: true);
        yield return Convert(kit, "video.hap-alpha.audio", "HAP", "HAP Alpha", 112, "HAP Alpha · with audio", VideoTarget.HapOf(HapFormat.HapAlpha, audio: true), CodecName,
            "HAP Alpha keeps transparency; with 16-bit PCM audio.", "vj", label: true);
        yield return Convert(kit, "video.hap-alpha.noaudio", "HAP", "HAP Alpha · no audio", 113, "HAP Alpha · no audio", VideoTarget.HapOf(HapFormat.HapAlpha, audio: false), CodecName,
            "HAP Alpha keeps transparency; video only.", "vj", accepts: RealVideo, label: true);
        yield return Convert(kit, "video.hap-q.audio", "HAP", "HAP Q", 114, "HAP Q · with audio", VideoTarget.HapOf(HapFormat.HapQ, audio: true), CodecName,
            "Higher-quality HAP Q — cleaner gradients, bigger files; with audio.", "vj", label: true);
        yield return Convert(kit, "video.hap-q.noaudio", "HAP", "HAP Q · no audio", 115, "HAP Q · no audio", VideoTarget.HapOf(HapFormat.HapQ, audio: false), CodecName,
            "Higher-quality HAP Q, video only.", "vj", accepts: RealVideo, label: true);

        // ProRes
        yield return Convert(kit, "video.prores.422hq.audio", "ProRes", "422 HQ", 120, "ProRes 422 HQ · with audio", VideoTarget.ProResOf(ProResProfile.Hq, audio: true), CodecName,
            "ProRes 422 HQ with 24-bit PCM audio — the high-quality editing master.", "vj", label: true);
        yield return Convert(kit, "video.prores.422hq.noaudio", "ProRes", "422 HQ · no audio", 121, "ProRes 422 HQ · no audio", VideoTarget.ProResOf(ProResProfile.Hq, audio: false), CodecName,
            "ProRes 422 HQ, video only.", "vj", accepts: RealVideo, label: true);
        yield return Convert(kit, "video.prores.4444.audio", "ProRes", "4444 α", 122, "ProRes 4444 · alpha · with audio", VideoTarget.ProResOf(ProResProfile.P4444, audio: true), CodecName,
            "ProRes 4444 with alpha and 24-bit PCM audio — transparency at master quality.", "vj", label: true);
        yield return Convert(kit, "video.prores.4444.noaudio", "ProRes", "4444 α · no audio", 123, "ProRes 4444 · alpha · no audio", VideoTarget.ProResOf(ProResProfile.P4444, audio: false), CodecName,
            "ProRes 4444 with alpha, video only.", "vj", accepts: RealVideo, label: true);
        yield return Convert(kit, "video.prores.422.audio", "ProRes", "422", 124, "ProRes 422 · with audio", VideoTarget.ProResOf(ProResProfile.Standard, audio: true), CodecName,
            "Standard ProRes 422 with 24-bit PCM audio.", "vj", label: true);
        yield return Convert(kit, "video.prores.422lt.audio", "ProRes", "422 LT", 125, "ProRes 422 LT · with audio", VideoTarget.ProResOf(ProResProfile.Lt, audio: true), CodecName,
            "Lighter ProRes 422 LT with audio — smaller files, still editable.", "vj", label: true);
        yield return Convert(kit, "video.prores.422proxy.audio", "ProRes", "422 Proxy", 126, "ProRes 422 Proxy · with audio", VideoTarget.ProResOf(ProResProfile.Proxy, audio: true), CodecName,
            "ProRes 422 Proxy with audio — small files for offline editing.", "vj", label: true);
        yield return Convert(kit, "video.prores.4444xq.audio", "ProRes", "4444 XQ α", 127, "ProRes 4444 XQ · alpha · with audio", VideoTarget.ProResOf(ProResProfile.P4444Xq, audio: true), CodecName,
            "Highest-bitrate ProRes 4444 XQ with alpha and 24-bit PCM audio.", "vj", label: true);

        // Legacy
        yield return Convert(kit, "video.avi.mpeg4", "AVI", null, 200, "AVI · MPEG-4", VideoTarget.Mpeg4Avi(), "MPEG-4",
            "Legacy Xvid-compatible MPEG-4 AVI with MP3 audio, for old players.", accepts: RealVideo);
        yield return Convert(kit, "video.wmv", "WMV", null, 210, "WMV · Windows Media", VideoTarget.Wmv(), "WMV",
            "Windows Media Video for very old Windows software.", accepts: RealVideo);
    }

    static bool NotGif(InputFile file) => !file.HasExtension(".gif");

    // Legacy and container-only targets would just crowd the wheel for animated GIF/APNG inputs. GIF/APNG never carry
    // sound either, so the "· no audio" twins are video-only too: an animation gets the plain variant ("MP4 · H.264",
    // "HAP"), which writes video only for it without the "no audio" warning.
    static bool RealVideo(InputFile file) => MediaInputs.IsVideo(file);

    /// <param name="suffix">Short name label ("clip (H.265).mp4"); null = the codec's own name.</param>
    /// <param name="label">
    /// True for targets that share their extension with other targets on the wheel: the output is always labelled.
    /// False: labelled only when the input already has that extension.
    /// </param>
    static VideoConvertOperation Convert(
        MediaToolkit kit, string id, string group, string? variant, int order, string title, VideoTarget target, string? suffix,
        string description, string icon = "video", Func<InputFile, bool>? accepts = null, bool label = false) =>
        new(Info(id, group, variant, order, title, description, target.Extension, icon), kit, target, suffix ?? target.DisplayName, accepts, label);

    static OperationInfo Info(
        string id, string group, string? variant, int order, string title, string description, string extension,
        string icon = "video", OperationWeight weight = OperationWeight.Heavy) => new()
    {
        Id = id,
        Category = OperationCategory.Convert,
        Group = group,
        Variant = variant,
        Title = title,
        Description = description,
        IconKey = icon,
        OutputExtension = extension,
        Order = order,
        Weight = weight,
    };
}
