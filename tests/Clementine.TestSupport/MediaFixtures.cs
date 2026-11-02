using System.Globalization;
using System.Text.Json;
using Clementine.Core;

namespace Clementine.TestSupport;

/// <summary>Generates small media files with FFmpeg's built-in test sources, and inspects results with ffprobe.</summary>
public static class MediaFixtures
{
    static readonly ToolLocator Tools = new(() => new ClementineSettings());

    public static string Ffmpeg => Tools.Ffmpeg ?? throw new InvalidOperationException("ffmpeg is required for these tests.");

    public static string Ffprobe => Tools.Ffprobe ?? throw new InvalidOperationException("ffprobe is required for these tests.");

    /// <summary>A colour-bars clip (H.264 + AAC tone for .mp4/.mov/.mkv; container picks codecs otherwise).</summary>
    public static Task<string> VideoAsync(
        string directory, string name = "clip.mp4", int width = 640, int height = 360, double seconds = 2, int fps = 30,
        bool audio = true, params string[] encodeArgs)
    {
        var output = System.IO.Path.Combine(directory, name);
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
            "-f", "lavfi", "-i", $"testsrc2=size={width}x{height}:rate={fps}:duration={Inv(seconds)}",
        };
        if (audio)
            args.AddRange(["-f", "lavfi", "-i", $"sine=frequency=440:sample_rate=48000:duration={Inv(seconds)}"]);
        if (encodeArgs.Length > 0)
        {
            args.AddRange(encodeArgs);
        }
        else if (System.IO.Path.GetExtension(name) is ".mp4" or ".mov" or ".mkv" or ".m4v")
        {
            args.AddRange(["-c:v", "libx264", "-pix_fmt", "yuv420p", "-preset", "ultrafast"]);
            if (audio)
                args.AddRange(["-c:a", "aac", "-b:a", "128k"]);
        }
        if (audio)
            args.Add("-shortest");
        args.Add(output);
        return RunAsync(Ffmpeg, args, output);
    }

    /// <summary>ProRes 4444 clip whose left half is fully transparent (yuva444p10le).</summary>
    public static Task<string> AlphaVideoAsync(string directory, string name = "alpha.mov", int width = 320, int height = 180, double seconds = 1)
    {
        var output = System.IO.Path.Combine(directory, name);
        var half = width / 2;
        return RunAsync(Ffmpeg,
        [
            "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
            "-f", "lavfi", "-i", $"testsrc2=size={width}x{height}:rate=30:duration={Inv(seconds)}",
            "-vf", $"format=rgba,geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':a='if(lt(X,{half}),0,255)'",
            "-c:v", "prores_ks", "-profile:v", "4444", "-pix_fmt", "yuva444p10le",
            output,
        ], output);
    }

    /// <summary>A sine tone. The container/codec follows the extension (.wav, .mp3, .flac, .m4a, .ogg …).</summary>
    public static Task<string> AudioAsync(string directory, string name = "tone.wav", double seconds = 2, int sampleRate = 48000, int channels = 2)
    {
        var output = System.IO.Path.Combine(directory, name);
        var layout = channels == 1 ? "mono" : "stereo";
        return RunAsync(Ffmpeg,
        [
            "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
            "-f", "lavfi", "-i", $"sine=frequency=440:sample_rate={sampleRate}:duration={Inv(seconds)}",
            "-af", $"aformat=channel_layouts={layout}",
            output,
        ], output);
    }

    /// <summary>A single test-pattern still. Format follows the extension (.png, .jpg, .bmp, .tiff, .webp, .gif).</summary>
    public static Task<string> ImageAsync(string directory, string name = "image.png", int width = 320, int height = 240)
    {
        var output = System.IO.Path.Combine(directory, name);
        return RunAsync(Ffmpeg,
        [
            "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
            "-f", "lavfi", "-i", $"testsrc2=size={width}x{height}:rate=1:duration=1",
            "-frames:v", "1", "-update", "1",
            output,
        ], output);
    }

    public static async Task<MediaProbe> ProbeAsync(string path)
    {
        var result = await ProcessRunner.RunAsync(Ffprobe,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path]);
        if (!result.Succeeded)
            throw new InvalidOperationException($"ffprobe failed on {path}: {result.StdErrTail}");

        using var doc = JsonDocument.Parse(result.StdOut);
        var streams = new List<ProbeStream>();
        if (doc.RootElement.TryGetProperty("streams", out var streamArray))
        {
            foreach (var s in streamArray.EnumerateArray())
            {
                streams.Add(new ProbeStream(
                    Str(s, "codec_type") ?? "",
                    Str(s, "codec_name") ?? "",
                    Str(s, "codec_tag_string"),
                    Str(s, "pix_fmt"),
                    Int(s, "width"),
                    Int(s, "height"),
                    Str(s, "profile"),
                    Int(s, "sample_rate"),
                    Int(s, "channels"),
                    Rate(Str(s, "avg_frame_rate"))));
            }
        }
        var format = doc.RootElement.GetProperty("format");
        var duration = double.TryParse(Str(format, "duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
        return new MediaProbe(duration, Str(format, "format_name") ?? "", streams);
    }

    /// <summary>Decodes every frame (catches files that probe fine but don't play).</summary>
    public static async Task<bool> DecodesCleanlyAsync(string path)
    {
        var result = await ProcessRunner.RunAsync(Ffmpeg, ["-hide_banner", "-v", "error", "-nostdin", "-i", path, "-f", "null", "-"]);
        return result.Succeeded && string.IsNullOrWhiteSpace(result.StdErrTail);
    }

    static async Task<string> RunAsync(string exe, IEnumerable<string> args, string output)
    {
        var result = await ProcessRunner.RunAsync(exe, args);
        if (!result.Succeeded || !File.Exists(output))
            throw new InvalidOperationException($"Fixture generation failed for {output}: {result.StdErrTail}");
        return output;
    }

    static string Inv(double value) => value.ToString(CultureInfo.InvariantCulture);

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText() : null;

    static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.Number ? v.TryGetInt32(out var i) : int.TryParse(v.GetString(), out i)) ? i : 0;

    static double Rate(string? fraction)
    {
        if (fraction is null)
            return 0;
        var parts = fraction.Split('/');
        if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var dd) && dd != 0)
            return n / dd;
        return 0;
    }
}

public sealed record MediaProbe(double Duration, string FormatName, IReadOnlyList<ProbeStream> Streams)
{
    public ProbeStream? Video => Streams.FirstOrDefault(s => s.CodecType == "video");

    public ProbeStream? Audio => Streams.FirstOrDefault(s => s.CodecType == "audio");

    public bool HasAudio => Audio is not null;
}

public sealed record ProbeStream(
    string CodecType,
    string CodecName,
    string? CodecTag,
    string? PixelFormat,
    int Width,
    int Height,
    string? Profile,
    int SampleRate,
    int Channels,
    double FrameRate);
