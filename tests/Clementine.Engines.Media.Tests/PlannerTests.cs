using Clementine.Core;
using Clementine.Engines.Media.Audio;
using Clementine.Engines.Media.Encoders;
using Clementine.Engines.Media.FFmpeg;
using Clementine.Engines.Media.Video;
using Clementine.TestSupport;

namespace Clementine.Engines.Media.Tests;

/// <summary>Pure logic: probe parsing, progress, error mapping and encoder planning (no FFmpeg runs).</summary>
public class PlannerTests
{
    static VideoStreamInfo Video(string pixelFormat = "yuv420p", int width = 1920, int height = 1080, string? colorSpace = null) => new()
    {
        Index = 0,
        Codec = "h264",
        Width = width,
        Height = height,
        PixelFormat = pixelFormat,
        HasAlpha = PixelFormats.HasAlpha(pixelFormat),
        FrameRate = new Rational(30, 1),
        RealFrameRate = new Rational(30, 1),
        TimeBase = new Rational(1, 15360),
        ColorSpace = colorSpace,
    };

    static MediaInfo Media(VideoStreamInfo video, bool audio = true) => new()
    {
        Path = @"C:\clips\in.mp4",
        Duration = TimeSpan.FromSeconds(10),
        VideoStreams = [video],
        AudioStreams = audio ? [new AudioStreamInfo { Index = 1, Codec = "aac", SampleRate = 44100, Channels = 6, ChannelLayout = "5.1" }] : [],
    };

    static string Args(VideoEncodePlan plan) => string.Join(' ', plan.Arguments);

    [Fact]
    public void Probe_json_is_parsed_including_rotation_alpha_and_cover_art()
    {
        const string json = """
            {"streams":[
              {"index":0,"codec_name":"vp9","codec_type":"video","width":640,"height":360,"pix_fmt":"yuv420p","avg_frame_rate":"30000/1001",
               "r_frame_rate":"30000/1001","time_base":"1/1000","color_space":"bt709","field_order":"tt","tags":{"alpha_mode":"1","language":"eng"},
               "side_data_list":[{"side_data_type":"Display Matrix","rotation":-90}],"disposition":{"default":1,"attached_pic":0}},
              {"index":1,"codec_name":"mjpeg","codec_type":"video","width":64,"height":64,"disposition":{"attached_pic":1}},
              {"index":2,"codec_name":"opus","codec_type":"audio","sample_rate":"48000","channels":2,"channel_layout":"stereo"},
              {"index":3,"codec_name":"hdmv_pgs_subtitle","codec_type":"subtitle"}],
             "format":{"format_name":"matroska,webm","duration":"12.5","size":"1000","bit_rate":"640","tags":{"title":"Hello"}}}
            """;

        var info = FfprobeService.Parse("x.webm", json);

        var video = Assert.Single(info.VideoStreams);
        Assert.True(video.HasAlpha);
        Assert.Equal(270, video.Rotation);
        Assert.Equal((360, 640), (video.DisplayWidth, video.DisplayHeight));
        Assert.True(video.IsInterlaced);
        Assert.True(video.HasIrregularTimeBase);
        Assert.False(video.IsVariableFrameRate);
        Assert.Equal("eng", video.Language);
        Assert.Single(info.AttachedPictures);
        Assert.Equal(48000, info.Audio!.SampleRate);
        Assert.False(Assert.Single(info.SubtitleStreams).IsText);
        Assert.Equal(TimeSpan.FromSeconds(12.5), info.Duration);
        Assert.True(info.IsFormat("webm"));
        Assert.Equal("Hello", info.Tags["title"]);
    }

    [Fact]
    public void Progress_parser_tracks_out_time_and_end()
    {
        var parser = new FfmpegProgressParser(TimeSpan.FromSeconds(10));
        Assert.True(parser.Feed("out_time_us=2500000"));
        Assert.Equal(0.25, parser.Fraction!.Value, 3);
        Assert.False(parser.Feed("out_time_us=1000000"));
        Assert.False(parser.Feed("frame=12"));
        Assert.True(parser.Feed("progress=end"));
        Assert.Equal(1, parser.Fraction);
        Assert.Null(new FfmpegProgressParser(null).Fraction);
    }

