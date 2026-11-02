using Clementine.Core;
using Clementine.Engines.Media.FFmpeg;
using Clementine.Engines.Media.Tests.Support;

namespace Clementine.Engines.Media.Tests;

/// <summary>"ffmpeg -encoders" parsing and the per-operation encoder check (essentials builds lack libx265, HAP…).</summary>
public class EncoderAvailabilityTests
{
    /// <summary>Real "ffmpeg -hide_banner -encoders" output (trimmed), legend included.</summary>
    const string CannedList = """
        Encoders:
         V..... = Video
         A..... = Audio
         S..... = Subtitle
         .F.... = Frame-level multithreading
         ..S... = Slice-level multithreading
         ...X.. = Codec is experimental
         ....B. = Supports draw_horiz_band
         .....D = Supports direct rendering method 1
         ------
         V....D a64multi             Multicolor charset for Commodore 64 (codec a64_multi)
         V..... libsvtav1            SVT-AV1(Scalable Video Technology for AV1) encoder (codec av1)
         VFS..D dxv                  Resolume DXV
         V....D gif                  GIF (Graphics Interchange Format)
         V....D libx264              libx264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 (codec h264)
         V....D h264_nvenc           NVIDIA NVENC H.264 encoder (codec h264)
         V.S..D hap                  Vidvox Hap
         V.S..D mpeg4                MPEG-4 part 2
         VFS... prores_ks            Apple ProRes (iCodec Pro) (codec prores)
         V....D libvpx-vp9           libvpx VP9 (codec vp9)
         V....D libwebp_anim         libwebp WebP image (codec webp)
         V....D wmv2                 Windows Media Video 8
         A....D aac                  AAC (Advanced Audio Coding)
         A....D alac                 ALAC (Apple Lossless Audio Codec)
         A....D flac                 FLAC (Free Lossless Audio Codec)
         A....D libmp3lame           libmp3lame MP3 (MPEG audio layer 3) (codec mp3)
         A....D libopus              libopus Opus (codec opus)
         A....D pcm_s16le            PCM signed 16-bit little-endian
         A....D wmav2                Windows Media Audio 2
         S..... srt                  SubRip subtitle
        """;

    [Fact]
    public void Parser_reads_encoder_names_and_skips_the_legend()
    {
        var names = FfmpegEncoders.Parse(CannedList.Replace("\n", "\r\n", StringComparison.Ordinal));

        Assert.Equal(20, names.Count);
        foreach (var name in new[] { "libx264", "h264_nvenc", "hap", "dxv", "prores_ks", "libvpx-vp9", "libsvtav1", "mpeg4", "gif", "libwebp_anim", "wmv2",
                     "aac", "alac", "flac", "libmp3lame", "libopus", "wmav2", "srt" })
            Assert.Contains(name, names);
        Assert.DoesNotContain("=", names);
        Assert.DoesNotContain("libx265", names);
        Assert.DoesNotContain("libvorbis", names);
        Assert.Empty(FfmpegEncoders.Parse("ffmpeg version n7.1\nUnrecognized option 'encoders'."));
    }

    [Fact]
    public void Missing_encoder_makes_the_operation_unavailable_and_unknown_means_available()
    {
        var known = FfmpegEncoders.Parse(CannedList);

        var missing = FfmpegEncoders.Check(known, ["libx265", "aac"]);
        Assert.False(missing.IsAvailable);
        Assert.Equal("This FFmpeg build has no libx265 encoder — install a full build (winget install Gyan.FFmpeg)", missing.Reason);
        Assert.True(FfmpegEncoders.Check(known, ["libx264", "aac"]).IsAvailable);
        Assert.True(FfmpegEncoders.Check(known, []).IsAvailable);
        Assert.True(FfmpegEncoders.Check(null, ["libx265"]).IsAvailable); // probe still running (or failed)
    }

