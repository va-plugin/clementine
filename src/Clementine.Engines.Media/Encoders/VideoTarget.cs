namespace Clementine.Engines.Media.Encoders;

/// <summary>Video codecs Clementine writes.</summary>
public enum VideoCodec
{
    H264,
    Hevc,
    ProRes,
    Hap,
    Dxv,
    Vp9,
    Av1,
    Mpeg4,
    Wmv2,
    Gif,
    AnimatedWebP,
}

/// <summary>prores_ks profile numbers.</summary>
public enum ProResProfile
{
    Proxy = 0,
    Lt = 1,
    Standard = 2,
    Hq = 3,
    P4444 = 4,
    P4444Xq = 5,
}

/// <summary>Formats of the FFmpeg HAP encoder.</summary>
public enum HapFormat
{
    Hap,
    HapAlpha,
    HapQ,
}

/// <summary>How an encode treats transparency.</summary>
public enum AlphaMode
{
    /// <summary>No alpha channel: transparent sources are composited over black.</summary>
    None,

    /// <summary>Keep transparency when the source has it (GIF, WebP); opaque sources stay opaque.</summary>
    Preserve,

    /// <summary>Always write an alpha channel (ProRes 4444, HAP Alpha, VP9 alpha); fully opaque when the source has none.</summary>
    Required,
}

/// <summary>Audio codecs used inside video containers.</summary>
public enum VideoAudioCodec
{
    None,
    Aac,
    Opus,
    Mp3,
    Wma,
    Pcm16,
    Pcm24,
}

/// <summary>
/// What a video encode produces: container, codec and its knobs, and the audio that goes with it.
/// Instances are immutable; use the factory methods and <c>with</c> to tweak.
/// </summary>
public sealed record VideoTarget
{
    public required VideoCodec Codec { get; init; }

    /// <summary>Output extension with a dot (".mov").</summary>
    public required string Extension { get; init; }

    public ProResProfile ProRes { get; init; } = ProResProfile.Hq;

    public HapFormat Hap { get; init; } = HapFormat.Hap;

    public AlphaMode AlphaMode { get; init; }

    public VideoAudioCodec Audio { get; init; } = VideoAudioCodec.Aac;

    public int AudioBitrateKbps { get; init; } = 192;

    /// <summary>Copy the source audio untouched when it already suits the container and nothing changes it.</summary>
    public bool CopyCompatibleAudio { get; init; } = true;

    /// <summary>
    /// Only copy audio that browsers and phones play (AAC, MP3). Camera AC-3/E-AC-3, ALAC or PCM is re-encoded to AAC —
    /// MP4 conversions promise "plays everywhere", and Chrome/Firefox play AC-3 in MP4 silently.
    /// </summary>
    public bool WebSafeAudio { get; init; }

    /// <summary>Quality for CRF-style encoders (x264/x265/VP9/AV1); null = codec default.</summary>
    public int? Crf { get; init; }

    public string? Preset { get; init; }

    /// <summary>Average bit rate instead of constant quality (two-pass "target size" compression).</summary>
    public int? BitrateKbps { get; init; }

    /// <summary>1 or 2 for two-pass x264/x265 encodes (stats live in the run's working directory).</summary>
    public int? Pass { get; init; }

    /// <summary>Use NVENC/QSV/AMF when settings allow and a probe succeeds (H.264/H.265 only).</summary>
    public bool AllowHardware { get; init; }

    /// <summary>Cap on output frame rate (GIF 15 fps); the source rate is kept when lower.</summary>
    public double? MaxFps { get; init; }

    /// <summary>Cap on output width (GIF "Small"); aspect is kept.</summary>
    public int? MaxWidth { get; init; }

    /// <summary>Intra-frame "VJ" codecs: DXV, HAP, ProRes.</summary>
    public bool IsVjCodec => Codec is VideoCodec.Dxv or VideoCodec.Hap or VideoCodec.ProRes;

    /// <summary>Codecs encoded from RGB(A) pixels rather than YUV.</summary>
    public bool IsRgb => Codec is VideoCodec.Dxv or VideoCodec.Hap or VideoCodec.Gif or VideoCodec.AnimatedWebP;

    public bool SupportsAudio => Codec is not (VideoCodec.Gif or VideoCodec.AnimatedWebP) && Audio != VideoAudioCodec.None;

