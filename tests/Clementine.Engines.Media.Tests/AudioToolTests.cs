using System.Globalization;
using Clementine.Core;
using Clementine.Engines.Media.Tests.Support;
using Clementine.TestSupport;

namespace Clementine.Engines.Media.Tests;

/// <summary>Each audio tool on a 2-second tone.</summary>
public class AudioToolTests
{
    static Dictionary<string, object?> Set(params (string Key, object? Value)[] values) => values.ToDictionary(v => v.Key, v => v.Value);

    [Fact]
    public async Task Trim_keeps_the_format_and_cover_art()
    {
        using var ws = new MediaWorkspace("audio-trim");
        var input = await ws.UseAsync(Fixture.TaggedMp3, "Sóng – ünïcødé.mp3");

        var result = await ws.RunAsync("audio.trim", input, Set(("start", "0:00.5"), ("end", "0:01.5")));

        var output = Assert.Single(result.Outputs);
        Assert.Equal("Sóng – ünïcødé (trimmed).mp3", Path.GetFileName(output));
        var info = await new FFmpeg.FfprobeService(ws.Inner.Context.Tools).ProbeAsync(output, default);
        Assert.InRange(info.DurationOrZero.TotalSeconds, 0.9, 1.1);
        Assert.Single(info.AttachedPictures);
        Assert.Equal("Clementine Test", info.Tags.GetValueOrDefault("title"));
    }

