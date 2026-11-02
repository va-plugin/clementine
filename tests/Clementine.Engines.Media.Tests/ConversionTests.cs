using Clementine.Core;
using Clementine.Engines.Media.Tests.Support;
using Clementine.TestSupport;

namespace Clementine.Engines.Media.Tests;

/// <summary>Every non-VJ Convert operation, run once on a tiny clip and checked with ffprobe.</summary>
public class ConversionTests
{
    [Theory]
    [InlineData("video.mp4.h264", ".mp4", "h264", "avc1", "yuv420p", "aac")]
    [InlineData("video.mp4.h264.noaudio", ".mp4", "h264", "avc1", "yuv420p", null)]
    [InlineData("video.mp4.h265", ".mp4", "hevc", "hvc1", "yuv420p", "aac")]
    [InlineData("video.mp4.h265.noaudio", ".mp4", "hevc", "hvc1", "yuv420p", null)]
    [InlineData("video.mov.h264", ".mov", "h264", "avc1", "yuv420p", "aac")]
    [InlineData("video.mkv.h264", ".mkv", "h264", null, "yuv420p", "aac")]
    [InlineData("video.webm.vp9", ".webm", "vp9", null, "yuv420p", "opus")]
    [InlineData("video.webm.av1", ".webm", "av1", null, "yuv420p", "opus")]
    [InlineData("video.avi.mpeg4", ".avi", "mpeg4", "XVID", "yuv420p", "mp3")]
    [InlineData("video.wmv", ".wmv", "wmv2", null, "yuv420p", "wmav2")]
    public async Task Everyday_formats_have_the_expected_codecs(string id, string extension, string codec, string? tag, string pixelFormat, string? audioCodec)
    {
        using var ws = new MediaWorkspace("convert");
        var input = await ws.UseAsync(Fixture.Clip, "Clip – ünïcødé 01.mp4");

        var result = await ws.RunAsync(id, input);

        var output = Assert.Single(result.Outputs);
        Assert.EndsWith(extension, output);
        Assert.True(File.Exists(input), "the input must never be touched");
        var probe = await MediaFixtures.ProbeAsync(output);
        Assert.Equal(codec, probe.Video!.CodecName);
        if (tag is not null)
            Assert.Equal(tag, probe.Video.CodecTag);
        Assert.Equal(pixelFormat, probe.Video.PixelFormat);
        Assert.Equal(audioCodec, probe.Audio?.CodecName);
        Assert.InRange(probe.Duration, 0.8, 1.3);
        Assert.True(await MediaFixtures.DecodesCleanlyAsync(output));
    }

    [Fact]
    public async Task Same_extension_conversion_gets_a_suffix_instead_of_clashing()
    {
        using var ws = new MediaWorkspace("convert-suffix");
        var input = await ws.UseAsync(Fixture.Clip);

        var result = await ws.RunAsync("video.mp4.h264", input);

        Assert.Equal("clip (H.264).mp4", Path.GetFileName(Assert.Single(result.Outputs)));
    }

    [Fact]
    public async Task Hardware_h264_works_or_falls_back_silently()
    {
        using var ws = new MediaWorkspace("convert-hw", hardware: true);
        var input = await ws.UseAsync(Fixture.Clip);

        var result = await ws.RunAsync("video.mp4.h264", input);

        var probe = await MediaFixtures.ProbeAsync(Assert.Single(result.Outputs));
        Assert.Equal("h264", probe.Video!.CodecName);
        Assert.Equal("yuv420p", probe.Video.PixelFormat);
    }

    [Fact]
    public async Task Gpu_encoder_failing_after_it_started_falls_back_silently_without_progress_going_back()
    {
        using var ws = new MediaWorkspace("convert-hw-fallback", hardware: true);
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
        // A "GPU encoder" that only fails once FFmpeg opens it — after FFmpeg has already printed "progress=end",
        // like NVENC does for frames larger than it supports.
        var broken = new FFmpeg.HardwareEncoder("libx264", FFmpeg.HardwareVendor.Nvidia, IsHevc: false);
        OperationResult result;
        using (FFmpeg.HardwareEncoders.Pretend(ws.Inner.Context.Tools.RequireFfmpeg(), hevc: false, broken))
            result = await ws.RunAsync("video.mp4.h264", input, progress: progress);

        var probe = await MediaFixtures.ProbeAsync(Assert.Single(result.Outputs));
        Assert.Equal("h264", probe.Video!.CodecName);
        Assert.Empty(result.Warnings);
        Assert.Contains(ws.Inner.Log.Lines, l => l.Contains("retrying with the software encoder", StringComparison.Ordinal));
        for (var i = 1; i < fractions.Count; i++)
            Assert.True(fractions[i] >= fractions[i - 1], "progress went back: " + string.Join(", ", fractions.Select(f => f.ToString("0.00"))));
        Assert.Equal(1, fractions[^1]);
        Assert.Equal(1, fractions.Count(f => f >= 1));
    }

