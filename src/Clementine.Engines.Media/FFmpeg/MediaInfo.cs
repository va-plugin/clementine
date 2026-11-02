namespace Clementine.Engines.Media.FFmpeg;

/// <summary>What ffprobe found in a media file: container facts plus every stream, grouped by kind.</summary>
public sealed record MediaInfo
{
    public required string Path { get; init; }

    /// <summary>FFmpeg demuxer name(s), e.g. "mov,mp4,m4a,3gp,3g2,mj2" or "matroska,webm".</summary>
    public string FormatName { get; init; } = "";

    public string? FormatLongName { get; init; }

    public TimeSpan? Duration { get; init; }

    /// <summary>File size in bytes.</summary>
    public long Size { get; init; }

    /// <summary>Overall bit rate in bits per second.</summary>
    public long? BitRate { get; init; }

    /// <summary>Real video streams (cover art is in <see cref="AttachedPictures"/>).</summary>
    public IReadOnlyList<VideoStreamInfo> VideoStreams { get; init; } = [];

    /// <summary>Embedded cover art / thumbnails (video streams with the attached_pic disposition).</summary>
    public IReadOnlyList<VideoStreamInfo> AttachedPictures { get; init; } = [];

    public IReadOnlyList<AudioStreamInfo> AudioStreams { get; init; } = [];

    public IReadOnlyList<SubtitleStreamInfo> SubtitleStreams { get; init; } = [];

    /// <summary>Data, attachment and unknown streams (timecode tracks, fonts in MKV…).</summary>
    public IReadOnlyList<OtherStreamInfo> OtherStreams { get; init; } = [];

    public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>();

    public int ChapterCount { get; init; }

    /// <summary>The first real video stream, or null for audio-only files.</summary>
    public VideoStreamInfo? Video => VideoStreams.Count > 0 ? VideoStreams[0] : null;

    /// <summary>The first audio stream.</summary>
    public AudioStreamInfo? Audio => AudioStreams.Count > 0 ? AudioStreams[0] : null;

    public bool HasVideo => VideoStreams.Count > 0;

    public bool HasAudio => AudioStreams.Count > 0;

    /// <summary>Duration, or zero when the container doesn't say.</summary>
    public TimeSpan DurationOrZero => Duration ?? Video?.Duration ?? Audio?.Duration ?? TimeSpan.Zero;

