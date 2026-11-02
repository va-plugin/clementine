using Clementine.Engines.Media.FFmpeg;

namespace Clementine.Engines.Media.Encoders;

/// <summary>
/// Picks the encoder a tool re-encodes with so the result matches the source: ProRes stays the same ProRes profile,
/// HAP the same HAP format, DXV stays DXV, H.264/H.265/VP9/AV1 stay themselves; anything else becomes H.264 MP4.
/// </summary>
public static class MatchingTargets
{
    public static VideoTarget For(MediaInfo source, string inputExtension)
    {
        var video = source.Video!;
        var hasAudio = source.HasAudio;
        var family = MediaInputs.Family(inputExtension);
        switch (video.Codec)
        {
            case "prores":
                var profile = ProResProfileOf(video);
                return VideoTarget.ProResOf(profile, hasAudio) with
                {
                    AlphaMode = profile >= ProResProfile.P4444 && video.HasAlpha ? AlphaMode.Required : AlphaMode.None,
                };
            case "hap":
                return VideoTarget.HapOf(HapFormatOf(video), hasAudio);
            case "dxv":
                return VideoTarget.DxvOf(hasAudio);
            case "h264":
                return VideoTarget.H264(Keep(family, ".mp4", ".mov", ".mkv"));
            case "hevc":
                return VideoTarget.Hevc(Keep(family, ".mp4", ".mov", ".mkv"));
            case "vp9":
                return VideoTarget.Vp9(video.HasAlpha, Keep(family, ".webm", ".mkv"));
            case "av1":
                return VideoTarget.Av1(Keep(family, ".webm", ".mkv", ".mp4"));
            default:
                return VideoTarget.H264(".mp4");
        }
    }

    /// <summary>ProRes profile from the FourCC (or ffprobe's profile name).</summary>
    public static ProResProfile ProResProfileOf(VideoStreamInfo video) => video.CodecTag switch
    {
        "apco" => ProResProfile.Proxy,
        "apcs" => ProResProfile.Lt,
        "apcn" => ProResProfile.Standard,
        "apch" => ProResProfile.Hq,
        "ap4h" => ProResProfile.P4444,
        "ap4x" => ProResProfile.P4444Xq,
        _ => video.Profile?.ToUpperInvariant() switch
        {
            "PROXY" => ProResProfile.Proxy,
            "LT" => ProResProfile.Lt,
            "STANDARD" => ProResProfile.Standard,
            "4444" => ProResProfile.P4444,
            "XQ" or "4444 XQ" => ProResProfile.P4444Xq,
            _ => ProResProfile.Hq,
        },
    };

    /// <summary>HAP flavour from the FourCC. HAP Q Alpha can't be encoded by FFmpeg, so it maps to HAP Alpha.</summary>
    public static HapFormat HapFormatOf(VideoStreamInfo video) => video.CodecTag switch
    {
        "Hap5" or "HapM" or "HapA" => HapFormat.HapAlpha,
        "HapY" => HapFormat.HapQ,
        _ => HapFormat.Hap,
    };

    /// <summary>True for codecs where every frame is a keyframe (stream-copy cuts are frame-exact).</summary>
    public static bool IsIntraOnly(VideoStreamInfo video) =>
        video.Codec is "prores" or "hap" or "dxv" or "mjpeg" or "png" or "qtrle" or "dnxhd" or "cfhd" or "rawvideo" or "dvvideo" or "ffv1" or "utvideo" or "gif";

    static string Keep(string family, params string[] allowed) => allowed.Contains(family) ? family : allowed[0];
}
