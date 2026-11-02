using Clementine.Core;
using Clementine.Engines.Media.Encoders;
using Clementine.Engines.Media.FFmpeg;
using Clementine.Engines.Media.Tests.Support;
using Clementine.Engines.Media.Video;
using Clementine.TestSupport;

namespace Clementine.Engines.Media.Tests;

/// <summary>Failure modes seen with real camera/phone/VJ files: output naming, one-frame clips, RAW, DV, progress.</summary>
public class RealWorldTests
{
    // ---- naming

    [Theory]
    [InlineData("video.dxv.audio", "DXV")]
    [InlineData("video.dxv.noaudio", "DXV")]
    [InlineData("video.hap.audio", "HAP")]
    [InlineData("video.hap-alpha.noaudio", "HAP Alpha")]
    [InlineData("video.hap-q.audio", "HAP Q")]
    [InlineData("video.prores.422hq.audio", "ProRes 422 HQ")]
    [InlineData("video.prores.4444.noaudio", "ProRes 4444")]
    [InlineData("video.prores.422.audio", "ProRes 422")]
    [InlineData("video.prores.422lt.audio", "ProRes 422 LT")]
    [InlineData("video.prores.422proxy.audio", "ProRes 422 Proxy")]
    [InlineData("video.prores.4444xq.audio", "ProRes 4444 XQ")]
    [InlineData("video.mov.h264", "H.264")]
    [InlineData("video.mkv.h264", "H.264")]
    [InlineData("video.mp4.h264", null)]
    [InlineData("video.mp4.h265", "H.265")]
    [InlineData("video.webm.vp9", null)]
    [InlineData("video.webm.vp9.alpha", "VP9 alpha")]
    [InlineData("video.webm.av1", "AV1")]
    [InlineData("video.gif.hq", null)]
    [InlineData("video.gif.small", "480 px")]
    [InlineData("video.avi.mpeg4", null)]
    public void Outputs_sharing_an_extension_carry_a_short_codec_label(string id, string? label)
    {
        using var ws = new MediaWorkspace("names-labels");
        var op = Assert.IsType<VideoConvertOperation>(ws.Op(id));

        // A camera clip: no target shares its extension, so only targets that share theirs with each other are labelled.
        Assert.Equal(label, op.SuffixFor(InputFile.FromPath(ws.Inner.WriteText("clip.mts", ""))));
        // Same extension as the input: always labelled ("clip (H.264).mp4"), never a second "clip.mp4".
        Assert.NotNull(op.SuffixFor(InputFile.FromPath(ws.Inner.WriteText("same" + op.Target.Extension, ""))));
    }

    [Fact]
    public void No_two_different_encodes_of_one_clip_share_a_name()
    {
        using var ws = new MediaWorkspace("names-unique");
        var input = InputFile.FromPath(ws.Inner.WriteText("clip.mp4", ""));
        var converts = ws.All.Values.OfType<VideoConvertOperation>().Where(op => op.Accepts(input)).ToList();

        // Same extension + same label must mean the same picture (only the audio variant may differ).
        foreach (var clash in converts.GroupBy(op => (op.Target.Extension, op.SuffixFor(input))))
        {
            var pictures = clash.Select(op => (op.Target.Codec, op.Target.ProRes, op.Target.Hap, op.Target.AlphaMode, op.Target.Crf, op.Target.MaxWidth))
                .Distinct().ToList();
            Assert.True(pictures.Count == 1,
                $"\"clip{(clash.Key.Item2 is { } l ? $" ({l})" : "")}{clash.Key.Extension}\" would be written by " + string.Join(", ", clash.Select(op => op.Info.Id)));
        }
    }

    [Fact]
    public async Task One_clip_to_several_mov_codecs_gives_distinct_labelled_names()
    {
        using var ws = new MediaWorkspace("names-mov");
        var input = await ws.UseAsync(Fixture.SilentClip, "it's [VJ] 100% 𝄞.MOV");

        var names = new List<string>();
        foreach (var id in new[] { "video.dxv.noaudio", "video.hap-alpha.noaudio", "video.prores.4444.noaudio", "video.hap.noaudio" })
            names.Add(Path.GetFileName(Assert.Single((await ws.RunAsync(id, input)).Outputs)));

        Assert.Equal(
            new[] { "it's [VJ] 100% 𝄞 (DXV).mov", "it's [VJ] 100% 𝄞 (HAP Alpha).mov", "it's [VJ] 100% 𝄞 (ProRes 4444).mov", "it's [VJ] 100% 𝄞 (HAP).mov" },
            names);
    }

