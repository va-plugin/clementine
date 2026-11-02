using Clementine.Core;
using Clementine.Engines.Media.Encoders;
using Clementine.Engines.Media.Tests.Support;
using Clementine.TestSupport;

namespace Clementine.Engines.Media.Tests;

/// <summary>DXV, HAP and ProRes — the codecs a Resolume VJ actually plays. Verified down to tags, pixels and audio.</summary>
public class VjCodecTests
{
    [Theory]
    [InlineData("video.dxv.audio", "dxv", "DXD3", "rgba", true)]
    [InlineData("video.dxv.noaudio", "dxv", "DXD3", "rgba", false)]
    [InlineData("video.hap.audio", "hap", "Hap1", "rgb0", true)]
    [InlineData("video.hap.noaudio", "hap", "Hap1", "rgb0", false)]
    [InlineData("video.hap-alpha.audio", "hap", "Hap5", "rgba", true)]
    [InlineData("video.hap-alpha.noaudio", "hap", "Hap5", "rgba", false)]
    [InlineData("video.hap-q.audio", "hap", "HapY", "rgb0", true)]
    [InlineData("video.hap-q.noaudio", "hap", "HapY", "rgb0", false)]
    public async Task Dxv_and_hap_write_the_right_codec_tag_and_pcm_audio(string id, string codec, string tag, string decodedFormat, bool audio)
    {
        using var ws = new MediaWorkspace("vj-rgb");
        var input = await ws.UseAsync(Fixture.Clip, "Clip – ünïcødé 01.mp4");

        var result = await ws.RunAsync(id, input);

        var output = Assert.Single(result.Outputs);
        Assert.EndsWith(".mov", output);
        Assert.StartsWith("Clip – ünïcødé 01", System.IO.Path.GetFileName(output));
        var probe = await MediaFixtures.ProbeAsync(output);
        Assert.Equal(codec, probe.Video!.CodecName);
        Assert.Equal(tag, probe.Video.CodecTag);
        Assert.Equal(decodedFormat, probe.Video.PixelFormat);
        Assert.Equal(320, probe.Video.Width);
        Assert.Equal(180, probe.Video.Height);
        if (audio)
        {
            Assert.Equal("pcm_s16le", probe.Audio!.CodecName);
            Assert.Equal(48000, probe.Audio.SampleRate);
            Assert.Equal(1, probe.Audio.Channels); // the fixture's tone is mono; channel count is kept
        }
        else
        {
            Assert.False(probe.HasAudio);
        }
        Assert.True(await MediaFixtures.DecodesCleanlyAsync(output), "output must decode without errors");
    }

    [Theory]
    [InlineData("video.prores.422hq.audio", "apch", "yuv422p10le", true)]
    [InlineData("video.prores.422hq.noaudio", "apch", "yuv422p10le", false)]
    [InlineData("video.prores.4444.audio", "ap4h", "yuva444p12le", true)]
    [InlineData("video.prores.4444.noaudio", "ap4h", "yuva444p12le", false)]
    [InlineData("video.prores.422.audio", "apcn", "yuv422p10le", true)]
    [InlineData("video.prores.422lt.audio", "apcs", "yuv422p10le", true)]
    [InlineData("video.prores.422proxy.audio", "apco", "yuv422p10le", true)]
    [InlineData("video.prores.4444xq.audio", "ap4x", "yuva444p12le", true)]
    public async Task ProRes_profiles_have_the_right_fourcc_pixel_format_and_24_bit_audio(string id, string tag, string decodedFormat, bool audio)
    {
        using var ws = new MediaWorkspace("vj-prores");
        var input = await ws.UseAsync(Fixture.Clip);

        var result = await ws.RunAsync(id, input);

        var probe = await MediaFixtures.ProbeAsync(Assert.Single(result.Outputs));
        Assert.Equal("prores", probe.Video!.CodecName);
        Assert.Equal(tag, probe.Video.CodecTag);
        Assert.Equal(decodedFormat, probe.Video.PixelFormat); // FFmpeg decodes 4444 as 12-bit; alpha plane present
        if (audio)
        {
            Assert.Equal("pcm_s24le", probe.Audio!.CodecName);
            Assert.Equal(48000, probe.Audio.SampleRate);
        }
        else
        {
            Assert.False(probe.HasAudio);
        }
        Assert.True(await MediaFixtures.DecodesCleanlyAsync(Assert.Single(result.Outputs)));
    }

