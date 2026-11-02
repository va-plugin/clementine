using Clementine.Core;
using Clementine.Engines.Media.Tests.Support;
using Clementine.TestSupport;

namespace Clementine.Engines.Media.Tests;

/// <summary>Each video tool on a tiny clip.</summary>
public class VideoToolTests
{
    static Dictionary<string, object?> Set(params (string Key, object? Value)[] values) => values.ToDictionary(v => v.Key, v => v.Value);

    [Theory]
    [InlineData("video.compress.good", "big clip (compressed).mp4")]
    [InlineData("video.compress.smaller", "big clip (smaller).mp4")]
    public async Task Compress_presets_make_h264_mp4(string id, string name)
    {
        using var ws = new MediaWorkspace("tool-compress");
        var input = await ws.UseAsync(Fixture.OddClip, "big clip.mp4");

        var result = await ws.RunAsync(id, input);

        var output = Assert.Single(result.Outputs);
        Assert.Equal(name, Path.GetFileName(output));
        var probe = await MediaFixtures.ProbeAsync(output);
        Assert.Equal("h264", probe.Video!.CodecName);
        Assert.True(new FileInfo(output).Length < new FileInfo(input).Length);
    }

    [Fact]
    public async Task Compress_to_a_target_size_lands_near_it()
    {
        using var ws = new MediaWorkspace("tool-compress-target");
        // A high-bitrate source so the target is meaningful.
        var input = await SharedFixtures.FfmpegTo(ws.Path("source.mp4"),
            "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30:duration=3", "-f", "lavfi", "-i", "sine=frequency=300:duration=3",
            "-c:v", "libx264", "-preset", "ultrafast", "-crf", "4", "-c:a", "aac", "-shortest");
        var targetBytes = 0.4 * 1024 * 1024;

        var result = await ws.RunAsync("video.compress.target", input, Set(("mode", "size"), ("sizeMb", 0.4)));

        var output = Assert.Single(result.Outputs);
        Assert.Equal("source (0.4 MB).mp4", Path.GetFileName(output));
        Assert.InRange(new FileInfo(output).Length, targetBytes * 0.6, targetBytes * 1.12);
    }

    [Fact]
    public void Compress_and_resize_labels_never_collide()
    {
        // Good / Smaller / Target size (MB or %) each get their own name — and "50% size" isn't Resize 50%'s "(50%)".
        Assert.Equal("25 MB", Video.CompressVideoOperation.TargetLabel(bySize: true, 25, 50));
        Assert.Equal("2.5 MB", Video.CompressVideoOperation.TargetLabel(bySize: true, 2.5, 50));
        Assert.Equal("50% size", Video.CompressVideoOperation.TargetLabel(bySize: false, 25, 50));
    }

    [Fact]
    public void Compress_target_too_small_is_explained()
    {
        Assert.Throws<OperationException>(() => Video.CompressVideoOperation.BitratePlan(10_000, TimeSpan.FromMinutes(10), hasAudio: true));
    }