    [Fact]
    public async Task Audio_variants_of_one_format_get_distinct_names()
    {
        using var ws = new MediaWorkspace("names-audio");
        var input = await ws.UseAsync(Fixture.ToneWav);

        var names = new List<string>();
        foreach (var id in new[] { "audio.mp3.320", "audio.mp3.128", "audio.aiff.24", "audio.m4a.alac" })
            names.Add(Path.GetFileName(Assert.Single((await ws.RunAsync(id, input)).Outputs)));

        Assert.Equal(new[] { "tone.mp3", "tone (128 kbps).mp3", "tone (24-bit).aiff", "tone (ALAC).m4a" }, names);
    }

    [Fact]
    public void Image_sequence_vj_outputs_are_labelled()
    {
        Assert.Equal("shot", ImageSequenceOperation.OutputName("shot_0001", VideoTarget.H264(".mp4", audio: false)));
        Assert.Equal("shot (HAP Alpha)", ImageSequenceOperation.OutputName("shot_0001", VideoTarget.HapOf(HapFormat.HapAlpha, audio: false)));
        Assert.Equal("render (ProRes 4444)", ImageSequenceOperation.OutputName("render.0001", VideoTarget.ProResOf(ProResProfile.P4444, audio: false)));
    }

    // ---- odd sources

    [Theory]
    [InlineData("video.gif.hq")]
    [InlineData("video.fps.25")]
    [InlineData("video.speed.double")]
    public async Task One_frame_clip_survives_frame_rate_changes(string id)
    {
        using var ws = new MediaWorkspace("one-frame");
        var input = await SharedFixtures.FfmpegTo(ws.Path("still.mp4"),
            "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=30", "-frames:v", "1", "-c:v", "libx264", "-pix_fmt", "yuv420p");

        var result = await ws.RunAsync(id, input);

        var output = Assert.Single(result.Outputs);
        Assert.True(await MediaFixtures.DecodesCleanlyAsync(output));
        Assert.NotNull((await MediaFixtures.ProbeAsync(output)).Video);
    }

    [Fact]
    public async Task One_frame_clip_target_size_compress_says_it_is_too_short()
    {
        using var ws = new MediaWorkspace("one-frame-target");
        var input = await SharedFixtures.FfmpegTo(ws.Path("still.mp4"),
            "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=30", "-frames:v", "1", "-c:v", "libx264", "-pix_fmt", "yuv420p");

        var error = await Assert.ThrowsAsync<OperationException>(() => ws.RunAsync("video.compress.target", input));

        Assert.Contains("too short", error.Message);
    }

