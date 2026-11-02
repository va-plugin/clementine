using System.Globalization;
using Clementine.Core;
using Clementine.Engines.Media.FFmpeg;

namespace Clementine.Engines.Media.Encoders;

/// <summary>The FFmpeg arguments for one encode plus what the caller needs to know about them.</summary>
public sealed record VideoEncodePlan(
    IReadOnlyList<string> Arguments,
    TimeSpan? ExpectedDuration,
    FrameSize OutputSize,
    IReadOnlyList<string> Warnings,
    string VideoEncoder);

/// <summary>
/// Turns (source, target, edit) into FFmpeg arguments: input options, one filter graph (deinterlace → square pixels →
/// tool filters → frame rate → colour-correct scale/format), encoder settings and audio handling. Pure — no I/O — so
/// every decision is unit-testable.
/// </summary>
public static class VideoEncodePlanner
{
    /// <summary>Warning added when an alpha variant gets an opaque source.</summary>
    public const string OpaqueAlphaWarning = "Source has no transparency — the alpha channel is fully opaque.";

    /// <summary>Warning added when an HDR source is tone-mapped for an 8-bit target.</summary>
    public const string ToneMappedWarning = "HDR video was tone-mapped to standard range (SDR) for this format.";

    /// <summary>Warning added when HDR (PQ/HLG) is kept in a ProRes output.</summary>
    public const string HdrKeptWarning =
        "HDR was kept — apps without HDR support (such as Resolume) show it washed out. Use HAP or DXV for a standard-range copy.";

    /// <summary>Message for camera RAW video, which FFmpeg can't develop (no white balance/ISO/debayer controls).</summary>
    public const string RawVideoMessage =
        "ProRes RAW can't be converted here — open it in Final Cut Pro, Premiere Pro or DaVinci Resolve and export ProRes 422 or 4444 first.";

    /// <summary>Warning added when a source with several sound tracks (camera/recorder MOVs) keeps only the first.</summary>
    public static string ExtraAudioTracksWarning(int count) => $"This file has {count} audio tracks; only the first was kept.";

    /// <summary>Warning added when a "with audio" variant gets a silent source.</summary>
    public const string NoAudioWarning = "Source has no audio — the output is video only.";

    /// <summary>PQ/HLG → linear → BT.709 primaries → Möbius tone curve (keeps in-range SDR colours) → BT.709 limited YUV.</summary>
    const string ToneMapChain =
        "zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=mobius:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv444p";

    static readonly double[] StandardRates = [23.976, 24, 25, 29.97, 30, 48, 50, 59.94, 60, 100, 119.88, 120];