    [Theory]
    [InlineData("moov atom not found", "incomplete")]
    [InlineData("[enc] Error while opening encoder", "encoder couldn't start")]
    [InlineData("C:\\x.mp4: No such file or directory", "couldn't find")]
    [InlineData("something odd", "couldn't process")]
    public void Errors_are_friendly(string stderr, string expected) =>
        Assert.Contains(expected, FfmpegErrors.Describe(stderr), StringComparison.OrdinalIgnoreCase);

    [Theory]
    [InlineData(1001, 4, 1000)]
    [InlineData(563, 4, 564)]
    [InlineData(562, 4, 560)]
    [InlineData(1001, 2, 1000)]
    [InlineData(1, 4, 4)]
    public void Sizes_round_to_the_codec_multiple(int value, int multiple, int expected) =>
        Assert.Equal(expected, VideoEncodePlanner.RoundTo(value, multiple));

    [Theory]
    [InlineData(29.91, 30000, 1001)]
    [InlineData(29.99, 30, 1)]
    [InlineData(24.0, 24, 1)]
    [InlineData(59.8, 60000, 1001)]
    [InlineData(12.5, 25, 2)]
    public void Variable_rates_snap_to_broadcast_rates(double fps, long numerator, long denominator) =>
        Assert.Equal(new Rational(numerator, denominator), VideoEncodePlanner.SnapFrameRate(fps).Reduce());

    [Fact]
    public void Hap_plan_converts_untagged_sd_with_bt601_to_rgba_and_pcm()
    {
        var plan = VideoEncodePlanner.Build(Media(Video(width: 640, height: 360)), VideoTarget.HapOf(HapFormat.Hap, audio: true), VideoEdit.None, "out.mov", new ClementineSettings());

        var args = Args(plan);
        Assert.Contains("in_color_matrix=bt601", args);
        Assert.Contains("out_range=full", args);
        Assert.Contains("format=rgba", args);
        Assert.Contains("-c:v hap -format hap -compressor snappy", args);
        Assert.Contains("-c:a pcm_s16le -ar 48000 -ac 2", args);
        Assert.DoesNotContain("-colorspace", args);
    }

    [Fact]
    public void ProRes_plan_tags_bt709_and_keeps_channels()
    {
        var plan = VideoEncodePlanner.Build(Media(Video(width: 640, height: 360)), VideoTarget.ProResOf(ProResProfile.Hq, audio: true), VideoEdit.None, "out.mov", new ClementineSettings());

        var args = Args(plan);
        Assert.Contains("in_color_matrix=bt601", args);
        Assert.Contains("out_color_matrix=bt709", args);
        Assert.Contains("-colorspace bt709", args);
        Assert.Contains("-profile:v 3 -vendor apl0", args);
        Assert.Contains("-c:a pcm_s24le -ar 48000", args);
        Assert.DoesNotContain("-ac 2", args);
    }

    [Fact]
    public void Alpha_source_into_opaque_codec_is_premultiplied_over_black()
    {
        var plan = VideoEncodePlanner.Build(Media(Video("yuva444p10le")), VideoTarget.DxvOf(audio: false), VideoEdit.None, "out.mov", new ClementineSettings());

        Assert.Contains("premultiply=inplace=1", Args(plan));
        Assert.Contains("-an", plan.Arguments);
    }

    [Fact]
    public void Vfr_sources_become_constant_rate_for_vj_codecs_only()
    {
        var vfr = Video() with { FrameRate = new Rational(2991, 100), RealFrameRate = new Rational(120, 1) };
        var settings = new ClementineSettings();

        var hap = Args(VideoEncodePlanner.Build(Media(vfr), VideoTarget.HapOf(HapFormat.Hap, false), VideoEdit.None, "o.mov", settings));
        var h264 = Args(VideoEncodePlanner.Build(Media(vfr), VideoTarget.H264(), VideoEdit.None, "o.mp4", settings));
        var off = Args(VideoEncodePlanner.Build(Media(vfr), VideoTarget.HapOf(HapFormat.Hap, false), VideoEdit.None, "o.mov", new ClementineSettings { ForceConstantFrameRate = false }));

        Assert.Contains("-fps_mode cfr -r 30000/1001", hap);
        Assert.DoesNotContain("-fps_mode", h264);
        Assert.DoesNotContain("-fps_mode", off);
    }