    [Theory]
    [InlineData("video.hap-alpha.noaudio")]
    [InlineData("video.prores.4444.noaudio")]
    [InlineData("video.prores.4444xq.audio")]
    public async Task Alpha_in_gives_alpha_out_with_transparent_pixels_intact(string id)
    {
        using var ws = new MediaWorkspace("vj-alpha");
        var input = await ws.UseAsync(Fixture.AlphaClip);

        var result = await ws.RunAsync(id, input);

        Assert.DoesNotContain(VideoEncodePlanner.OpaqueAlphaWarning, result.Warnings);
        var frame = await Frames.DecodeAsync(Assert.Single(result.Outputs), 0.5);
        Assert.True(frame.At(40, 90).A < 8, $"left half must stay transparent, alpha was {frame.At(40, 90).A}");
        Assert.True(frame.At(280, 90).A > 247, $"right half must stay opaque, alpha was {frame.At(280, 90).A}");
    }

    [Fact]
    public async Task Alpha_variant_on_an_opaque_source_writes_opaque_alpha_and_warns()
    {
        using var ws = new MediaWorkspace("vj-opaque");
        var input = await ws.UseAsync(Fixture.Clip);

        var result = await ws.RunAsync("video.hap-alpha.noaudio", input);

        Assert.Contains(VideoEncodePlanner.OpaqueAlphaWarning, result.Warnings);
        var frame = await Frames.DecodeAsync(Assert.Single(result.Outputs));
        Assert.Equal(255, frame.At(10, 10).A);
        Assert.Equal(255, frame.At(300, 170).A);
    }

    [Theory]
    [InlineData("video.hap.noaudio")]
    [InlineData("video.dxv.noaudio")]
    [InlineData("video.prores.422hq.noaudio")]
    public async Task Opaque_codecs_composite_transparency_over_black(string id)
    {
        using var ws = new MediaWorkspace("vj-flatten");
        var input = await ws.UseAsync(Fixture.AlphaClip);

        var result = await ws.RunAsync(id, input);

        var frame = await Frames.DecodeAsync(Assert.Single(result.Outputs), 0.5);
        Frames.AssertColor(frame.At(40, 90), 0, 0, 0, 6, "transparent area becomes black");
        Assert.Equal(255, frame.At(40, 90).A);
    }

    [Fact]
    public async Task Vp9_alpha_source_keeps_alpha_in_hap_alpha()
    {
        using var ws = new MediaWorkspace("vj-vp9a");
        var input = await ws.UseAsync(Fixture.AlphaWebm);

        var result = await ws.RunAsync("video.hap-alpha.noaudio", input);

        var frame = await Frames.DecodeAsync(Assert.Single(result.Outputs), 0.5);
        Assert.True(frame.At(40, 90).A < 8, "VP9 alpha must be decoded with libvpx so it survives");
        Assert.True(frame.At(280, 90).A > 247);
    }

    [Theory]
    [InlineData("video.dxv.audio", 1000, 564)]
    [InlineData("video.hap.audio", 1000, 564)]
    [InlineData("video.hap-q.noaudio", 1000, 564)]
    [InlineData("video.prores.422hq.audio", 1000, 562)]
    [InlineData("video.prores.4444.audio", 1000, 562)]
    [InlineData("video.mp4.h264", 1000, 562)]
    public async Task Odd_sizes_are_rounded_to_what_the_encoder_needs(string id, int width, int height)
    {
        using var ws = new MediaWorkspace("vj-odd");
        var input = await ws.UseAsync(Fixture.OddClip, "odd size 1001x563.mp4");
        Assert.Equal(1001, (await MediaFixtures.ProbeAsync(input)).Video!.Width);

        var result = await ws.RunAsync(id, input);

        var output = Assert.Single(result.Outputs);
        var probe = await MediaFixtures.ProbeAsync(output);
        Assert.Equal(width, probe.Video!.Width);
        Assert.Equal(height, probe.Video.Height);
        Assert.True(await MediaFixtures.DecodesCleanlyAsync(output));
    }

    [Fact]
    public async Task Surround_audio_is_resampled_to_48k_and_downmixed_for_hap_but_kept_for_prores()
    {
        using var ws = new MediaWorkspace("vj-audio");
        var input = await ws.UseAsync(Fixture.SurroundClip);

        var hap = await MediaFixtures.ProbeAsync(Assert.Single((await ws.RunAsync("video.hap.audio", input)).Outputs));
        var prores = await MediaFixtures.ProbeAsync(Assert.Single((await ws.RunAsync("video.prores.422hq.audio", input)).Outputs));

        Assert.Equal(("pcm_s16le", 48000, 2), (hap.Audio!.CodecName, hap.Audio.SampleRate, hap.Audio.Channels));
        Assert.Equal(("pcm_s24le", 48000, 6), (prores.Audio!.CodecName, prores.Audio.SampleRate, prores.Audio.Channels));
    }