    public static VideoEncodePlan Build(
        MediaInfo source,
        VideoTarget target,
        VideoEdit edit,
        string outputPath,
        ClementineSettings settings,
        HardwareEncoder? hardware = null)
    {
        var video = source.Video ?? throw new OperationException("This file has no video stream.");
        EnsureDecodable(video);
        var warnings = new List<string>();
        var args = new List<string>();
        var useHardware = hardware is not null && target.AllowHardware && settings.UseHardwareEncoding
            && hardware.IsHevc == (target.Codec == VideoCodec.Hevc) && target.Codec is VideoCodec.H264 or VideoCodec.Hevc;
        var hw = useHardware ? hardware : null;

        // ---- inputs
        args.AddRange(DecoderArguments(video));
        if (edit.Start is { } start && start > TimeSpan.Zero)
            args.AddRange(["-ss", Seconds(start)]);
        if (edit.Duration is { } length && length > TimeSpan.Zero)
            args.AddRange(["-t", Seconds(length)]);
        args.AddRange(edit.InputOptions);
        args.AddRange(["-i", source.Path]);

        // ---- video graph
        // HDR (PQ/HLG) only survives in 10-bit ProRes; every other target is 8-bit SDR and needs a tone map,
        // after which the frames are plain BT.709 YUV.
        var toneMap = IsHdr(video) && target.Codec != VideoCodec.ProRes;
        var frames = toneMap ? AfterToneMap(video) : video;
        if (toneMap)
            warnings.Add(ToneMappedWarning);
        else if (IsHdr(video) && video.Codec != "prores")
            warnings.Add(HdrKeptWarning);
        var keepAlpha = target.AlphaMode != AlphaMode.None && frames.HasAlpha;
        if (target.AlphaMode == AlphaMode.Required && !frames.HasAlpha)
            warnings.Add(OpaqueAlphaWarning);

        var graph = new List<string>();
        var input = $"[0:{video.Index}]";
        var pre = PreFilters(video).ToList();

        List<string> chain;
        string chainInput;
        if (edit.VideoGraph is { } custom)
        {
            var customInput = input;
            if (pre.Count > 0)
            {
                graph.Add($"{input}{string.Join(',', pre)}[pre]");
                customInput = "[pre]";
            }
            graph.Add(custom(customInput));
            chain = [];
            chainInput = "[edited]";
        }
        else
        {
            chain = [.. pre, .. edit.VideoFilters];
            chainInput = input;
        }

        if (toneMap)
            chain.Add(ToneMapChain);

        var fps = edit.FrameRate;
        if (target.Codec == VideoCodec.Gif)
            fps = GifFrameRate(Math.Min(fps?.Value ?? video.Fps, target.MaxFps ?? 15));
        else if (fps is null && target.MaxFps is { } maxFps && video.Fps > maxFps + 0.01)
            fps = Rational.FromDouble(maxFps);
        // eof_action=pass: without it a one-frame clip (or the last frame of a short one) is dropped at the end of the
        // stream, and a 1-frame clip → GIF fails with "Error writing trailer".
        if (fps is { } rate)
            chain.Add($"fps={rate}:eof_action=pass");

        var outputSize = OutputSize(video, target, edit);
        var (stage, pixelFormat) = FinalStage(frames, target, outputSize, keepAlpha, hw);
        chain.AddRange(stage);

        if (target.Codec == VideoCodec.Gif)
            graph.Add($"{chainInput}{string.Join(',', chain)},split[g0][g1];[g0]palettegen=stats_mode=diff[gp];[g1][gp]paletteuse=dither=sierra2_4a[vout]");
        else
            graph.Add($"{chainInput}{string.Join(',', chain)}[vout]");

        // ---- audio graph
        var audio = source.Audio;
        var wantsAudio = target.SupportsAudio && !edit.DropAudio;
        string? audioMap = null;
        if (wantsAudio && audio is null && target.IsVjCodec && !source.IsFormat("gif") && !source.IsFormat("apng"))
            warnings.Add(NoAudioWarning);
        if (wantsAudio && audio is not null)
        {
            if (source.AudioStreams.Count > 1)
                warnings.Add(ExtraAudioTracksWarning(source.AudioStreams.Count));
            var audioInput = $"[0:{audio.Index}]";
            if (edit.AudioGraph is { } audioGraph)
            {
                graph.Add(audioGraph(audioInput));
                audioMap = "[edited_a]";
            }
            else if (edit.AudioFilters.Count > 0)
            {
                graph.Add($"{audioInput}{string.Join(',', edit.AudioFilters)}[aout]");
                audioMap = "[aout]";
            }
            else
            {
                audioMap = $"0:{audio.Index}";
            }
        }

        args.AddRange(["-filter_complex", string.Join(';', graph), "-map", "[vout]"]);
        if (audioMap is not null)
            args.AddRange(["-map", audioMap]);

        // ---- video encoder
        args.AddRange(VideoCodecArguments(target, keepAlpha, hw));
        if (pixelFormat is not null && target.Codec != VideoCodec.Gif)
            args.AddRange(["-pix_fmt", pixelFormat]);
        if (!target.IsRgb)
            args.AddRange(ColorTags(frames));
        if (edit.FrameRate is null && settings.ForceConstantFrameRate && target.IsVjCodec
            && (video.IsVariableFrameRate || video.HasIrregularTimeBase))
        {
            args.AddRange(["-fps_mode", "cfr", "-r", ConstantFrameRate(video).ToString()]);
        }

        // ---- audio encoder
        if (audioMap is not null && audio is not null)
            args.AddRange(AudioCodecArguments(target, audio, edit, settings, source));
        else
            args.Add("-an");
        args.Add("-sn");
        args.Add("-dn");

        // ---- container
        if (edit.Start is not null || edit.Duration is not null || Math.Abs(edit.Speed - 1) > 1e-6 || edit.VideoGraph is not null)
            args.AddRange(["-map_chapters", "-1"]);
        if (edit.OutputKind == EncodeOutputKind.File)
        {
            if (target.Extension is ".mp4" or ".m4v" || (target.Extension == ".mov" && !target.IsVjCodec))
                args.AddRange(["-movflags", "+faststart"]);
            if (target.Codec is VideoCodec.Gif or VideoCodec.AnimatedWebP)
                args.AddRange(["-loop", "0"]);
        }
        args.AddRange(edit.ExtraOutputArguments);
        if (edit.OutputKind == EncodeOutputKind.Null)
            args.AddRange(["-f", "null"]);
        args.Add(outputPath);

        return new VideoEncodePlan(args, ExpectedDuration(source, edit), outputSize, warnings, hw?.Name ?? SoftwareEncoderName(target));
    }