    /// <summary>Human name for status lines and warnings ("ProRes 4444", "HAP Alpha", "H.264").</summary>
    public string DisplayName => Codec switch
    {
        VideoCodec.H264 => "H.264",
        VideoCodec.Hevc => "H.265",
        VideoCodec.ProRes => ProRes switch
        {
            ProResProfile.Proxy => "ProRes 422 Proxy",
            ProResProfile.Lt => "ProRes 422 LT",
            ProResProfile.Standard => "ProRes 422",
            ProResProfile.Hq => "ProRes 422 HQ",
            ProResProfile.P4444 => "ProRes 4444",
            _ => "ProRes 4444 XQ",
        },
        VideoCodec.Hap => Hap switch
        {
            HapFormat.HapAlpha => "HAP Alpha",
            HapFormat.HapQ => "HAP Q",
            _ => "HAP",
        },
        VideoCodec.Dxv => "DXV",
        VideoCodec.Vp9 => "VP9",
        VideoCodec.Av1 => "AV1",
        VideoCodec.Mpeg4 => "MPEG-4",
        VideoCodec.Wmv2 => "WMV",
        VideoCodec.Gif => "GIF",
        _ => "animated WebP",
    };

    /// <summary>Rounding the encoder needs for width/height (HAP/DXV textures are 4×4 blocks; 4:2:0 needs even sizes).</summary>
    public int SizeMultiple => Codec switch
    {
        VideoCodec.Hap or VideoCodec.Dxv => 4,
        VideoCodec.Gif or VideoCodec.AnimatedWebP => 1,
        _ => 2,
    };

    public static VideoTarget H264(string extension = ".mp4", bool audio = true) => new()
    {
        Codec = VideoCodec.H264,
        Extension = extension,
        Audio = audio ? VideoAudioCodec.Aac : VideoAudioCodec.None,
        Crf = 20,
        Preset = "medium",
        AllowHardware = true,
    };

    public static VideoTarget Hevc(string extension = ".mp4", bool audio = true) => new()
    {
        Codec = VideoCodec.Hevc,
        Extension = extension,
        Audio = audio ? VideoAudioCodec.Aac : VideoAudioCodec.None,
        Crf = 23,
        Preset = "medium",
        AllowHardware = true,
    };

    public static VideoTarget ProResOf(ProResProfile profile, bool audio) => new()
    {
        Codec = VideoCodec.ProRes,
        Extension = ".mov",
        ProRes = profile,
        AlphaMode = profile >= ProResProfile.P4444 ? AlphaMode.Required : AlphaMode.None,
        Audio = audio ? VideoAudioCodec.Pcm24 : VideoAudioCodec.None,
        CopyCompatibleAudio = false,
    };

    public static VideoTarget HapOf(HapFormat format, bool audio) => new()
    {
        Codec = VideoCodec.Hap,
        Extension = ".mov",
        Hap = format,
        AlphaMode = format == HapFormat.HapAlpha ? AlphaMode.Required : AlphaMode.None,
        Audio = audio ? VideoAudioCodec.Pcm16 : VideoAudioCodec.None,
        CopyCompatibleAudio = false,
    };

    public static VideoTarget DxvOf(bool audio) => new()
    {
        Codec = VideoCodec.Dxv,
        Extension = ".mov",
        Audio = audio ? VideoAudioCodec.Pcm16 : VideoAudioCodec.None,
        CopyCompatibleAudio = false,
    };

    public static VideoTarget Vp9(bool alpha = false, string extension = ".webm") => new()
    {
        Codec = VideoCodec.Vp9,
        Extension = extension,
        AlphaMode = alpha ? AlphaMode.Required : AlphaMode.None,
        Audio = VideoAudioCodec.Opus,
        AudioBitrateKbps = 128,
        Crf = 32,
    };

    public static VideoTarget Av1(string extension = ".webm") => new()
    {
        Codec = VideoCodec.Av1,
        Extension = extension,
        Audio = extension == ".webm" ? VideoAudioCodec.Opus : VideoAudioCodec.Aac,
        AudioBitrateKbps = 128,
        Crf = 35,
        Preset = "8",
    };

    public static VideoTarget Mpeg4Avi() => new()
    {
        Codec = VideoCodec.Mpeg4,
        Extension = ".avi",
        Audio = VideoAudioCodec.Mp3,
        CopyCompatibleAudio = false,
    };

    public static VideoTarget Wmv() => new()
    {
        Codec = VideoCodec.Wmv2,
        Extension = ".wmv",
        Audio = VideoAudioCodec.Wma,
        CopyCompatibleAudio = false,
    };

    public static VideoTarget Gif(int? maxWidth) => new()
    {
        Codec = VideoCodec.Gif,
        Extension = ".gif",
        Audio = VideoAudioCodec.None,
        MaxFps = 15,
        MaxWidth = maxWidth,
        AlphaMode = AlphaMode.Preserve,
    };

    public static VideoTarget WebP() => new()
    {
        Codec = VideoCodec.AnimatedWebP,
        Extension = ".webp",
        Audio = VideoAudioCodec.None,
        MaxFps = 30,
        AlphaMode = AlphaMode.Preserve,
    };
}