    [Fact]
    public async Task With_audio_variant_on_a_silent_clip_warns_and_writes_video_only()
    {
        using var ws = new MediaWorkspace("vj-silent");
        var input = await ws.UseAsync(Fixture.SilentClip);

        var result = await ws.RunAsync("video.dxv.audio", input);

        Assert.Contains(VideoEncodePlanner.NoAudioWarning, result.Warnings);
        Assert.False((await MediaFixtures.ProbeAsync(Assert.Single(result.Outputs))).HasAudio);
    }

    [Fact]
    public async Task Irregular_time_base_sources_come_out_constant_frame_rate()
    {
        using var ws = new MediaWorkspace("vj-cfr");
        var input = await ws.UseAsync(Fixture.MkvClip);

        var result = await ws.RunAsync("video.hap.noaudio", input);

        var probe = await MediaFixtures.ProbeAsync(Assert.Single(result.Outputs));
        Assert.Equal(30, probe.Video!.FrameRate, 2);
    }

    [Fact]
    public async Task Rotated_phone_video_is_written_upright()
    {
        using var ws = new MediaWorkspace("vj-rotate");
        var clip = await SharedFixtures.GetAsync(Fixture.SilentClip);
        var rotated = await SharedFixtures.FfmpegTo(ws.Path("rotated.mp4"), "-display_rotation", "90", "-i", clip, "-c", "copy");

        var result = await ws.RunAsync("video.hap.noaudio", rotated);

        var probe = await MediaFixtures.ProbeAsync(Assert.Single(result.Outputs));
        Assert.Equal((180, 320), (probe.Video!.Width, probe.Video.Height));
    }

    [Fact]
    public async Task Interlaced_sources_are_deinterlaced_for_intra_codecs()
    {
        using var ws = new MediaWorkspace("vj-interlaced");
        var input = await SharedFixtures.FfmpegTo(ws.Path("interlaced.mp4"),
            "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=50:duration=0.5", "-vf", "tinterlace=interleave_top,fieldorder=tff",
            "-c:v", "libx264", "-preset", "ultrafast", "-flags", "+ildct+ilme", "-x264-params", "tff=1");
        var info = await new FFmpeg.FfprobeService(ws.Inner.Context.Tools).ProbeAsync(input, default);
        Assert.True(info.Video!.IsInterlaced, $"fixture field order: {info.Video.FieldOrder}");

        var result = await ws.RunAsync("video.prores.422hq.noaudio", input);

        var output = await new FFmpeg.FfprobeService(ws.Inner.Context.Tools).ProbeAsync(Assert.Single(result.Outputs), default);
        Assert.False(output.Video!.IsInterlaced);
        Assert.Equal(25, output.Video.Fps, 1);
    }

    [Theory]
    [InlineData(true, 1280, 720, "video.hap.noaudio")]
    [InlineData(false, 1280, 720, "video.hap.noaudio")]
    [InlineData(false, 640, 360, "video.dxv.noaudio")]
    [InlineData(true, 1280, 720, "video.prores.422hq.noaudio")]
    [InlineData(false, 640, 360, "video.prores.422hq.noaudio")]
    public async Task Colour_bars_survive_with_the_right_matrix(bool tagged, int width, int height, string id)
    {
        using var ws = new MediaWorkspace("vj-colour");
        // Encode a solid (200, 50, 50) with the matrix a player would assume for this size: 709 for HD, 601 for SD.
        var matrix = height >= 720 ? "bt709" : "bt601";
        var tag = height >= 720 ? "bt709" : "smpte170m";
        var args = new List<string>
        {
            "-f", "lavfi", "-i", $"color=c=0xC83232:s={width}x{height}:r=30:d=0.2",
            "-vf", $"scale=out_color_matrix={matrix}:out_range=tv,format=yuv420p" + (tagged ? "" : ",setparams=colorspace=unknown:color_primaries=unknown:color_trc=unknown"),
            "-c:v", "libx264", "-crf", "8", "-preset", "ultrafast",
        };
        if (tagged)
            args.AddRange(["-colorspace", tag, "-color_primaries", tag, "-color_trc", tag]);
        var input = await SharedFixtures.FfmpegTo(ws.Path($"bars {(tagged ? "tagged" : "untagged")}.mp4"), [.. args]);
        var inputProbe = await new FFmpeg.FfprobeService(ws.Inner.Context.Tools).ProbeAsync(input, default);
        Assert.Equal(tagged, inputProbe.Video!.ColorSpace is not null);

        var result = await ws.RunAsync(id, input);

        var frame = await Frames.DecodeAsync(Assert.Single(result.Outputs), 0.1);
        Frames.AssertColor(frame.At(width / 2, height / 2), 200, 50, 50, 8, $"{id} {(tagged ? "tagged" : "untagged")} {width}x{height}");
    }