    /// <summary>Input options that make FFmpeg decode the stream fully: VP8/VP9 alpha needs libvpx (the native decoders drop it).</summary>
    public static IReadOnlyList<string> DecoderArguments(VideoStreamInfo video)
    {
        EnsureDecodable(video);
        return video.HasAlpha && video.Codec is "vp9" or "vp8" ? ["-c:v", video.Codec == "vp9" ? "libvpx-vp9" : "libvpx"] : [];
    }

    /// <summary>Clean-up every decode gets first: deinterlace flagged frames, make pixels square.</summary>
    public static IReadOnlyList<string> PreFilters(VideoStreamInfo video)
    {
        EnsureDecodable(video);
        var filters = new List<string>();
        if (video.IsInterlaced)
            filters.Add("bwdif=mode=send_frame:parity=auto:deint=interlaced");
        if (video.HasNonSquarePixels)
            filters.Add("scale='trunc(iw*sar/2)*2':ih,setsar=1");
        return filters;
    }

    /// <summary>Refuses sources FFmpeg can't turn into a usable picture (camera RAW), with a friendly message.</summary>
    public static void EnsureDecodable(VideoStreamInfo video)
    {
        if (video.Codec is "prores_raw" || video.CodecTag is "aprn" or "aprh")
            throw new OperationException(RawVideoMessage);
    }

    /// <summary>PQ (HDR10) or HLG transfer.</summary>
    public static bool IsHdr(VideoStreamInfo video) => video.ColorTransfer is "smpte2084" or "arib-std-b67";

    /// <summary>What the frames look like after <see cref="ToneMapChain"/>.</summary>
    static VideoStreamInfo AfterToneMap(VideoStreamInfo video) => video with
    {
        ColorSpace = "bt709",
        ColorPrimaries = "bt709",
        ColorTransfer = "bt709",
        ColorRange = "tv",
        PixelFormat = "yuv444p",
        HasAlpha = false,
        BitDepth = 8,
    };

    /// <summary>Colour-correct conversion to 8-bit RGB(A) at <paramref name="size"/> (snapshots, contact sheets); HDR is tone-mapped.</summary>
    public static string ToRgb(VideoStreamInfo video, FrameSize? size, bool keepAlpha)
    {
        if (IsHdr(video))
            return $"{ToneMapChain},{ToRgb(AfterToneMap(video), size, keepAlpha: false)}";
        var scale = size is { } s ? $"scale={s.Width}:{s.Height}:flags=bicubic" : "scale=flags=bicubic";
        var format = keepAlpha && video.HasAlpha ? "rgba" : "rgb24";
        if (PixelFormats.IsRgb(video.PixelFormat))
            return $"{scale},format={format}";
        return $"{scale}:in_color_matrix={ColorSpaces.SourceMatrix(video)}:in_range={ColorSpaces.SourceRange(video)}:out_range=full,format={format}";
    }

