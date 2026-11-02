using System.Collections.Concurrent;
using System.Globalization;
using Clementine.Core;
using Clementine.TestSupport;

namespace Clementine.Engines.Media.Tests.Support;

/// <summary>A decoded RGBA frame.</summary>
public sealed record RgbaFrame(int Width, int Height, byte[] Pixels)
{
    public (byte R, byte G, byte B, byte A) At(int x, int y)
    {
        var i = (y * Width + x) * 4;
        return (Pixels[i], Pixels[i + 1], Pixels[i + 2], Pixels[i + 3]);
    }
}

/// <summary>
/// Test plumbing: a workspace with hardware encoding off (deterministic), shared fixtures generated once per run
/// and copied into each test's own folder, and frame/pixel inspection.
/// </summary>
public sealed class MediaWorkspace : IDisposable
{
    readonly TestWorkspace _workspace;
    readonly MediaOperationProvider _provider = new();

    public MediaWorkspace(string name, bool hardware = false)
    {
        _workspace = new TestWorkspace(name);
        _workspace.Settings.Update(s => s.UseHardwareEncoding = hardware);
    }

    public TestWorkspace Inner => _workspace;

    public string Root => _workspace.Root;

    public SettingsStore Settings => _workspace.Settings;

    public IOperation Op(string id) => _workspace.Operation(_provider, id);

    public IReadOnlyDictionary<string, IOperation> All => _workspace.Operations(_provider);

    public string Path(string relative) => _workspace.Path(relative);

    /// <summary>Copies a shared fixture into this workspace (optionally renamed) and returns its path.</summary>
    public async Task<string> UseAsync(Fixture fixture, string? asName = null)
    {
        var source = await SharedFixtures.GetAsync(fixture);
        var target = Path(asName ?? System.IO.Path.GetFileName(source));
        File.Copy(source, target, overwrite: false);
        return target;
    }

    public Task<OperationResult> RunAsync(string id, string input, IReadOnlyDictionary<string, object?>? options = null, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default) =>
        _workspace.RunAsync(Op(id), [input], options, progress, cancellationToken);

    public Task<OperationResult> RunAsync(string id, IEnumerable<string> inputs, IReadOnlyDictionary<string, object?>? options = null) =>
        _workspace.RunAsync(Op(id), inputs, options);

    /// <summary>Files left in the workspace root that aren't inputs (partials included).</summary>
    public IReadOnlyList<string> FilesInRoot() => Directory.GetFileSystemEntries(Root).Select(System.IO.Path.GetFileName).ToList()!;

    public void Dispose() => _workspace.Dispose();
}

/// <summary>Fixtures shared by every test in the run.</summary>
public enum Fixture
{
    /// <summary>320×180, 1 s, 30 fps, H.264 + AAC 48 kHz stereo (.mp4).</summary>
    Clip,

    /// <summary>320×180, 1 s, H.264, no audio (.mp4).</summary>
    SilentClip,

    /// <summary>ProRes 4444 320×180 1 s, left half fully transparent (.mov).</summary>
    AlphaClip,

    /// <summary>1001×563 (odd) 0.5 s H.264 4:4:4 + audio (.mp4).</summary>
    OddClip,

    /// <summary>320×180 1 s with 5.1 audio at 44.1 kHz (.mov).</summary>
    SurroundClip,

    /// <summary>320×180 1 s VP9 WebM with alpha (left half transparent).</summary>
    AlphaWebm,

    /// <summary>2 s sine 48 kHz stereo WAV.</summary>
    ToneWav,

    /// <summary>2 s sine MP3 with title tag and cover art.</summary>
    TaggedMp3,

    /// <summary>320×180 2 s, keyframe every 0.5 s, H.264 + AAC (.mp4) — for splitting.</summary>
    ShortGopClip,

    /// <summary>Animated GIF 160×90, 1 s.</summary>
    Gif,

    /// <summary>MKV (1/1000 time base) 320×180 1 s 30 fps.</summary>
    MkvClip,

    /// <summary>A 2-entry SRT file.</summary>
    Subtitles,

    /// <summary>MP4 with a mov_text subtitle track.</summary>
    SubtitledClip,

    /// <summary>Audio-only file wearing a video extension (.mp4).</summary>
    AudioOnlyMp4,
}

/// <summary>Generates each fixture once per test run (thread-safe, lazily).</summary>
public static class SharedFixtures
{
    static readonly string Directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ClementineTests", $"media-fixtures-{Environment.ProcessId}");
    static readonly ConcurrentDictionary<Fixture, Lazy<Task<string>>> Cache = new();