    [Fact]
    public async Task Vp9_alpha_keeps_transparency()
    {
        using var ws = new MediaWorkspace("convert-vp9a");
        var input = await ws.UseAsync(Fixture.AlphaClip);

        var result = await ws.RunAsync("video.webm.vp9.alpha", input);

        var frame = await Frames.DecodeAsync(Assert.Single(result.Outputs), 0.5, libvpx: true);
        Assert.True(frame.At(40, 90).A < 10, $"left should be transparent, alpha {frame.At(40, 90).A}");
        Assert.True(frame.At(280, 90).A > 245);
    }

    [Theory]
    [InlineData("video.gif.hq", 320)]
    [InlineData("video.gif.small", 320)]
    public async Task Gif_is_a_looping_palette_animation(string id, int width)
    {
        using var ws = new MediaWorkspace("convert-gif");
        var input = await ws.UseAsync(Fixture.Clip);

        var result = await ws.RunAsync(id, input);

        var probe = await MediaFixtures.ProbeAsync(Assert.Single(result.Outputs));
        Assert.Equal("gif", probe.Video!.CodecName);
        Assert.Equal(width, probe.Video.Width);
        Assert.InRange(probe.Video.FrameRate, 14, 16);
    }

    [Fact]
    public async Task Small_gif_is_capped_at_480_pixels()
    {
        using var ws = new MediaWorkspace("convert-gif-small");
        var input = await ws.UseAsync(Fixture.OddClip);

        var result = await ws.RunAsync("video.gif.small", input);

        Assert.Equal(480, (await MediaFixtures.ProbeAsync(Assert.Single(result.Outputs))).Video!.Width);
    }