    /// <summary>Output size: the tool's size (or the upright display size), capped and rounded to what the codec accepts.</summary>
    public static FrameSize OutputSize(VideoStreamInfo video, VideoTarget target, VideoEdit edit)
    {
        var size = edit.ScaleTo ?? edit.FrameSize ?? new FrameSize(video.DisplayWidth, video.DisplayHeight);
        if (target.MaxWidth is { } maxWidth && size.Width > maxWidth)
            size = new FrameSize(maxWidth, Math.Max(1, (int)Math.Round(size.Height * (double)maxWidth / size.Width)));
        return new FrameSize(RoundTo(size.Width, target.SizeMultiple), RoundTo(size.Height, target.SizeMultiple));
    }

    /// <summary>Nearest multiple (ties go down so a 1001 px frame becomes 1000, never 1004).</summary>
    public static int RoundTo(int value, int multiple)
    {
        if (multiple <= 1)
            return Math.Max(1, value);
        var rounded = (value + multiple / 2 - 1) / multiple * multiple;
        return Math.Max(multiple, rounded);
    }

    /// <summary>Snaps a measured average rate to the nearest broadcast rate when within 1 % (29.91 → 30000/1001).</summary>
    public static Rational SnapFrameRate(double fps)
    {
        var nearest = StandardRates.MinBy(standard => Math.Abs(fps - standard) / standard);
        return Math.Abs(fps - nearest) / nearest < 0.01
            ? Rational.FromDouble(nearest)
            : Rational.FromDouble(Math.Round(fps, 3));
    }

    /// <summary>
    /// GIF frame delays are whole hundredths of a second, so 15 fps (6.67 cs) would play back at 16.7 fps.
    /// Pick the nearest rate that divides 100 exactly (15 → 100/7 ≈ 14.3 fps, 10 → 10, 12 → 12.5).
    /// </summary>
    public static Rational GifFrameRate(double fps)
    {
        var centiseconds = Math.Clamp((int)Math.Round(100 / Math.Max(1, fps)), 2, 100);
        return new Rational(100, centiseconds).Reduce();
    }

    /// <summary>The constant rate to force: the exact base rate for steady streams, the snapped average for VFR ones.</summary>
    public static Rational ConstantFrameRate(VideoStreamInfo video) =>
        !video.IsVariableFrameRate && video.RealFrameRate.IsValid && video.RealFrameRate.Value <= 240
            ? video.RealFrameRate
            : SnapFrameRate(video.Fps);

    static (List<string> Filters, string? PixelFormat) FinalStage(VideoStreamInfo video, VideoTarget target, FrameSize size, bool keepAlpha, HardwareEncoder? hw)
    {
        var filters = new List<string>();
        var sourceIsRgb = PixelFormats.IsRgb(video.PixelFormat);
        var inMatrix = ColorSpaces.SourceMatrix(video);
        var inRange = ColorSpaces.SourceRange(video);
        var flags = target.Codec == VideoCodec.Gif ? "lanczos" : "bicubic";

        // Transparent source into an opaque codec: premultiply = composite over black, then drop alpha.
        if (video.HasAlpha && !keepAlpha)
        {
            filters.Add(sourceIsRgb
                ? "format=rgba64le"
                : $"scale=in_color_matrix={inMatrix}:in_range={inRange}:out_range=full,format=rgba64le");
            filters.Add("premultiply=inplace=1,format=rgb48le");
            sourceIsRgb = true;
        }

        var scale = $"scale={size.Width}:{size.Height}:flags={flags}";
        var pixelFormat = PixelFormatFor(target, keepAlpha, hw);
        if (target.IsRgb && target.Codec != VideoCodec.AnimatedWebP)
        {
            filters.Add(sourceIsRgb ? scale : $"{scale}:in_color_matrix={inMatrix}:in_range={inRange}:out_range=full");
        }
        else
        {
            // WebP stores BT.601 YUV; everything else is tagged with (and converted to) the output matrix.
            var outMatrix = target.Codec == VideoCodec.AnimatedWebP ? "bt601" : ColorSpaces.OutputMatrix(video);
            filters.Add(sourceIsRgb
                ? $"{scale}:out_color_matrix={outMatrix}:out_range=limited"
                : $"{scale}:in_color_matrix={inMatrix}:in_range={inRange}:out_color_matrix={outMatrix}:out_range=limited");
        }
        filters.Add($"setsar=1,format={pixelFormat}");
        return (filters, pixelFormat);
    }