    static SharedFixtures()
    {
        System.IO.Directory.CreateDirectory(Directory);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        };
    }

    public static Task<string> GetAsync(Fixture fixture) =>
        Cache.GetOrAdd(fixture, f => new Lazy<Task<string>>(() => CreateAsync(f))).Value;

    static async Task<string> CreateAsync(Fixture fixture)
    {
        switch (fixture)
        {
            case Fixture.Clip:
                return await MediaFixtures.VideoAsync(Directory, "clip.mp4", 320, 180, seconds: 1);
            case Fixture.SilentClip:
                return await MediaFixtures.VideoAsync(Directory, "silent.mp4", 320, 180, seconds: 1, audio: false);
            case Fixture.AlphaClip:
                return await MediaFixtures.AlphaVideoAsync(Directory, "alpha.mov", 320, 180, seconds: 1);
            case Fixture.OddClip:
                return await MediaFixtures.VideoAsync(Directory, "odd.mp4", 1000, 562, seconds: 0.5, fps: 30, audio: true,
                    "-vf", "scale=1001:563,setsar=1", "-c:v", "libx264", "-pix_fmt", "yuv444p", "-preset", "ultrafast", "-c:a", "aac");
            case Fixture.SurroundClip:
                return await Ffmpeg("surround.mov",
                    "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30:duration=1",
                    "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100:duration=1",
                    "-filter_complex", "[1:a]pan=5.1|c0=c0|c1=c0|c2=c0|c3=c0|c4=c0|c5=c0[a]",
                    "-map", "0:v", "-map", "[a]", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "pcm_s16le", "-shortest");
            case Fixture.AlphaWebm:
                return await Ffmpeg("alpha.webm",
                    "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30:duration=1",
                    "-vf", "format=rgba,geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':a='if(lt(X,160),0,255)',format=yuva420p",
                    "-c:v", "libvpx-vp9", "-deadline", "realtime", "-cpu-used", "8", "-auto-alt-ref", "0", "-b:v", "1M");
            case Fixture.ToneWav:
                return await MediaFixtures.AudioAsync(Directory, "tone.wav", seconds: 2);
            case Fixture.TaggedMp3:
                var cover = await MediaFixtures.ImageAsync(Directory, "cover.jpg", 64, 64);
                return await Ffmpeg("tagged.mp3",
                    "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100:duration=2", "-i", cover,
                    "-map", "0:a", "-map", "1:v", "-c:a", "libmp3lame", "-b:a", "128k", "-c:v", "copy",
                    "-disposition:v:0", "attached_pic", "-id3v2_version", "3",
                    "-metadata", "title=Clementine Test", "-metadata", "artist=Tester");
            case Fixture.ShortGopClip:
                return await MediaFixtures.VideoAsync(Directory, "gop.mp4", 320, 180, seconds: 2, fps: 30, audio: true,
                    "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-g", "15", "-keyint_min", "15", "-c:a", "aac");
            case Fixture.Gif:
                return await Ffmpeg("anim.gif", "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=10:duration=1");
            case Fixture.MkvClip:
                return await MediaFixtures.VideoAsync(Directory, "clip.mkv", 320, 180, seconds: 1);
            case Fixture.Subtitles:
                var srt = System.IO.Path.Combine(Directory, "subs.srt");
                await File.WriteAllTextAsync(srt, "1\r\n00:00:00,000 --> 00:00:00,800\r\nHello ünïcødé\r\n\r\n2\r\n00:00:00,800 --> 00:00:01,000\r\nBye\r\n\r\n");
                return srt;
            case Fixture.SubtitledClip:
                var subs = await GetAsync(Fixture.Subtitles);
                var clip = await GetAsync(Fixture.Clip);
                return await Ffmpeg("subtitled.mp4", "-i", clip, "-i", subs, "-map", "0", "-map", "1", "-c", "copy", "-c:s", "mov_text");
            case Fixture.AudioOnlyMp4:
                return await Ffmpeg("audio-only.mp4", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=1", "-c:a", "aac");
            default:
                throw new ArgumentOutOfRangeException(nameof(fixture));
        }
    }

    /// <summary>Runs ffmpeg with the given arguments writing <paramref name="name"/> in the fixture folder.</summary>
    public static async Task<string> Ffmpeg(string name, params string[] args)
    {
        var output = System.IO.Path.Combine(Directory, name);
        return await FfmpegTo(output, args);
    }

    public static async Task<string> FfmpegTo(string output, params string[] args)
    {
        var result = await ProcessRunner.RunAsync(MediaFixtures.Ffmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", .. args, output]);
        if (!result.Succeeded || !File.Exists(output))
            throw new InvalidOperationException($"Fixture generation failed for {output}: {result.StdErrTail}");
        return output;
    }
}

/// <summary>Pixel-level inspection of encoded files.</summary>
public static class Frames
{
    /// <summary>Decodes one frame to straight RGBA. VP8/VP9 need libvpx to see alpha.</summary>
    public static async Task<RgbaFrame> DecodeAsync(string path, double atSeconds = 0, bool libvpx = false)
    {
        var probe = await MediaFixtures.ProbeAsync(path);
        var video = probe.Video ?? throw new InvalidOperationException("no video");
        var raw = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ClementineTests", $"frame-{Guid.NewGuid():N}.rgba");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(raw)!);
        try
        {
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y" };
            if (libvpx)
                args.AddRange(["-c:v", "libvpx-vp9"]);
            args.AddRange(["-ss", atSeconds.ToString(CultureInfo.InvariantCulture), "-i", path, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgba", raw]);
            var result = await ProcessRunner.RunAsync(MediaFixtures.Ffmpeg, args);
            if (!result.Succeeded)
                throw new InvalidOperationException(result.StdErrTail);
            var bytes = await File.ReadAllBytesAsync(raw);
            var width = video.Width;
            var height = bytes.Length / 4 / Math.Max(1, width);
            return new RgbaFrame(width, height, bytes);
        }
        finally
        {
            File.Delete(raw);
        }
    }

    public static void AssertColor((byte R, byte G, byte B, byte A) actual, int r, int g, int b, int tolerance, string because)
    {
        Assert.True(
            Math.Abs(actual.R - r) <= tolerance && Math.Abs(actual.G - g) <= tolerance && Math.Abs(actual.B - b) <= tolerance,
            $"{because}: expected ≈({r},{g},{b}) ±{tolerance}, got ({actual.R},{actual.G},{actual.B},{actual.A})");
    }
}