    [Fact]
    public void Hardware_encoder_is_used_only_when_allowed()
    {
        var nvenc = new HardwareEncoder("h264_nvenc", HardwareVendor.Nvidia, IsHevc: false);
        var on = VideoEncodePlanner.Build(Media(Video()), VideoTarget.H264(), VideoEdit.None, "o.mp4", new ClementineSettings(), nvenc);
        var off = VideoEncodePlanner.Build(Media(Video()), VideoTarget.H264(), VideoEdit.None, "o.mp4", new ClementineSettings { UseHardwareEncoding = false }, nvenc);
        var compress = VideoEncodePlanner.Build(Media(Video()), VideoTarget.H264() with { AllowHardware = false }, VideoEdit.None, "o.mp4", new ClementineSettings(), nvenc);

        Assert.Equal("h264_nvenc", on.VideoEncoder);
        Assert.Equal("libx264", off.VideoEncoder);
        Assert.Equal("libx264", compress.VideoEncoder);
    }

    [Fact]
    public void Aac_audio_is_copied_into_mp4_when_untouched()
    {
        var media = Media(Video()) with { AudioStreams = [new AudioStreamInfo { Index = 1, Codec = "aac", SampleRate = 48000, Channels = 2 }] };

        Assert.Contains("-c:a copy", Args(VideoEncodePlanner.Build(media, VideoTarget.H264(), VideoEdit.None, "o.mp4", new ClementineSettings())));
        Assert.Contains("-c:a aac", Args(VideoEncodePlanner.Build(media, VideoTarget.H264(), new VideoEdit { AudioFilters = ["areverse"] }, "o.mp4", new ClementineSettings())));
    }

    [Fact]
    public void Matching_targets_keep_the_source_codec()
    {
        Assert.Equal(ProResProfile.P4444Xq, MatchingTargets.For(Media(Video() with { Codec = "prores", CodecTag = "ap4x" }), ".mov").ProRes);
        Assert.Equal(HapFormat.HapQ, MatchingTargets.For(Media(Video() with { Codec = "hap", CodecTag = "HapY" }), ".mov").Hap);
        Assert.Equal(VideoCodec.Dxv, MatchingTargets.For(Media(Video() with { Codec = "dxv" }), ".mov").Codec);
        Assert.Equal(".mov", MatchingTargets.For(Media(Video()), ".mov").Extension);
        Assert.Equal((VideoCodec.H264, ".mp4"), (MatchingTargets.For(Media(Video() with { Codec = "mpeg2video" }), ".mpg") is var t ? (t.Codec, t.Extension) : default));
    }

    [Fact]
    public void Geometry_helpers()
    {
        Assert.Equal(new FrameSize(1920, 1080), VideoGeometry.ShortSide(new FrameSize(1280, 720), 1080));
        Assert.Equal(new FrameSize(1080, 1920), VideoGeometry.ShortSide(new FrameSize(720, 1280), 1080));
        Assert.Equal(new FrameSize(1280, 720), VideoGeometry.CapShortSide(new FrameSize(1280, 720), 1080));
        Assert.Equal(new FrameSize(640, 360), VideoGeometry.Fit(new FrameSize(1920, 1080), 640, 640));
        var (crop, x, y) = VideoGeometry.CropToAspect(new FrameSize(1920, 1080), 9 / 16.0, CropAnchor.Right);
        Assert.Equal((new FrameSize(608, 1080), 1312, 0), (crop, x, y));
        Assert.Equal(new[] { "atempo=2", "atempo=2", "atempo=1.25" }, VideoGeometry.AtempoChain(5));
        Assert.Equal(new[] { "atempo=0.5", "atempo=0.5" }, VideoGeometry.AtempoChain(0.25));
    }