    static string PixelFormatFor(VideoTarget target, bool keepAlpha, HardwareEncoder? hw) => target.Codec switch
    {
        VideoCodec.H264 or VideoCodec.Hevc when hw is not null => HardwareEncoders.PixelFormat(hw),
        VideoCodec.ProRes when target.ProRes >= ProResProfile.P4444 =>
            target.AlphaMode == AlphaMode.Required || keepAlpha ? "yuva444p10le" : "yuv444p10le",
        VideoCodec.ProRes => "yuv422p10le",
        VideoCodec.Hap or VideoCodec.Dxv => "rgba",
        VideoCodec.Vp9 => target.AlphaMode == AlphaMode.Required || keepAlpha ? "yuva420p" : "yuv420p",
        VideoCodec.AnimatedWebP => keepAlpha ? "yuva420p" : "yuv420p",
        VideoCodec.Gif => keepAlpha ? "rgba" : "rgb24",
        _ => "yuv420p",
    };

    static IEnumerable<string> VideoCodecArguments(VideoTarget target, bool keepAlpha, HardwareEncoder? hw)
    {
        switch (target.Codec)
        {
            case VideoCodec.H264 when hw is not null:
                return ["-c:v", hw.Name, .. HardwareEncoders.QualityArguments(hw)];
            case VideoCodec.H264 when target.BitrateKbps is { } kbps:
                return ["-c:v", "libx264", "-preset", target.Preset ?? "medium", "-b:v", $"{Int(kbps)}k",
                    .. target.Pass is { } x264Pass ? new[] { "-pass", Int(x264Pass), "-passlogfile", "clementine-2pass" } : []];
            case VideoCodec.H264:
                return ["-c:v", "libx264", "-preset", target.Preset ?? "medium", "-crf", Int(target.Crf ?? 20)];
            case VideoCodec.Hevc when target.BitrateKbps is { } hevcKbps:
                return ["-c:v", "libx265", "-preset", target.Preset ?? "medium", "-b:v", $"{Int(hevcKbps)}k", "-tag:v", "hvc1", "-x265-params",
                    target.Pass is { } x265Pass ? $"log-level=error:pass={Int(x265Pass)}:stats=clementine-2pass.log" : "log-level=error"];
            case VideoCodec.Hevc when hw is not null:
                return ["-c:v", hw.Name, .. HardwareEncoders.QualityArguments(hw), "-tag:v", "hvc1"];
            case VideoCodec.Hevc:
                return ["-c:v", "libx265", "-preset", target.Preset ?? "medium", "-crf", Int(target.Crf ?? 23), "-tag:v", "hvc1", "-x265-params", "log-level=error"];
            case VideoCodec.ProRes:
                var alpha = target.ProRes >= ProResProfile.P4444 && (target.AlphaMode == AlphaMode.Required || keepAlpha);
                return ["-c:v", "prores_ks", "-profile:v", Int((int)target.ProRes), "-vendor", "apl0", .. alpha ? new[] { "-alpha_bits", "16" } : []];
            case VideoCodec.Hap:
                var format = target.Hap switch { HapFormat.HapAlpha => "hap_alpha", HapFormat.HapQ => "hap_q", _ => "hap" };
                return ["-c:v", "hap", "-format", format, "-compressor", "snappy"];
            case VideoCodec.Dxv:
                return ["-c:v", "dxv", "-format", "dxt1"];
            case VideoCodec.Vp9:
                return ["-c:v", "libvpx-vp9", "-crf", Int(target.Crf ?? 32), "-b:v", "0", "-row-mt", "1", "-deadline", "good", "-cpu-used", "2",
                    .. target.AlphaMode == AlphaMode.Required || keepAlpha ? new[] { "-auto-alt-ref", "0" } : []];
            case VideoCodec.Av1:
                return ["-c:v", "libsvtav1", "-crf", Int(target.Crf ?? 35), "-preset", target.Preset ?? "8"];
            case VideoCodec.Mpeg4:
                return ["-c:v", "mpeg4", "-q:v", "3", "-tag:v", "XVID"];
            case VideoCodec.Wmv2:
                return ["-c:v", "wmv2", "-q:v", "4"];
            case VideoCodec.Gif:
                return ["-c:v", "gif"];
            default:
                return ["-c:v", "libwebp_anim", "-quality", "75", "-lossless", "0"];
        }
    }