    [Theory]
    [InlineData("video.mp4.h264", "libx264,aac")]
    [InlineData("video.mp4.h264.noaudio", "libx264")]
    [InlineData("video.mp4.h265", "libx265,aac")]
    [InlineData("video.mov.h264", "libx264,aac")]
    [InlineData("video.mkv.h264", "libx264,aac")]
    [InlineData("video.webm.vp9", "libvpx-vp9,libopus")]
    [InlineData("video.webm.vp9.alpha", "libvpx-vp9,libopus")]
    [InlineData("video.webm.av1", "libsvtav1,libopus")]
    [InlineData("video.gif.hq", "gif")]
    [InlineData("video.webp.animated", "libwebp_anim")]
    [InlineData("video.dxv.audio", "dxv")]
    [InlineData("video.hap-alpha.noaudio", "hap")]
    [InlineData("video.prores.4444.audio", "prores_ks")]
    [InlineData("video.avi.mpeg4", "mpeg4,libmp3lame")]
    [InlineData("video.wmv", "wmv2,wmav2")]
    [InlineData("video.audio.mp3", "libmp3lame")]
    [InlineData("video.audio.m4a", "aac")]
    [InlineData("video.audio.flac", "flac")]
    [InlineData("video.audio.wav", "")]
    [InlineData("audio.mp3.192", "libmp3lame")]
    [InlineData("audio.m4a.aac", "aac")]
    [InlineData("audio.m4a.alac", "alac")]
    [InlineData("audio.flac", "flac")]
    [InlineData("audio.ogg", "libvorbis")]
    [InlineData("audio.opus", "libopus")]
    [InlineData("audio.wav.24", "")]
    [InlineData("audio.aiff.16", "")]
    [InlineData("video.compress.good", "libx264,aac")]
    [InlineData("video.compress.target", "libx264,aac")]
    [InlineData("audio.compress.smaller", "libmp3lame")]
    [InlineData("audio.waveform", "libx264,aac")]
    [InlineData("video.trim", "")]
    [InlineData("video.mov.remux", "")]
    public void Operations_name_the_encoders_they_need(string id, string encoders)
    {
        using var ws = new MediaWorkspace("encoders-required");
        var op = Assert.IsAssignableFrom<MediaOperation>(ws.Op(id));

        Assert.Equal(encoders, string.Join(",", op.RequiredEncoders));
    }

    [Fact]
    public void Operations_report_a_missing_encoder_once_the_build_is_known()
    {
        using var ws = new MediaWorkspace("encoders-missing");
        var ffmpeg = ws.Inner.Context.Tools.RequireFfmpeg();

        using (FfmpegEncoders.Pretend(ffmpeg, FfmpegEncoders.Parse(CannedList)))
        {
            var hevc = ws.Op("video.mp4.h265").CheckAvailability();
            Assert.False(hevc.IsAvailable);
            Assert.Equal(FfmpegEncoders.MissingMessage("libx265"), hevc.Reason);
            Assert.False(ws.Op("audio.ogg").CheckAvailability().IsAvailable);
            Assert.True(ws.Op("video.mp4.h264").CheckAvailability().IsAvailable);
            Assert.True(ws.Op("video.hap.audio").CheckAvailability().IsAvailable);
            Assert.True(ws.Op("video.trim").CheckAvailability().IsAvailable);
        }

        using (FfmpegEncoders.Pretend(ffmpeg, encoders: null))
            Assert.True(ws.Op("video.mp4.h265").CheckAvailability().IsAvailable); // not known yet → never blocks, never hides
    }

    [Fact]
    public async Task The_real_build_is_probed_once_and_has_every_encoder()
    {
        using var ws = new MediaWorkspace("encoders-real");
        var ffmpeg = ws.Inner.Context.Tools.RequireFfmpeg();

        var known = await FfmpegEncoders.GetAsync(ffmpeg, default);

        Assert.NotNull(known);
        foreach (var name in new[] { "libx264", "libx265", "mpeg4", "hap", "dxv", "prores_ks", "libvpx-vp9", "libsvtav1", "libmp3lame", "libopus", "libvorbis",
                     "libwebp_anim", "aac", "alac", "flac", "wmv2", "wmav2", "gif" })
            Assert.Contains(name, known);
        Assert.Same(known, await FfmpegEncoders.GetAsync(ffmpeg, default)); // cached per ffmpeg path
        Assert.Same(known, FfmpegEncoders.TryGetKnown(ffmpeg));
        Assert.All(ws.All.Values, op => Assert.True(op.CheckAvailability().IsAvailable, $"{op.Info.Id}: {op.CheckAvailability().Reason}"));
    }

    [Fact]
    public async Task A_broken_ffmpeg_path_never_hides_anything()
    {
        var bogus = Path.Combine(Path.GetTempPath(), "ClementineTests", $"no-ffmpeg-{Guid.NewGuid():N}", "ffmpeg.exe");

        Assert.Null(await FfmpegEncoders.GetAsync(bogus, default));
        Assert.True(FfmpegEncoders.Check(FfmpegEncoders.TryGetKnown(bogus), ["libx264"]).IsAvailable);
    }

    [Fact]
    public void Shared_tool_groups_need_no_video_or_audio_label()
    {
        using var ws = new MediaWorkspace("encoders-variants");

        foreach (var id in new[] { "video.trim", "audio.trim", "video.info", "audio.info", "video.join", "audio.join", "video.split", "audio.split", "audio.speed" })
            Assert.Null(ws.Op(id).Info.Variant);
    }
}