    [Fact]
    public async Task Trim_copies_by_default_and_reencodes_when_precise()
    {
        using var ws = new MediaWorkspace("tool-trim");
        var input = await ws.UseAsync(Fixture.ShortGopClip, "Clip – ünïcødé 01.mp4");

        var copy = await ws.RunAsync("video.trim", input, Set(("start", "0:00.5"), ("end", "0:01.5")));
        var precise = await ws.RunAsync("video.trim", input, Set(("start", TimeSpan.FromSeconds(0.4)), ("end", TimeSpan.FromSeconds(1.2)), ("precise", true)));

        var copied = await MediaFixtures.ProbeAsync(Assert.Single(copy.Outputs));
        Assert.EndsWith("(trimmed).mp4", Assert.Single(copy.Outputs));
        Assert.InRange(copied.Duration, 0.9, 1.15);
        var exact = await MediaFixtures.ProbeAsync(Assert.Single(precise.Outputs));
        Assert.InRange(exact.Duration, 0.75, 0.86);
        Assert.Contains(copy.Warnings, w => w.Contains("keyframe", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Trim_dynamic_default_end_is_the_duration()
    {
        using var ws = new MediaWorkspace("tool-trim-defaults");
        var input = await ws.UseAsync(Fixture.ShortGopClip);

        var defaults = await ws.Op("video.trim").GetDynamicDefaultsAsync([InputFile.FromPath(input)], default);

        Assert.InRange(defaults.GetTime("end", TimeSpan.Zero).TotalSeconds, 1.9, 2.1);
    }

    [Fact]
    public async Task Trim_end_zero_or_before_the_start_means_to_the_end()
    {
        using var ws = new MediaWorkspace("tool-trim-open");
        var input = await ws.UseAsync(Fixture.ShortGopClip);

        // End 00:00.000 is what the dialog shows when the length couldn't be read.
        var open = await ws.RunAsync("video.trim", input, Set(("start", "0:01"), ("end", TimeSpan.Zero)));
        var backwards = await ws.RunAsync("video.trim", input, Set(("start", "0:01"), ("end", "0:00.5"), ("precise", true)));

        Assert.InRange((await MediaFixtures.ProbeAsync(Assert.Single(open.Outputs))).Duration, 0.9, 1.1);
        Assert.DoesNotContain(open.Warnings, w => w.Contains("wasn't after the start", StringComparison.Ordinal));
        Assert.InRange((await MediaFixtures.ProbeAsync(Assert.Single(backwards.Outputs))).Duration, 0.9, 1.1);
        Assert.Contains(backwards.Warnings, w => w.Contains("wasn't after the start", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0.0, 0.0, 0.0, 0.0, null)] // unknown length, end 0 → whole file
    [InlineData(1.5, 0.0, 0.0, 1.5, null)] // unknown length, end 0 → from the start to the end
    [InlineData(1.5, 1.0, 0.0, 1.5, null)] // unknown length, end before the start → to the end
    [InlineData(1.0, 3.0, 0.0, 1.0, 2.0)]  // unknown length, a real range is kept as asked
    [InlineData(1.0, 0.0, 10.0, 1.0, 9.0)] // known length, end 0 → to the end
    [InlineData(4.0, 2.0, 10.0, 4.0, 6.0)] // known length, end before the start → to the end
    [InlineData(1.0, 30.0, 10.0, 1.0, 9.0)] // end past the end is clamped
    public void Trim_range_never_comes_out_empty(double start, double end, double duration, double expectedStart, double? expectedLength)
    {
        var options = new OptionValues(Set(("start", TimeSpan.FromSeconds(start)), ("end", TimeSpan.FromSeconds(end))));

        var (s, length, warning) = Video.TrimVideoOperation.Range(options, TimeSpan.FromSeconds(duration));

        Assert.Equal(expectedStart, s.TotalSeconds, 3);
        Assert.Equal(expectedLength, length?.TotalSeconds);
        Assert.Equal(end > 0 && end <= start, warning is not null);
    }

    [Fact]
    public void Trim_start_past_a_known_end_is_still_an_error()
    {
        var options = new OptionValues(Set(("start", TimeSpan.FromSeconds(12)), ("end", TimeSpan.Zero)));
        var error = Assert.Throws<OperationException>(() => Video.TrimVideoOperation.Range(options, TimeSpan.FromSeconds(10)));
        Assert.Contains("past the end", error.Message);
    }

    [Theory]
    [InlineData("video.resize.720p", 1280, 720)]
    [InlineData("video.resize.1080p", 1920, 1080)]
    [InlineData("video.resize.50", 160, 90)]
    public async Task Resize_presets(string id, int width, int height)
    {
        using var ws = new MediaWorkspace("tool-resize");
        var input = await ws.UseAsync(Fixture.Clip);

        var result = await ws.RunAsync(id, input);

        var probe = await MediaFixtures.ProbeAsync(Assert.Single(result.Outputs));
        Assert.Equal((width, height), (probe.Video!.Width, probe.Video.Height));
    }

    [Fact]
    public async Task Resize_custom_keeps_aspect_and_even_sizes()
    {
        using var ws = new MediaWorkspace("tool-resize-custom");
        var input = await ws.UseAsync(Fixture.Clip);

        var result = await ws.RunAsync("video.resize.custom", input, Set(("width", 301), ("height", 0)));

        var probe = await MediaFixtures.ProbeAsync(Assert.Single(result.Outputs));
        Assert.Equal((302, 170), (probe.Video!.Width, probe.Video.Height));
    }

    [Fact]
    public async Task ProRes_source_stays_prores_when_resized()
    {
        using var ws = new MediaWorkspace("tool-resize-prores");
        var input = await ws.UseAsync(Fixture.AlphaClip);

        var result = await ws.RunAsync("video.resize.50", input);

        var output = Assert.Single(result.Outputs);
        var probe = await MediaFixtures.ProbeAsync(output);
        Assert.Equal(("prores", "ap4h", 160, 90), (probe.Video!.CodecName, probe.Video.CodecTag, probe.Video.Width, probe.Video.Height));
        Assert.True((await Frames.DecodeAsync(output, 0.5)).At(20, 45).A < 8, "alpha must survive a matching re-encode");
    }

    [Theory]
    [InlineData("9:16", "center", 102, 180)]
    [InlineData("1:1", "left", 180, 180)]
    [InlineData("custom", "center", 200, 100)]
    public async Task Crop(string aspect, string anchor, int width, int height)
    {
        using var ws = new MediaWorkspace("tool-crop");
        var input = await ws.UseAsync(Fixture.Clip);

        var result = await ws.RunAsync("video.crop", input, Set(("aspect", aspect), ("anchor", anchor), ("width", 200), ("height", 100)));

        var probe = await MediaFixtures.ProbeAsync(Assert.Single(result.Outputs));
        Assert.Equal((width, height), (probe.Video!.Width, probe.Video.Height));
    }

    [Theory]
    [InlineData("video.speed.double", 0.5)]
    [InlineData("video.speed.half", 2.0)]
    public async Task Speed_changes_duration_and_keeps_the_frame_rate(string id, double expectedSeconds)
    {
        using var ws = new MediaWorkspace("tool-speed");
        var input = await ws.UseAsync(Fixture.Clip);

        var result = await ws.RunAsync(id, input);

        var probe = await MediaFixtures.ProbeAsync(Assert.Single(result.Outputs));
        Assert.InRange(probe.Duration, expectedSeconds - 0.1, expectedSeconds + 0.12);
        Assert.Equal(30, probe.Video!.FrameRate, 1);
        Assert.True(probe.HasAudio);
    }

    [Fact]
    public async Task Custom_speed_can_drop_the_audio()
    {
        using var ws = new MediaWorkspace("tool-speed-custom");
        var input = await ws.UseAsync(Fixture.Clip);

        var result = await ws.RunAsync("video.speed.custom", input, Set(("factor", 4), ("dropAudio", true)));

        var probe = await MediaFixtures.ProbeAsync(Assert.Single(result.Outputs));
        Assert.False(probe.HasAudio);
        Assert.InRange(probe.Duration, 0.2, 0.32);
        Assert.EndsWith("(4x).mp4", Assert.Single(result.Outputs));
    }

    [Theory]
    [InlineData("video.fps.25", 25)]
    [InlineData("video.fps.60", 60)]
    public async Task Frame_rate(string id, double fps)
    {
        using var ws = new MediaWorkspace("tool-fps");
        var input = await ws.UseAsync(Fixture.Clip);

        var result = await ws.RunAsync(id, input);

        Assert.Equal(fps, (await MediaFixtures.ProbeAsync(Assert.Single(result.Outputs))).Video!.FrameRate, 1);
    }

    [Theory]
    [InlineData("video.rotate.right", 180, 320)]
    [InlineData("video.rotate.left", 180, 320)]
    [InlineData("video.rotate.180", 320, 180)]
    [InlineData("video.flip.horizontal", 320, 180)]
    [InlineData("video.flip.vertical", 320, 180)]
    public async Task Rotate_and_flip(string id, int width, int height)
    {
        using var ws = new MediaWorkspace("tool-rotate");
        var input = await ws.UseAsync(Fixture.AlphaClip);

        var result = await ws.RunAsync(id, input);

        var output = Assert.Single(result.Outputs);
        var probe = await MediaFixtures.ProbeAsync(output);
        Assert.Equal((width, height), (probe.Video!.Width, probe.Video.Height));
        // The transparent half of the alpha fixture moves with the transform.
        var frame = await Frames.DecodeAsync(output, 0.5);
        var transparentCorner = id switch
        {
            "video.rotate.right" => frame.At(width / 2, 10),           // left half → top
            "video.rotate.left" => frame.At(width / 2, height - 10),   // left half → bottom
            "video.rotate.180" or "video.flip.horizontal" => frame.At(width - 10, height / 2),
            _ => frame.At(10, height / 2),
        };
        Assert.True(transparentCorner.A < 8, $"{id}: expected transparency, alpha {transparentCorner.A}");
    }

    [Fact]
    public async Task Reverse_plays_backwards()
    {
        using var ws = new MediaWorkspace("tool-reverse");
        var input = await ws.UseAsync(Fixture.Clip);
        var lastFrame = await Frames.DecodeAsync(input, 0.96);

        var result = await ws.RunAsync("video.reverse", input);

        var output = Assert.Single(result.Outputs);
        Assert.EndsWith("(reversed).mp4", output);
        var firstFrame = await Frames.DecodeAsync(output, 0);
        Frames.AssertColor(firstFrame.At(100, 60), lastFrame.At(100, 60).R, lastFrame.At(100, 60).G, lastFrame.At(100, 60).B, 24, "first reversed frame ≈ last original frame");
        Assert.True((await MediaFixtures.ProbeAsync(output)).HasAudio);
    }

    [Fact]
    public async Task Ping_pong_loop_is_forward_then_backward_without_doubled_turnarounds()
    {
        using var ws = new MediaWorkspace("tool-pingpong");
        var input = await ws.UseAsync(Fixture.Clip);

        var result = await ws.RunAsync("video.pingpong", input);

        var output = Assert.Single(result.Outputs);
        var probe = await MediaFixtures.ProbeAsync(output);
        // 30 frames forward + 28 back = 58 frames at 30 fps.
        Assert.InRange(probe.Duration, 1.85, 2.0);
        Assert.True(await MediaFixtures.DecodesCleanlyAsync(output));
    }

    [Fact]
    public async Task Long_clips_reverse_in_segments()
    {
        using var ws = new MediaWorkspace("tool-reverse-long");
        var input = await SharedFixtures.FfmpegTo(ws.Path("long.mp4"),
            "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=30:duration=5", "-f", "lavfi", "-i", "sine=frequency=500:duration=5",
            "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", "-shortest");
        var lastFrame = await Frames.DecodeAsync(input, 4.95);
        var budget = Video.ReverseVideoOperation.FrameMemoryBudget;
        Video.ReverseVideoOperation.FrameMemoryBudget = 1; // forces the minimum 2 s segments
        OperationResult result;
        try
        {
            result = await ws.RunAsync("video.reverse", input);
        }
        finally
        {
            Video.ReverseVideoOperation.FrameMemoryBudget = budget;
        }

        var output = Assert.Single(result.Outputs);
        var probe = await MediaFixtures.ProbeAsync(output);
        Assert.InRange(probe.Duration, 4.85, 5.15);
        Assert.True(probe.HasAudio);
        var firstFrame = await Frames.DecodeAsync(output, 0);
        Frames.AssertColor(firstFrame.At(50, 30), lastFrame.At(50, 30).R, lastFrame.At(50, 30).G, lastFrame.At(50, 30).B, 24, "segmented reverse starts at the original end");
        Assert.True(await MediaFixtures.DecodesCleanlyAsync(output));
    }

    [Theory]
    [InlineData("parts", 2, false, 2)]
    [InlineData("seconds", 0.5, true, 4)]
    public async Task Split_into_a_folder(string mode, double value, bool precise, int expected)
    {
        using var ws = new MediaWorkspace("tool-split");
        var input = await ws.UseAsync(Fixture.ShortGopClip, "Clip – ünïcødé 01.mp4");

        var result = await ws.RunAsync("video.split", input, Set(("mode", mode), ("parts", value), ("seconds", value), ("precise", precise)));

        var folder = Assert.Single(result.Outputs);
        Assert.True(Directory.Exists(folder));
        Assert.Equal("Clip – ünïcødé 01 (parts)", Path.GetFileName(folder));
        var files = Directory.GetFiles(folder).Order().ToList();
        Assert.Equal(expected, files.Count);
        Assert.Equal("Clip – ünïcødé 01 01.mp4", Path.GetFileName(files[0]));
        foreach (var file in files)
            Assert.True(await MediaFixtures.DecodesCleanlyAsync(file));
    }

    [Fact]
    public async Task Names_starting_with_a_dash_split_and_extract_frames()
    {
        // "-intro 01.mp4" as a bare pattern would be read by FFmpeg as an option.
        using var ws = new MediaWorkspace("tool-dash");
        var input = await ws.UseAsync(Fixture.ShortGopClip, "-intro.mp4");

        var split = await ws.RunAsync("video.split", input, Set(("mode", "parts"), ("parts", 2)));
        var precise = await ws.RunAsync("video.split", input, Set(("mode", "parts"), ("parts", 2), ("precise", true)));
        var frames = await ws.RunAsync("video.snapshot.frames", input);

        static string Names(OperationResult result) => string.Join("|", Directory.GetFiles(Assert.Single(result.Outputs)).Select(Path.GetFileName).Order());
        Assert.Equal("-intro 01.mp4|-intro 02.mp4", Names(split));
        Assert.Equal("-intro 01.mp4|-intro 02.mp4", Names(precise));
        var pngs = Directory.GetFiles(Assert.Single(frames.Outputs)).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(60, pngs.Count);
        Assert.Equal("-intro_00001.png", pngs[0]);
    }

    [Fact]
    public async Task Join_matching_clips_losslessly_in_natural_order()
    {
        using var ws = new MediaWorkspace("tool-join");
        var a = await ws.UseAsync(Fixture.Clip, "part 2.mp4");
        var b = await ws.UseAsync(Fixture.Clip, "part 10.mp4");

        var result = await ws.RunAsync("video.join", [b, a]);

        var output = Assert.Single(result.Outputs);
        Assert.Equal("part 2 (joined).mp4", Path.GetFileName(output));
        Assert.Empty(result.Warnings);
        Assert.InRange((await MediaFixtures.ProbeAsync(output)).Duration, 1.9, 2.15);
    }

    [Fact]
    public async Task Join_mixed_clips_normalises_to_the_first()
    {
        using var ws = new MediaWorkspace("tool-join-mixed");
        var a = await ws.UseAsync(Fixture.Clip, "a.mp4");
        var b = await ws.UseAsync(Fixture.SilentClip, "b.mp4");
        var c = await ws.UseAsync(Fixture.MkvClip, "c.mkv");

        var result = await ws.RunAsync("video.join", [a, b, c]);

        var probe = await MediaFixtures.ProbeAsync(Assert.Single(result.Outputs));
        Assert.Equal(("h264", 320, 180), (probe.Video!.CodecName, probe.Video.Width, probe.Video.Height));
        Assert.True(probe.HasAudio);
        Assert.InRange(probe.Duration, 2.85, 3.2);
        Assert.NotEmpty(result.Warnings);
    }

    [Theory]
    [InlineData("video.snapshot.first", "clip (first frame).png")]
    [InlineData("video.snapshot.middle", "clip (middle frame).png")]
    [InlineData("video.snapshot.sheet", "clip (contact sheet).png")]
    public async Task Snapshots(string id, string name)
    {
        using var ws = new MediaWorkspace("tool-snap");
        var input = await ws.UseAsync(Fixture.Clip);

        var result = await ws.RunAsync(id, input);

        var output = Assert.Single(result.Outputs);
        Assert.Equal(name, Path.GetFileName(output));
        var probe = await MediaFixtures.ProbeAsync(output);
        Assert.Equal("png", probe.Video!.CodecName);
        if (id.EndsWith("sheet", StringComparison.Ordinal))
            Assert.Equal((4 * 320 + 5 * 6, 4 * 180 + 5 * 6), (probe.Video.Width, probe.Video.Height));
        else
            Assert.Equal((320, 180), (probe.Video.Width, probe.Video.Height));
    }

    [Fact]
    public async Task Snapshot_at_a_time_keeps_alpha()
    {
        using var ws = new MediaWorkspace("tool-snap-at");
        var input = await ws.UseAsync(Fixture.AlphaClip);

        var result = await ws.RunAsync("video.snapshot.at", input, Set(("time", "0:00.5")));

        var output = Assert.Single(result.Outputs);
        Assert.Equal("alpha (frame 00.00.500).png", Path.GetFileName(output));
        var probe = await MediaFixtures.ProbeAsync(output);
        Assert.Equal("rgba", probe.Video!.PixelFormat);
        Assert.True((await Frames.DecodeAsync(output)).At(40, 90).A < 8);
    }

    [Fact]
    public async Task All_frames_become_a_numbered_png_sequence()
    {
        using var ws = new MediaWorkspace("tool-frames");
        var input = await ws.UseAsync(Fixture.Clip, "100% clip.mp4");

        var result = await ws.RunAsync("video.snapshot.frames", input);

        var folder = Assert.Single(result.Outputs);
        var files = Directory.GetFiles(folder).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(30, files.Count);
        Assert.Equal("100% clip_00001.png", files[0]);
    }

    [Fact]
    public async Task Remove_audio_copies_the_video_only()
    {
        using var ws = new MediaWorkspace("tool-mute");
        var input = await ws.UseAsync(Fixture.Clip);

        var result = await ws.RunAsync("video.remove-audio", input);

        var probe = await MediaFixtures.ProbeAsync(Assert.Single(result.Outputs));
        Assert.False(probe.HasAudio);
        Assert.Equal("h264", probe.Video!.CodecName);
    }

    [Fact]
    public async Task Subtitles_extract_to_srt_and_burn_in()
    {
        using var ws = new MediaWorkspace("tool-subs");
        var subtitled = await ws.UseAsync(Fixture.SubtitledClip);
        var video = await ws.UseAsync(Fixture.Clip, "Clip – ünïcødé 01.mp4");
        var srt = await ws.UseAsync(Fixture.Subtitles, "Clip – ünïcødé 01.srt");

        var extracted = await ws.RunAsync("video.subtitles.extract", subtitled);
        var burned = await ws.RunAsync("video.subtitles.burn", [video, srt]);

        var text = await File.ReadAllTextAsync(Assert.Single(extracted.Outputs));
        Assert.Contains("Hello ünïcødé", text);
        Assert.Contains("-->", text);
        var output = Assert.Single(burned.Outputs);
        Assert.Equal("Clip – ünïcødé 01 (subtitled).mp4", Path.GetFileName(output));
        Assert.True(await MediaFixtures.DecodesCleanlyAsync(output));
        Assert.True(ws.Op("video.subtitles.burn").AcceptsAll([InputFile.FromPath(video), InputFile.FromPath(srt)]));
        Assert.False(ws.Op("video.subtitles.burn").AcceptsAll([InputFile.FromPath(video)]));
    }

    [Fact]
    public async Task Remove_metadata_strips_tags()
    {
        using var ws = new MediaWorkspace("tool-meta");
        var clip = await SharedFixtures.GetAsync(Fixture.Clip);
        var input = await SharedFixtures.FfmpegTo(ws.Path("tagged.mp4"), "-i", clip, "-c", "copy",
            "-metadata", "title=Secret", "-metadata", "location=+52.37+004.89/");

        var result = await ws.RunAsync("video.metadata.remove", input);

        var info = await new FFmpeg.FfprobeService(ws.Inner.Context.Tools).ProbeAsync(Assert.Single(result.Outputs), default);
        Assert.False(info.Tags.ContainsKey("title"));
        Assert.False(info.Tags.ContainsKey("location"));
        Assert.True(info.HasAudio && info.HasVideo);
    }

    [Fact]
    public async Task Info_reports_codec_size_alpha_and_audio()
    {
        using var ws = new MediaWorkspace("tool-info");
        var input = await ws.UseAsync(Fixture.AlphaClip);

        var result = await ws.RunAsync("video.info", input);

        Assert.NotNull(result.Text);
        Assert.Contains("320 × 180", result.Text);
        Assert.Contains("Alpha:         yes", result.Text);
        Assert.Contains("prores", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("h264", "h264", false, "shot.mp4")]
    [InlineData("prores4444", "prores", true, "shot (ProRes 4444).mov")]
    [InlineData("hapalpha", "hap", true, "shot (HAP Alpha).mov")]
    [InlineData("dxv", "dxv", false, "shot (DXV).mov")]
    public async Task Image_sequence_to_video(string codec, string expected, bool alpha, string name)
    {
        using var ws = new MediaWorkspace("tool-seq");
        var frames = new List<string>();
        for (var i = 1; i <= 12; i++)
        {
            frames.Add(await SharedFixtures.FfmpegTo(ws.Path($"shot_{i}.png"),
                "-f", "lavfi", "-i", $"color=c=black:s=64x48,format=rgba,geq=r='255':g='255':b='255':a='if(between(X,{i * 4},{i * 4 + 7}),255,0)'",
                "-frames:v", "1"));
        }

        var result = await ws.RunAsync("sequence.video", frames, Set(("fps", 24), ("codec", codec)));

        var output = Assert.Single(result.Outputs);
        Assert.Equal(name, Path.GetFileName(output));
        var probe = await MediaFixtures.ProbeAsync(output);
        Assert.Equal(expected, probe.Video!.CodecName);
        Assert.Equal(24, probe.Video.FrameRate, 1);
        Assert.InRange(probe.Duration, 0.45, 0.55);
        var frame = await Frames.DecodeAsync(output, 0);
        Frames.AssertColor(frame.At(8, 24), 255, 255, 255, 12, "the opaque bar of the first image");
        Assert.True(frame.At(8, 24).A > 247);
        if (alpha)
            Assert.True(frame.At(40, 24).A < 8, "transparent pixels stay transparent");
        else
            Frames.AssertColor(frame.At(40, 24), 0, 0, 0, 12, "transparent pixels become black");
    }
}