    public bool IsFormat(string name) =>
        FormatName.Split(',').Any(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Facts every stream shares.</summary>
public abstract record StreamInfo
{
    /// <summary>Absolute stream index in the file (what "-map 0:N" refers to).</summary>
    public int Index { get; init; }

    /// <summary>FFmpeg codec name ("h264", "prores", "hap", "pcm_s24le").</summary>
    public string Codec { get; init; } = "";

    public string? CodecLongName { get; init; }

    /// <summary>FourCC as a string ("avc1", "apch", "Hap5", "DXD3").</summary>
    public string? CodecTag { get; init; }

    public string? Profile { get; init; }

    public TimeSpan? Duration { get; init; }

    public long? BitRate { get; init; }

    public string? Language { get; init; }

    public string? Title { get; init; }

    public bool IsDefault { get; init; }

    public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>();
}

/// <summary>A video stream (or cover-art picture).</summary>
public sealed record VideoStreamInfo : StreamInfo
{
    /// <summary>Coded width in pixels (before rotation and pixel-aspect correction).</summary>
    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>Sample (pixel) aspect ratio; <see cref="Rational.Zero"/> when unknown (treated as square).</summary>
    public Rational SampleAspectRatio { get; init; }

    /// <summary>Average frame rate (avg_frame_rate).</summary>
    public Rational FrameRate { get; init; }

    /// <summary>Base frame rate the timestamps are built on (r_frame_rate).</summary>
    public Rational RealFrameRate { get; init; }

    public Rational TimeBase { get; init; }

    public string? PixelFormat { get; init; }

    /// <summary>The stream carries transparency (alpha pixel format, or a VP8/VP9 "alpha_mode" side channel).</summary>
    public bool HasAlpha { get; init; }

    public int BitDepth { get; init; } = 8;

    /// <summary>Display rotation in degrees from the display matrix (0, 90, 180, 270; counter-clockwise like FFmpeg).</summary>
    public int Rotation { get; init; }

    /// <summary>"progressive", "tt", "bb", "tb", "bt" or null.</summary>
    public string? FieldOrder { get; init; }

    public string? ColorPrimaries { get; init; }

    public string? ColorTransfer { get; init; }

    /// <summary>YUV matrix ("bt709", "smpte170m", "bt2020nc"…).</summary>
    public string? ColorSpace { get; init; }

    /// <summary>"tv" (limited) or "pc" (full).</summary>
    public string? ColorRange { get; init; }

    public long? FrameCount { get; init; }

    public bool IsAttachedPicture { get; init; }

    /// <summary>
    /// Interlaced per the container, or DV whose container doesn't say (DV in AVI reports "unknown"): DV is coded as
    /// interlaced fields, and the deinterlacer only touches frames the decoder flags as interlaced anyway.
    /// </summary>
    public bool IsInterlaced => FieldOrder is "tt" or "bb" or "tb" or "bt" || (Codec == "dvvideo" && FieldOrder is null or "unknown");

    /// <summary>True when rotation swaps width and height on display.</summary>
    public bool IsRotatedSideways => Math.Abs(Rotation % 180) == 90;

    /// <summary>Square-pixel width as it appears on screen (after pixel aspect and rotation).</summary>
    public int DisplayWidth => IsRotatedSideways ? SquareHeight : SquareWidth;

    public int DisplayHeight => IsRotatedSideways ? SquareWidth : SquareHeight;

    /// <summary>True when the pixels aren't square (anamorphic DV/DVD…).</summary>
    public bool HasNonSquarePixels =>
        SampleAspectRatio.IsValid && SampleAspectRatio.Numerator != SampleAspectRatio.Denominator;

    int SquareWidth => HasNonSquarePixels
        ? Math.Max(2, (int)Math.Round(Width * SampleAspectRatio.Value))
        : Width;

    int SquareHeight => Height;

    /// <summary>Best frames-per-second figure (average, falling back to the base rate, then 30).</summary>
    public double Fps => FrameRate.IsValid && FrameRate.Value < 1000 && !AverageCountsEmptyFrames ? FrameRate.Value
        : RealFrameRate.IsValid && RealFrameRate.Value < 1000 ? RealFrameRate.Value
        : 30;

    /// <summary>
    /// The average rate is above any real video rate while the base rate is sane: the frame count includes empty
    /// "dropped frame" chunks (an AVI remuxed from MOV gets a 1/600 time base and reports 600 fps). The base rate is the
    /// real one — forcing CFR at the average would write 24 copies of every frame.
    /// </summary>
    public bool AverageCountsEmptyFrames =>
        FrameRate.IsValid && FrameRate.Value > 240 && RealFrameRate.IsValid && RealFrameRate.Value is > 0 and <= 240;

    /// <summary>The frame rate as an FFmpeg rational, preferring the exact base rate when the stream is constant-rate.</summary>
    public Rational BestFrameRate => !IsVariableFrameRate && RealFrameRate.IsValid && RealFrameRate.Value < 1000
        ? RealFrameRate
        : FrameRate.IsValid && FrameRate.Value < 1000 ? FrameRate : Rational.FromDouble(Fps);

    /// <summary>Average and base rates disagree: phone recordings, screen captures and other variable-frame-rate video.</summary>
    public bool IsVariableFrameRate => !AverageCountsEmptyFrames &&
        FrameRate.IsValid && RealFrameRate.IsValid && Math.Abs(FrameRate.Value - RealFrameRate.Value) / RealFrameRate.Value > 0.01;

    /// <summary>
    /// The time base can't represent the frame rate exactly (MKV/WebM/FLV's 1/1000 at 29.97 or 30 fps), which leaves
    /// intra-frame codecs with jittery frame durations unless the output is forced to a constant rate.
    /// </summary>
    public bool HasIrregularTimeBase
    {
        get
        {
            if (!TimeBase.IsValid || !(Fps > 0))
                return false;
            var ticksPerFrame = TimeBase.Denominator / (TimeBase.Numerator * BestFrameRate.Value);
            return Math.Abs(ticksPerFrame - Math.Round(ticksPerFrame)) > 0.01;
        }
    }
}

/// <summary>An audio stream.</summary>
public sealed record AudioStreamInfo : StreamInfo
{
    public int SampleRate { get; init; }

    public int Channels { get; init; }

    /// <summary>"stereo", "5.1(side)", "mono"…</summary>
    public string? ChannelLayout { get; init; }

    /// <summary>"fltp", "s16", "s32"…</summary>
    public string? SampleFormat { get; init; }

    /// <summary>Bits per sample for PCM/lossless sources (0 when unknown or lossy).</summary>
    public int BitsPerSample { get; init; }

    public bool IsPcm => Codec.StartsWith("pcm_", StringComparison.Ordinal);
}

/// <summary>A subtitle stream.</summary>
public sealed record SubtitleStreamInfo : StreamInfo
{
    static readonly HashSet<string> TextCodecs = new(StringComparer.Ordinal)
    {
        "subrip", "srt", "ass", "ssa", "webvtt", "mov_text", "text", "microdvd", "subviewer", "subviewer1", "sami", "realtext", "stl", "jacosub", "mpl2", "pjs", "vplayer",
    };

    /// <summary>Text-based (convertible to SRT); bitmap formats like PGS or VobSub are not.</summary>
    public bool IsText => TextCodecs.Contains(Codec);
}

/// <summary>A data, attachment or unknown stream.</summary>
public sealed record OtherStreamInfo : StreamInfo
{
    /// <summary>"data", "attachment" or whatever ffprobe reported.</summary>
    public string Kind { get; init; } = "data";
}