    [Fact]
    public async Task Animated_webp_keeps_alpha()
    {
        using var ws = new MediaWorkspace("convert-webp");
        var input = await ws.UseAsync(Fixture.AlphaClip);

        var result = await ws.RunAsync("video.webp.animated", input);

        var output = Assert.Single(result.Outputs);
        Assert.EndsWith(".webp", output);
        var bytes = await File.ReadAllBytesAsync(output);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Contains("ANIM", System.Text.Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 200)));
        Assert.Contains("ALPH", System.Text.Encoding.ASCII.GetString(bytes));
    }

    [Fact]
    public async Task Animated_gif_converts_to_mp4()
    {
        using var ws = new MediaWorkspace("convert-fromgif");
        var input = await ws.UseAsync(Fixture.Gif);

        var result = await ws.RunAsync("video.mp4.h264", input);

        var probe = await MediaFixtures.ProbeAsync(Assert.Single(result.Outputs));
        Assert.Equal(("h264", 160, 90), (probe.Video!.CodecName, probe.Video.Width, probe.Video.Height));
        Assert.False(probe.HasAudio);
        Assert.Empty(result.Warnings);
    }

    [Theory]
    [InlineData("video.hap.audio")]
    [InlineData("video.dxv.audio")]
    [InlineData("video.prores.4444.audio")]
    public async Task With_audio_vj_variants_on_a_gif_write_video_only_without_a_no_audio_warning(string id)
    {
        using var ws = new MediaWorkspace("convert-gifvj");
        var input = await ws.UseAsync(Fixture.Gif);

        var result = await ws.RunAsync(id, input);

        Assert.False((await MediaFixtures.ProbeAsync(Assert.Single(result.Outputs))).HasAudio);
        Assert.DoesNotContain(Encoders.VideoEncodePlanner.NoAudioWarning, result.Warnings);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task Transparent_gif_round_trips_into_hap_alpha()
    {
        using var ws = new MediaWorkspace("convert-gifalpha");
        var input = await ws.UseAsync(Fixture.AlphaClip, "overlay.mov");

        var gif = Assert.Single((await ws.RunAsync("video.gif.hq", input)).Outputs);
        var hap = Assert.Single((await ws.RunAsync("video.hap-alpha.audio", gif)).Outputs);

        var frame = await Frames.DecodeAsync(hap, 0.5);
        Assert.True(frame.At(40, 90).A < 8, "GIF transparency must become HAP alpha");
        Assert.True(frame.At(280, 90).A > 247);
    }

    [Fact]
    public async Task Gif_conversions_are_not_offered_for_gif_inputs()
    {
        using var ws = new MediaWorkspace("convert-gifaccept");
        var gif = InputFile.FromPath(await ws.UseAsync(Fixture.Gif));

        Assert.False(ws.Op("video.gif.hq").Accepts(gif));
        Assert.False(ws.Op("video.webp.animated").Accepts(gif));
        Assert.True(ws.Op("video.prores.422lt.audio").Accepts(gif));
        Assert.False(ws.Op("video.audio.mp3").Accepts(gif));
    }

    [Fact]
    public void Animated_images_get_the_plain_variants_but_never_the_no_audio_twins()
    {
        using var ws = new MediaWorkspace("convert-gif-twins");
        foreach (var name in new[] { "anim.gif", "anim.apng" })
        {
            var animation = InputFile.FromPath(ws.Inner.WriteText(name, ""));
            var offered = ws.All.Values.Where(op => op.Info.Category == OperationCategory.Convert && op.Accepts(animation)).Select(op => op.Info.Id).ToList();

            // GIF/APNG never carry sound: a "· no audio" twin would just double every entry.
            Assert.DoesNotContain(offered, id => id.EndsWith(".noaudio", StringComparison.Ordinal));
            foreach (var id in new[] { "video.mp4.h264", "video.mp4.h265", "video.dxv.audio", "video.hap.audio", "video.hap-alpha.audio", "video.hap-q.audio",
                         "video.prores.422hq.audio", "video.prores.4444.audio" })
                Assert.Contains(id, offered);
        }

        // Real videos still get both twins.
        var clip = InputFile.FromPath(ws.Inner.WriteText("clip.mov", ""));
        Assert.True(ws.Op("video.hap.noaudio").Accepts(clip));
        Assert.True(ws.Op("video.hap.audio").Accepts(clip));
    }

    [Fact]
    public async Task Mov_remux_copies_streams_without_re_encoding()
    {
        using var ws = new MediaWorkspace("convert-movremux");
        var input = await ws.UseAsync(Fixture.MkvClip);

        var result = await ws.RunAsync("video.mov.remux", input);

        var probe = await MediaFixtures.ProbeAsync(Assert.Single(result.Outputs));
        Assert.Equal("h264", probe.Video!.CodecName);
        Assert.Equal("aac", probe.Audio!.CodecName);
        Assert.Contains("mov", probe.FormatName);
    }

    [Fact]
    public async Task Mov_remux_refuses_codecs_mov_cannot_hold()
    {
        using var ws = new MediaWorkspace("convert-movremux-vp9");
        var input = await ws.UseAsync(Fixture.AlphaWebm);

        var error = await Assert.ThrowsAsync<OperationException>(() => ws.RunAsync("video.mov.remux", input));
        Assert.Contains("VP9", error.Message);
    }

    [Fact]
    public async Task Mkv_remux_keeps_every_stream_including_subtitles()
    {
        using var ws = new MediaWorkspace("convert-mkvremux");
        var input = await ws.UseAsync(Fixture.SubtitledClip);

        var result = await ws.RunAsync("video.mkv.remux", input);

        var probe = await MediaFixtures.ProbeAsync(Assert.Single(result.Outputs));
        Assert.Contains(probe.Streams, s => s.CodecType == "subtitle" && s.CodecName == "subrip");
        Assert.Equal("h264", probe.Video!.CodecName);
        Assert.Equal("aac", probe.Audio!.CodecName);
    }

    [Theory]
    [InlineData("video.audio.mp3", ".mp3", "mp3")]
    [InlineData("video.audio.wav", ".wav", "pcm_s16le")]
    [InlineData("video.audio.m4a", ".m4a", "aac")]
    [InlineData("video.audio.flac", ".flac", "flac")]
    public async Task Sound_track_extraction(string id, string extension, string codec)
    {
        using var ws = new MediaWorkspace("convert-extract");
        var input = await ws.UseAsync(Fixture.Clip);

        var result = await ws.RunAsync(id, input);

        var output = Assert.Single(result.Outputs);
        Assert.EndsWith(extension, output);
        var probe = await MediaFixtures.ProbeAsync(output);
        Assert.Null(probe.Video);
        Assert.Equal(codec, probe.Audio!.CodecName);
    }

    [Fact]
    public async Task Extraction_from_a_silent_video_says_so()
    {
        using var ws = new MediaWorkspace("convert-extract-silent");
        var input = await ws.UseAsync(Fixture.SilentClip);

        var error = await Assert.ThrowsAsync<OperationException>(() => ws.RunAsync("video.audio.mp3", input));
        Assert.Equal("This video has no audio track.", error.Message);
    }

    [Theory]
    [InlineData("audio.mp3.320", ".mp3", "mp3", 320)]
    [InlineData("audio.mp3.192", ".mp3", "mp3", 192)]
    [InlineData("audio.mp3.128", ".mp3", "mp3", 128)]
    [InlineData("audio.m4a.aac", ".m4a", "aac", 0)]
    [InlineData("audio.m4a.alac", ".m4a", "alac", 0)]
    [InlineData("audio.wav.16", ".wav", "pcm_s16le", 0)]
    [InlineData("audio.wav.24", ".wav", "pcm_s24le", 0)]
    [InlineData("audio.flac", ".flac", "flac", 0)]
    [InlineData("audio.ogg", ".ogg", "vorbis", 0)]
    [InlineData("audio.opus", ".opus", "opus", 0)]
    [InlineData("audio.aiff.16", ".aiff", "pcm_s16be", 0)]
    [InlineData("audio.aiff.24", ".aiff", "pcm_s24be", 0)]
    public async Task Audio_conversions(string id, string extension, string codec, int kbps)
    {
        using var ws = new MediaWorkspace("convert-audio");
        var input = await ws.UseAsync(Fixture.TaggedMp3, "Sóng – ünïcødé.mp3");
        if (extension == ".mp3")
            input = await ws.UseAsync(Fixture.ToneWav, "Sóng – ünïcødé.wav");

        var result = await ws.RunAsync(id, input);

        var output = Assert.Single(result.Outputs);
        Assert.EndsWith(extension, output);
        var probe = await MediaFixtures.ProbeAsync(output);
        Assert.Equal(codec, probe.Audio!.CodecName);
        Assert.InRange(probe.Duration, 1.8, 2.3);
        if (kbps > 0)
            Assert.InRange(new FileInfo(output).Length * 8 / probe.Duration / 1000, kbps * 0.85, kbps * 1.15);
    }

    [Theory]
    [InlineData("audio.m4a.aac", true)]
    [InlineData("audio.flac", true)]
    [InlineData("audio.wav.16", false)]
    [InlineData("audio.ogg", false)]
    public async Task Tags_are_kept_and_cover_art_only_where_supported(string id, bool coverExpected)
    {
        using var ws = new MediaWorkspace("convert-tags");
        var input = await ws.UseAsync(Fixture.TaggedMp3);

        var result = await ws.RunAsync(id, input);

        var output = Assert.Single(result.Outputs);
        var info = await new FFmpeg.FfprobeService(ws.Inner.Context.Tools).ProbeAsync(output, default);
        Assert.Equal(coverExpected, info.AttachedPictures.Count == 1);
        var title = info.Tags.GetValueOrDefault("title") ?? info.Audio!.Tags.GetValueOrDefault("title")
            ?? info.Tags.GetValueOrDefault("INAM");
        Assert.Equal("Clementine Test", title);
        if (!coverExpected)
            Assert.Contains(result.Warnings, w => w.Contains("Cover art", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Audio_conversion_to_its_own_format_is_not_offered()
    {
        using var ws = new MediaWorkspace("convert-audio-own");
        var mp3 = InputFile.FromPath(await ws.UseAsync(Fixture.TaggedMp3));

        Assert.False(ws.Op("audio.mp3.320").Accepts(mp3));
        Assert.True(ws.Op("audio.wav.16").Accepts(mp3));
    }
}