    [Fact]
    public async Task Trim_end_zero_or_before_the_start_means_to_the_end()
    {
        using var ws = new MediaWorkspace("audio-trim-open");
        var input = await ws.UseAsync(Fixture.ToneWav);

        var open = await ws.RunAsync("audio.trim", input, Set(("start", "0:00.5"), ("end", TimeSpan.Zero)));
        var backwards = await ws.RunAsync("audio.trim", input, Set(("start", "0:01.5"), ("end", "0:01")));

        Assert.InRange((await MediaFixtures.ProbeAsync(Assert.Single(open.Outputs))).Duration, 1.45, 1.55);
        Assert.Empty(open.Warnings);
        Assert.InRange((await MediaFixtures.ProbeAsync(Assert.Single(backwards.Outputs))).Duration, 0.45, 0.55);
        Assert.Contains(backwards.Warnings, w => w.Contains("wasn't after the start", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("audio.normalize.streaming", -14.0)]
    [InlineData("audio.normalize.broadcast", -23.0)]
    public async Task Loudness_normalisation_hits_the_target(string id, double lufs)
    {
        using var ws = new MediaWorkspace("audio-norm");
        var input = await SharedFixtures.FfmpegTo(ws.Path("quiet.wav"),
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=4", "-af", "volume=-20dB");

        var result = await ws.RunAsync(id, input);

        var output = Assert.Single(result.Outputs);
        Assert.Equal(FormattableString.Invariant($"quiet ({lufs:0} LUFS).wav"), Path.GetFileName(output));
        Assert.Equal(48000, (await MediaFixtures.ProbeAsync(output)).Audio!.SampleRate);
        Assert.InRange(await MeasureLufsAsync(output), lufs - 1.5, lufs + 1.5);
    }

    [Fact]
    public async Task Peak_normalisation_brings_the_peak_near_zero()
    {
        using var ws = new MediaWorkspace("audio-peak");
        var input = await SharedFixtures.FfmpegTo(ws.Path("quiet.wav"),
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=2", "-af", "volume=-12dB");

        var result = await ws.RunAsync("audio.normalize.peak", input);

        Assert.Equal("quiet (peak 0 dB).wav", Path.GetFileName(Assert.Single(result.Outputs)));
        Assert.InRange(await MaxVolumeAsync(Assert.Single(result.Outputs)), -0.6, 0.0);
    }

    [Fact]
    public async Task Volume_changes_the_level()
    {
        using var ws = new MediaWorkspace("audio-volume");
        var input = await ws.UseAsync(Fixture.ToneWav);
        var before = await MaxVolumeAsync(input);

        var result = await ws.RunAsync("audio.volume", input, Set(("db", -6)));

        var output = Assert.Single(result.Outputs);
        Assert.Equal("tone (-6 dB).wav", Path.GetFileName(output));
        Assert.InRange(await MaxVolumeAsync(output) - before, -6.5, -5.5);
    }

    [Theory]
    [InlineData("audio.channels.mono", 1)]
    [InlineData("audio.channels.stereo", 2)]
    [InlineData("audio.channels.swap", 2)]
    [InlineData("audio.channels.left", 1)]
    [InlineData("audio.channels.right", 1)]
    public async Task Channels(string id, int channels)
    {
        using var ws = new MediaWorkspace("audio-channels");
        var input = await ws.UseAsync(Fixture.ToneWav);

        var result = await ws.RunAsync(id, input);

        Assert.Equal(channels, (await MediaFixtures.ProbeAsync(Assert.Single(result.Outputs))).Audio!.Channels);
    }

    [Fact]
    public async Task Channel_tools_explain_that_mono_has_one_channel()
    {
        using var ws = new MediaWorkspace("audio-channels-mono");
        var input = await ws.UseAsync(Fixture.TaggedMp3);

        var error = await Assert.ThrowsAsync<OperationException>(() => ws.RunAsync("audio.channels.swap", input));
        Assert.Contains("mono", error.Message);
    }

    [Fact]
    public async Task Speed_changes_the_duration()
    {
        using var ws = new MediaWorkspace("audio-speed");
        var input = await ws.UseAsync(Fixture.ToneWav);

        var result = await ws.RunAsync("audio.speed", input, Set(("factor", 2)));

        Assert.InRange((await MediaFixtures.ProbeAsync(Assert.Single(result.Outputs))).Duration, 0.95, 1.05);
    }

    [Theory]
    [InlineData("audio.fade.default")]
    [InlineData("audio.fade.custom")]
    public async Task Fade_starts_and_ends_silent(string id)
    {
        using var ws = new MediaWorkspace("audio-fade");
        var input = await ws.UseAsync(Fixture.ToneWav);

        var result = await ws.RunAsync(id, input, Set(("in", 0.5), ("out", 0.5)));

        var output = Assert.Single(result.Outputs);
        Assert.True(await MaxVolumeAsync(output, "0", "0.02") < -30, "start is faded in");
        Assert.True(await MaxVolumeAsync(output, "1.97", "0.03") < -25, "end is faded out");
    }

    [Theory]
    [InlineData("tone")]
    [InlineData("silence")]
    public async Task Bleep_covers_the_ranges(string sound)
    {
        using var ws = new MediaWorkspace("audio-bleep");
        var input = await ws.UseAsync(Fixture.ToneWav);

        var result = await ws.RunAsync("audio.bleep", input, Set(("ranges", "0:00.5-0:00.9, 1.2–1.4"), ("sound", sound)));

        var output = Assert.Single(result.Outputs);
        Assert.InRange((await MediaFixtures.ProbeAsync(output)).Duration, 1.95, 2.05);
        var inside = await MaxVolumeAsync(output, "0.6", "0.2");
        if (sound == "silence")
            Assert.True(inside < -60, $"range should be silent, peak {inside}");
        else
            Assert.InRange(inside, -17, -9); // about −12 dB (−15 once upmixed to stereo)
        var original = await MaxVolumeAsync(input, "0.1", "0.2");
        Assert.InRange(await MaxVolumeAsync(output, "0.1", "0.2"), original - 0.5, original + 0.5);
    }

    [Fact]
    public async Task Bleep_rejects_unreadable_ranges()
    {
        using var ws = new MediaWorkspace("audio-bleep-bad");
        var input = await ws.UseAsync(Fixture.ToneWav);

        var error = await Assert.ThrowsAsync<OperationException>(() => ws.RunAsync("audio.bleep", input, Set(("ranges", "soon-ish"))));
        Assert.Contains("0:12.5-0:13.2", error.Message);
    }

    [Fact]
    public void Normalize_presets_have_distinct_names()
    {
        var names = string.Join("|", Enum.GetValues<Audio.NormalizeTarget>().Select(Audio.NormalizeOperation.Suffix));
        Assert.Equal("-14 LUFS|-16 LUFS|-23 LUFS|peak 0 dB", names);
    }

    [Theory]
    [InlineData("audio.compress.smaller", 128, "long (compressed).mp3")]
    [InlineData("audio.compress.target", 64, "long (0.17 MB).mp3")]
    public async Task Compress(string id, int kbps, string name)
    {
        using var ws = new MediaWorkspace("audio-compress");
        var input = await SharedFixtures.FfmpegTo(ws.Path("long.wav"), "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100:duration=20");

        var result = await ws.RunAsync(id, input, Set(("sizeMb", 0.17)));

        var output = Assert.Single(result.Outputs);
        Assert.Equal(name, Path.GetFileName(output));
        var probe = await MediaFixtures.ProbeAsync(output);
        Assert.InRange(new FileInfo(output).Length * 8 / probe.Duration / 1000, kbps * 0.85, kbps * 1.15);
    }

    [Theory]
    [InlineData("parts", 4)]
    [InlineData("seconds", 2)]
    [InlineData("silence", 3)]
    public async Task Split(string mode, int expected)
    {
        using var ws = new MediaWorkspace("audio-split");
        // tone · silence · tone · silence · tone (1 s each)
        var input = await SharedFixtures.FfmpegTo(ws.Path("talk.wav"),
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=5",
            "-af", "volume=enable='between(t,1,2)+between(t,3,4)':volume=0");

        var result = await ws.RunAsync("audio.split", input, Set(("mode", mode), ("parts", 4), ("seconds", 3), ("threshold", -40), ("minSilence", 0.5)));

        var folder = Assert.Single(result.Outputs);
        var files = Directory.GetFiles(folder).Order().ToList();
        Assert.Equal(expected, files.Count);
        Assert.Equal("talk 01.wav", Path.GetFileName(files[0]));
    }

    [Fact]
    public async Task Split_a_name_starting_with_a_dash()
    {
        // "-intro 01.wav" as a bare pattern would be read by FFmpeg as an option.
        using var ws = new MediaWorkspace("audio-split-dash");
        var input = await ws.UseAsync(Fixture.ToneWav, "-intro.wav");

        var result = await ws.RunAsync("audio.split", input, Set(("mode", "parts"), ("parts", 2)));

        var files = string.Join("|", Directory.GetFiles(Assert.Single(result.Outputs)).Select(Path.GetFileName).Order());
        Assert.Equal("-intro 01.wav|-intro 02.wav", files);
    }

    [Fact]
    public async Task Join_same_and_mixed_formats()
    {
        using var ws = new MediaWorkspace("audio-join");
        var a = await ws.UseAsync(Fixture.ToneWav, "take 1.wav");
        var b = await ws.UseAsync(Fixture.ToneWav, "take 2.wav");
        var c = await ws.UseAsync(Fixture.TaggedMp3, "take 3.mp3");

        var same = await ws.RunAsync("audio.join", [b, a]);
        var mixed = await ws.RunAsync("audio.join", [a, c]);

        Assert.Equal("take 1 (joined).wav", Path.GetFileName(Assert.Single(same.Outputs)));
        Assert.InRange((await MediaFixtures.ProbeAsync(Assert.Single(same.Outputs))).Duration, 3.95, 4.05);
        Assert.Empty(same.Warnings);
        Assert.InRange((await MediaFixtures.ProbeAsync(Assert.Single(mixed.Outputs))).Duration, 3.9, 4.15);
        Assert.NotEmpty(mixed.Warnings);
    }

    [Fact]
    public async Task Reverse_and_waveform_video()
    {
        using var ws = new MediaWorkspace("audio-reverse");
        var input = await ws.UseAsync(Fixture.ToneWav);

        var reversed = await ws.RunAsync("audio.reverse", input);
        var waveform = await ws.RunAsync("audio.waveform", input);

        Assert.InRange((await MediaFixtures.ProbeAsync(Assert.Single(reversed.Outputs))).Duration, 1.95, 2.05);
        var video = await MediaFixtures.ProbeAsync(Assert.Single(waveform.Outputs));
        Assert.Equal(("h264", 1280, 720), (video.Video!.CodecName, video.Video.Width, video.Video.Height));
        Assert.True(video.HasAudio);
        var frame = await Frames.DecodeAsync(Assert.Single(waveform.Outputs), 1);
        Frames.AssertColor(frame.At(5, 5), 0x15, 0x15, 0x15, 6, "dark background");
    }

    [Fact]
    public async Task Remove_metadata_and_info()
    {
        using var ws = new MediaWorkspace("audio-meta");
        var input = await ws.UseAsync(Fixture.TaggedMp3);

        var stripped = await ws.RunAsync("audio.metadata.remove", input);
        var info = await ws.RunAsync("audio.info", input);

        var probe = await new FFmpeg.FfprobeService(ws.Inner.Context.Tools).ProbeAsync(Assert.Single(stripped.Outputs), default);
        Assert.False(probe.Tags.ContainsKey("title"));
        Assert.Empty(probe.AttachedPictures);
        Assert.Contains("Clementine Test", info.Text);
        Assert.Contains("44100 Hz", info.Text);
        Assert.Contains("Cover art", info.Text);
    }

    static async Task<double> MeasureLufsAsync(string path)
    {
        var result = await ProcessRunner.RunAsync(MediaFixtures.Ffmpeg, ["-hide_banner", "-nostdin", "-i", path, "-af", "ebur128", "-f", "null", "-"]);
        var marker = result.StdErrTail.LastIndexOf("I:", StringComparison.Ordinal);
        var text = result.StdErrTail[(marker + 2)..].TrimStart();
        return double.Parse(text[..text.IndexOf(' ')], CultureInfo.InvariantCulture);
    }

    static async Task<double> MaxVolumeAsync(string path, string? start = null, string? length = null)
    {
        var args = new List<string> { "-hide_banner", "-nostdin" };
        if (start is not null)
            args.AddRange(["-ss", start, "-t", length!]);
        args.AddRange(["-i", path, "-af", "volumedetect", "-f", "null", "-"]);
        var result = await ProcessRunner.RunAsync(MediaFixtures.Ffmpeg, args);
        var match = System.Text.RegularExpressions.Regex.Match(result.StdErrTail, @"max_volume:\s*(-?[\d.]+|-?inf)");
        Assert.True(match.Success, result.StdErrTail);
        return match.Groups[1].Value.Contains("inf") ? -200 : double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }
}