    [Theory]
    [InlineData("video.hap.audio")]
    [InlineData("video.prores.422hq.audio")]
    [InlineData("video.snapshot.first")]
    public async Task ProRes_raw_is_refused_with_a_friendly_message(string id)
    {
        using var ws = new MediaWorkspace("prores-raw");
        var prores = await SharedFixtures.FfmpegTo(ws.Path("normal.mov"),
            "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=25:duration=0.2", "-c:v", "prores_ks", "-profile:v", "3");
        // Relabel the sample entry as ProRes RAW ('aprn'), which is how a camera RAW clip probes.
        var bytes = await File.ReadAllBytesAsync(prores);
        var raw = ws.Path("A001_C002.mov");
        await File.WriteAllBytesAsync(raw, Replace(bytes, "apch"u8.ToArray(), "aprn"u8.ToArray()));
        Assert.Equal("prores_raw", (await MediaFixtures.ProbeAsync(raw)).Video!.CodecName);

        var error = await Assert.ThrowsAsync<OperationException>(() => ws.RunAsync(id, raw));

        Assert.Contains("ProRes RAW", error.Message);
        Assert.DoesNotContain(ws.FilesInRoot(), f => f.Contains("A001_C002 (", StringComparison.Ordinal) || f.Contains(".partial", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Dv_in_avi_is_deinterlaced_although_the_container_does_not_say_so()
    {
        using var ws = new MediaWorkspace("dv-avi");
        var mov = await SharedFixtures.FfmpegTo(ws.Path("capture.mov"),
            "-f", "lavfi", "-i", "testsrc2=size=720x576:rate=25:duration=0.4", "-f", "lavfi", "-i", "sine=sample_rate=48000:duration=0.4",
            "-c:v", "dvvideo", "-pix_fmt", "yuv420p", "-aspect", "16:9", "-c:a", "pcm_s16le", "-ac", "2", "-shortest");
        // Rewrapped to AVI: no field order, and a 1/600 time base whose empty chunks make ffprobe report 600 fps.
        var input = await SharedFixtures.FfmpegTo(ws.Path("tape.avi"), "-i", mov, "-c", "copy");
        var tools = ws.Inner.Context.Tools;
        var source = (await new FfprobeService(tools).ProbeAsync(input, default)).Video!;
        Assert.True(source.IsInterlaced, $"DV field order reported as {source.FieldOrder ?? "null"}");
        Assert.True(source.AverageCountsEmptyFrames, $"fixture should report a bogus average rate, got {source.FrameRate}");
        Assert.Equal(25, source.Fps, 2);
        Assert.False(source.IsVariableFrameRate);

        var result = await ws.RunAsync("video.hap.audio", input);

        var output = await new FfprobeService(tools).ProbeAsync(Assert.Single(result.Outputs), default);
        Assert.False(output.Video!.IsInterlaced, $"output field order {output.Video.FieldOrder}");
        Assert.Equal((1024, 576), (output.Video.Width, output.Video.Height));
        Assert.Equal(25, output.Video.Fps, 1);
        Assert.InRange(output.Video.FrameCount ?? 0, 9, 11);
    }

    [Fact]
    public async Task Camera_ac3_audio_is_re_encoded_to_aac_for_mp4_but_kept_by_mkv()
    {
        using var ws = new MediaWorkspace("ac3-mp4");
        var input = await SharedFixtures.FfmpegTo(ws.Path("00001.MTS"),
            "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=25:duration=0.5", "-f", "lavfi", "-i", "sine=sample_rate=48000:duration=0.5",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "ac3", "-ac", "2", "-f", "mpegts");

        var mp4 = await ws.RunAsync("video.mp4.h264", input);
        var mkv = await ws.RunAsync("video.mkv.h264", input);

        Assert.Equal("aac", (await MediaFixtures.ProbeAsync(Assert.Single(mp4.Outputs))).Audio!.CodecName);
        Assert.Equal("ac3", (await MediaFixtures.ProbeAsync(Assert.Single(mkv.Outputs))).Audio!.CodecName);
    }

    [Fact]
    public async Task Side_surround_audio_converts_to_opus()
    {
        using var ws = new MediaWorkspace("opus-51side");
        // AC-3, DTS and most 6-channel WAVs decode as "5.1(side)", which libopus refuses unless remapped.
        var input = await SharedFixtures.FfmpegTo(ws.Path("mix 5.1.wav"),
            "-f", "lavfi", "-i", "sine=sample_rate=48000:duration=0.5", "-filter_complex", "pan=5.1(side)|c0=c0|c1=c0|c2=c0|c3=c0|c4=c0|c5=c0",
            "-c:a", "pcm_s16le");

        var result = await ws.RunAsync("audio.opus", input);

        var probe = await MediaFixtures.ProbeAsync(Assert.Single(result.Outputs));
        Assert.Equal("opus", probe.Audio!.CodecName);
        Assert.Equal(6, probe.Audio.Channels);
    }

    [Fact]
    public async Task Timecode_and_extra_audio_tracks_from_a_camera_mov()
    {
        using var ws = new MediaWorkspace("camera-mov");
        // Timecode (tmcd) track, a mono 44.1 kHz track and a 5.1 96 kHz track.
        var input = await SharedFixtures.FfmpegTo(ws.Path("C0001.MOV"),
            "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=25:duration=0.5",
            "-f", "lavfi", "-i", "sine=sample_rate=44100:duration=0.5", "-f", "lavfi", "-i", "sine=sample_rate=96000:duration=0.5",
            "-filter_complex", "[2:a]pan=5.1|c0=c0|c1=c0|c2=c0|c3=c0|c4=c0|c5=c0[s]",
            "-map", "0:v", "-map", "1:a", "-map", "[s]", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
            "-c:a", "pcm_s24le", "-timecode", "01:00:00:00");

        var hap = await ws.RunAsync("video.hap.audio", input);
        var prores = await ws.RunAsync("video.prores.422hq.noaudio", input);

        var probe = await MediaFixtures.ProbeAsync(Assert.Single(hap.Outputs));
        Assert.Equal(("hap", "pcm_s16le", 48000, 1), (probe.Video!.CodecName, probe.Audio!.CodecName, probe.Audio.SampleRate, probe.Audio.Channels));
        Assert.Contains(VideoEncodePlanner.ExtraAudioTracksWarning(2), hap.Warnings);
        Assert.DoesNotContain(VideoEncodePlanner.ExtraAudioTracksWarning(2), prores.Warnings);
        Assert.Equal("C0001 (HAP).mov", Path.GetFileName(hap.Outputs[0]));
        Assert.Equal("C0001 (ProRes 422 HQ).mov", Path.GetFileName(prores.Outputs[0]));
    }

    [Fact]
    public void Hdr_kept_in_prores_warns_that_sdr_apps_show_it_washed_out()
    {
        var hlg = new MediaInfo
        {
            Path = @"C:\clips\IMG_0001.MOV",
            Duration = TimeSpan.FromSeconds(2),
            VideoStreams =
            [
                new VideoStreamInfo
                {
                    Codec = "hevc", Width = 1920, Height = 1080, PixelFormat = "yuv420p10le", BitDepth = 10,
                    FrameRate = new Rational(30, 1), RealFrameRate = new Rational(30, 1), TimeBase = new Rational(1, 600),
                    ColorTransfer = "arib-std-b67", ColorPrimaries = "bt2020", ColorSpace = "bt2020nc",
                },
            ],
        };

        var prores = VideoEncodePlanner.Build(hlg, VideoTarget.ProResOf(ProResProfile.Hq, audio: false), VideoEdit.None, "o.mov", new ClementineSettings());
        var hap = VideoEncodePlanner.Build(hlg, VideoTarget.HapOf(HapFormat.Hap, audio: false), VideoEdit.None, "o.mov", new ClementineSettings());

        Assert.Contains(VideoEncodePlanner.HdrKeptWarning, prores.Warnings);
        Assert.Contains("arib-std-b67", prores.Arguments);
        Assert.Contains(VideoEncodePlanner.ToneMappedWarning, hap.Warnings);
        Assert.DoesNotContain(VideoEncodePlanner.HdrKeptWarning, hap.Warnings);
    }

    // ---- progress

    [Fact]
    public async Task A_failed_ffmpeg_run_never_reports_completion()
    {
        using var ws = new MediaWorkspace("progress-fail");
        var kit = new MediaToolkit(ws.Inner.Context);
        var fractions = new List<double>();
        var progress = new SyncProgress(p =>
        {
            if (p.Fraction is { } f)
            {
                lock (fractions)
                    fractions.Add(f);
            }
        });

        // Fails when the encoder opens — after FFmpeg has already printed "progress=end".
        await Assert.ThrowsAsync<OperationException>(() => kit.Ffmpeg.RunAsync(
            ["-f", "lavfi", "-i", "testsrc2=size=160x90:duration=1", "-c:v", "libx264", "-profile:v", "baseline", "-pix_fmt", "yuv444p", "-f", "null", "-"],
            new FfmpegRunOptions { ExpectedDuration = TimeSpan.FromSeconds(1), Progress = progress }));

        Assert.DoesNotContain(fractions, f => f >= 1);
    }

    [Fact]
    public void Monotonic_progress_never_goes_back_or_past_one()
    {
        var seen = new List<double?>();
        var progress = MonotonicProgress.Wrap(new SyncProgress(p => seen.Add(p.Fraction)));

        progress.Report(JobProgress.Indeterminate("Reading…"));
        progress.Report(new JobProgress(0.5, "GPU"));
        progress.Report(new JobProgress(0.2, "software"));
        progress.Report(JobProgress.Indeterminate("Checking…"));
        progress.Report(new JobProgress(1.5, "done"));

        Assert.Equal(new double?[] { null, 0.5, 0.5, 0.5, 1.0 }, seen);
        Assert.Same(progress, MonotonicProgress.Wrap(progress));
    }

    static byte[] Replace(byte[] data, byte[] find, byte[] with)
    {
        var copy = (byte[])data.Clone();
        var replaced = 0;
        for (var i = 0; i <= copy.Length - find.Length; i++)
        {
            if (copy.AsSpan(i, find.Length).SequenceEqual(find))
            {
                with.CopyTo(copy, i);
                replaced++;
            }
        }
        Assert.True(replaced > 0, "fourcc not found");
        return copy;
    }
}