    [Fact]
    public void Split_cut_points()
    {
        var parts = SplitVideoOperation.CutPoints(new OptionValues { ["mode"] = "parts", ["parts"] = 4.0 }, TimeSpan.FromSeconds(8));
        var pieces = SplitVideoOperation.CutPoints(new OptionValues { ["mode"] = "seconds", ["seconds"] = 3.0 }, TimeSpan.FromSeconds(8));

        Assert.Equal(new double[] { 2, 4, 6 }, parts.Select(p => p.TotalSeconds));
        Assert.Equal(new double[] { 3, 6 }, pieces.Select(p => p.TotalSeconds));
    }

    [Fact]
    public void Audio_helpers()
    {
        var ranges = AudioFilters.ParseRanges("0:12.5-0:13.2, 1:05 – 1:06");
        Assert.Equal(2, ranges.Count);
        Assert.Equal(TimeSpan.FromSeconds(65), ranges[1].Start);
        Assert.Equal("between(t,12.5,13.2)+between(t,65,66)", AudioFilters.Between(ranges));
        Assert.Throws<OperationException>(() => AudioFilters.ParseRanges("2-1"));

        var cuts = AudioFilters.SilenceCuts("silence_start: 0\nsilence_end: 0.5\nsilence_start: 3\nsilence_end: 4 | silence_duration: 1\nsilence_start: 9.5", TimeSpan.FromSeconds(10));
        Assert.Equal(new[] { 3.5 }, cuts.Select(c => c.TotalSeconds));

        Assert.Equal(-3.2, AudioFilters.ParseMaxVolume("[Parsed_volumedetect_0] max_volume: -3.2 dB"));
        var loud = AudioFilters.ParseLoudnorm("""noise { "input_i" : "-27.61", "input_tp" : "-4.47", "input_lra" : "18.06", "input_thresh" : "-39.20", "target_offset" : "0.58" }""");
        Assert.Equal(("-27.61", "0.58"), (loud.IntegratedLufs, loud.Offset));
        Assert.Equal(64, CompressAudioOperation.BitrateFor(0.17 * 1024 * 1024, TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public void Natural_sort_orders_numbers_by_value()
    {
        var names = new[] { "clip 10.mp4", "clip 2.mp4", "Clip 1.mp4", "clip 02b.mp4" }.Order(NaturalComparer.Instance).ToList();
        Assert.Equal(new[] { "Clip 1.mp4", "clip 2.mp4", "clip 02b.mp4", "clip 10.mp4" }, names);
        Assert.Equal("shot", ImageSequenceOperation.SequenceName("shot_0001"));
        Assert.Equal("sequence", ImageSequenceOperation.SequenceName("0001"));
    }

    [Fact]
    public async Task Catalog_ids_use_only_media_prefixes_and_groups_are_sane()
    {
        using var ws = new TestWorkspace("media-catalog");
        var operations = new MediaOperationProvider().CreateOperations(ws.Context).ToList();

        Assert.All(operations, op => Assert.Matches("^(video|audio|sequence)\\.", op.Info.Id));
        Assert.True(operations.Count > 90, $"only {operations.Count} operations");
        Assert.All(operations.Where(o => o.Info.Weight == OperationWeight.Heavy), op => Assert.NotEqual(OperationCategory.Action, op.Info.Category));

        var mp4 = InputFile.FromPath(await MediaFixtures.VideoAsync(ws.Root, "x.mp4", 64, 64, seconds: 0.1));
        var convert = OperationCatalog.Group(operations.Where(o => o.Info.Category == OperationCategory.Convert && o.AcceptsAll([mp4])));
        Assert.Equal(new[] { "MP4", "MOV", "MKV", "WebM", "GIF", "WebP", "Audio", "DXV", "HAP", "ProRes", "AVI", "WMV" }, convert.Select(g => g.Name));
        Assert.Equal("video.prores.422hq.audio", convert.Single(g => g.Name == "ProRes").Default.Info.Id);
    }
}