    internal static string SoftwareEncoderName(VideoTarget target) => target.Codec switch
    {
        VideoCodec.H264 => "libx264",
        VideoCodec.Hevc => "libx265",
        VideoCodec.ProRes => "prores_ks",
        VideoCodec.Hap => "hap",
        VideoCodec.Dxv => "dxv",
        VideoCodec.Vp9 => "libvpx-vp9",
        VideoCodec.Av1 => "libsvtav1",
        VideoCodec.Mpeg4 => "mpeg4",
        VideoCodec.Wmv2 => "wmv2",
        VideoCodec.Gif => "gif",
        _ => "libwebp_anim",
    };

    static IEnumerable<string> ColorTags(VideoStreamInfo video)
    {
        var matrix = ColorSpaces.OutputMatrix(video);
        var (primaries, transfer) = ColorSpaces.DefaultsFor(matrix);
        return
        [
            "-colorspace", ColorSpaces.TagOf(matrix),
            "-color_primaries", video.ColorPrimaries ?? primaries,
            "-color_trc", video.ColorTransfer ?? transfer,
            "-color_range", "tv",
        ];
    }

    static IEnumerable<string> AudioCodecArguments(VideoTarget target, AudioStreamInfo audio, VideoEdit edit, ClementineSettings settings, MediaInfo source)
    {
        if (target.CopyCompatibleAudio && !edit.ChangesAudio && CanCopyAudio(audio.Codec, target.Extension)
            && (!target.WebSafeAudio || audio.Codec is "aac" or "mp3"))
        {
            return ["-c:a", "copy"];
        }

        var downmix = audio.Channels > 2 ? new[] { "-ac", "2" } : [];
        var rate = Int(settings.VjAudioSampleRate is >= 8000 and <= 192000 ? settings.VjAudioSampleRate : 48000);
        return target.Audio switch
        {
            VideoAudioCodec.Pcm16 => ["-c:a", "pcm_s16le", "-ar", rate, .. downmix],
            VideoAudioCodec.Pcm24 => ["-c:a", "pcm_s24le", "-ar", rate],
            VideoAudioCodec.Opus => ["-c:a", "libopus", "-b:a", $"{target.AudioBitrateKbps}k", .. downmix],
            VideoAudioCodec.Mp3 => ["-c:a", "libmp3lame", "-b:a", $"{target.AudioBitrateKbps}k", .. downmix],
            VideoAudioCodec.Wma => ["-c:a", "wmav2", "-b:a", $"{target.AudioBitrateKbps}k", .. downmix],
            _ => ["-c:a", "aac", "-b:a", $"{target.AudioBitrateKbps}k"],
        };
    }

    /// <summary>Audio codecs a container takes as-is (stream copy).</summary>
    public static bool CanCopyAudio(string codec, string extension) => extension switch
    {
        ".mp4" or ".m4v" => codec is "aac" or "mp3" or "alac" or "ac3" or "eac3",
        ".mov" => codec is "aac" or "mp3" or "alac" or "ac3" or "eac3" || codec.StartsWith("pcm_", StringComparison.Ordinal),
        ".mkv" => codec is "aac" or "mp3" or "opus" or "vorbis" or "flac" or "ac3" or "eac3" or "dts" or "truehd" or "alac"
            || codec.StartsWith("pcm_", StringComparison.Ordinal),
        ".webm" => codec is "opus" or "vorbis",
        _ => false,
    };

    static TimeSpan? ExpectedDuration(MediaInfo source, VideoEdit edit)
    {
        if (edit.OutputDuration is { } exact)
            return exact;
        var total = source.Duration ?? source.Video?.Duration;
        if (total is null)
            return null;
        var start = edit.Start ?? TimeSpan.Zero;
        var length = total.Value - start;
        if (edit.Duration is { } limit && limit < length)
            length = limit;
        if (length <= TimeSpan.Zero)
            return null;
        return edit.Speed > 0 ? TimeSpan.FromTicks((long)(length.Ticks / edit.Speed)) : length;
    }

    static string Seconds(TimeSpan time) => time.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture);

    static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);
}