    [Fact]
    public async Task Hdr_is_tone_mapped_for_hap_but_kept_in_prores()
    {
        using var ws = new MediaWorkspace("vj-hdr");
        // SDR (200, 50, 50) re-encoded as HDR10 (PQ, BT.2020, 10-bit H.265) — like a phone's HDR clip.
        var input = await SharedFixtures.FfmpegTo(ws.Path("hdr clip.mp4"),
            "-f", "lavfi", "-i", "color=c=0xC83232:s=640x360:r=30:d=0.3",
            "-vf", "format=gbrpf32le,zscale=tin=bt709:t=smpte2084:pin=bt709:p=bt2020:m=bt2020nc:r=tv:npl=100,format=yuv420p10le",
            "-color_primaries", "bt2020", "-color_trc", "smpte2084", "-colorspace", "bt2020nc",
            "-c:v", "libx265", "-preset", "ultrafast", "-x265-params", "log-level=error");

        var hap = await ws.RunAsync("video.hap.noaudio", input);
        var prores = await ws.RunAsync("video.prores.422hq.noaudio", input);

        Assert.Contains(VideoEncodePlanner.ToneMappedWarning, hap.Warnings);
        var frame = await Frames.DecodeAsync(Assert.Single(hap.Outputs), 0.1);
        Frames.AssertColor(frame.At(320, 180), 192, 50, 48, 16, "tone-mapped HDR red stays close to the SDR original");
        var kept = await new FFmpeg.FfprobeService(ws.Inner.Context.Tools).ProbeAsync(Assert.Single(prores.Outputs), default);
        Assert.Equal(("smpte2084", "bt2020"), (kept.Video!.ColorTransfer, kept.Video.ColorPrimaries));
        Assert.DoesNotContain(VideoEncodePlanner.ToneMappedWarning, prores.Warnings);
        Assert.Contains(VideoEncodePlanner.HdrKeptWarning, prores.Warnings);
    }

    [Fact]
    public async Task Progress_reaches_the_end_and_cancellation_leaves_nothing_behind()
    {
        using var ws = new MediaWorkspace("vj-cancel");
        var input = await ws.UseAsync(Fixture.Clip);
        var fractions = new List<double>();
        var progress = new SyncProgress(p =>
        {
            if (p.Fraction is { } f)
            {
                lock (fractions)
                    fractions.Add(f);
            }
        });

        await ws.RunAsync("video.prores.422hq.audio", input, progress: progress);
        Assert.True(fractions.Max() > 0.9, $"progress peaked at {fractions.DefaultIfEmpty().Max()}");

        // A longer encode, cancelled once it is under way.
        var longer = await SharedFixtures.FfmpegTo(ws.Path("long.mp4"),
            "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30:duration=20", "-c:v", "libx264", "-preset", "ultrafast");
        var before = ws.FilesInRoot().ToHashSet();
        using var cts = new CancellationTokenSource();
        var started = new SyncProgress(p =>
        {
            if (p.Fraction is > 0.02)
                cts.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ws.RunAsync("video.prores.4444xq.audio", longer, progress: started, cancellationToken: cts.Token));

        var leftovers = ws.FilesInRoot().Where(f => !before.Contains(f) && f != "_temp").ToList();
        Assert.True(leftovers.Count == 0, "cancelled encode left: " + string.Join(", ", leftovers));
    }

    [Fact]
    public async Task Audio_only_file_is_refused_with_a_friendly_message()
    {
        using var ws = new MediaWorkspace("vj-audioonly");
        var input = await ws.UseAsync(Fixture.AudioOnlyMp4);

        var error = await Assert.ThrowsAsync<OperationException>(() => ws.RunAsync("video.hap.audio", input));

        Assert.Contains("audio-only", error.Message);
    }
}

/// <summary>IProgress that reports synchronously (Progress&lt;T&gt; posts to the thread pool, which races assertions).</summary>
public sealed class SyncProgress(Action<JobProgress> report) : IProgress<JobProgress>
{
    public void Report(JobProgress value) => report(value);
}
