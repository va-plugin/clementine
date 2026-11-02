using System.Globalization;
using System.Text;
using Clementine.Core;
using Clementine.Engines.Media.Encoders;
using Clementine.Engines.Media.FFmpeg;

namespace Clementine.Engines.Media.Video;

/// <summary>
/// Joins videos end to end in natural name order. Matching clips are concatenated losslessly; mixed ones are
/// normalised to the first clip's size and frame rate (letterboxed, silent audio added where missing) as H.264 MP4.
/// </summary>
public sealed class JoinVideoOperation : MediaOperation
{
    public JoinVideoOperation(OperationInfo info, MediaToolkit kit)
        : base(info, kit)
    {
    }

    public override InputMode InputMode => InputMode.Combined;

    public override bool Accepts(InputFile file) => MediaInputs.IsVideo(file);

    public override async Task<OperationResult> RunAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(JobProgress.Indeterminate("Reading the videos…"));
        var inputs = request.Inputs.OrderBy(i => i.Name, NaturalComparer.Instance).ToList();
        var sources = new List<MediaInfo>();
        foreach (var input in inputs)
            sources.Add(await Kit.Probe.ProbeVideoAsync(input.Path, cancellationToken).ConfigureAwait(false));

        var total = TimeSpan.FromTicks(sources.Sum(s => s.DurationOrZero.Ticks));
        if (CanConcatenate(sources, inputs))
        {
            var list = Path.Combine(request.TempDirectory, "join.txt");
            await File.WriteAllTextAsync(list, "ffconcat version 1.0\n" + string.Join('\n', sources.Select(s => OutputFiles.ConcatEntry(s.Path))) + "\n",
                new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            using var output = request.Output.BeginFile(inputs[0], inputs[0].Extension, "joined");
            var args = new List<string> { "-f", "concat", "-safe", "0", "-i", list, "-map", "0:v:0", "-map", "0:a?", "-c", "copy" };
            if (inputs[0].Extension is ".mp4" or ".mov" or ".m4v")
                args.AddRange(["-movflags", "+faststart"]);
            args.Add(output.WorkingPath);
            var final = await RunToOutputAsync(output, args, new FfmpegRunOptions { ExpectedDuration = total, Progress = progress, Status = "Joining…" }, cancellationToken)
                .ConfigureAwait(false);
            return OperationResult.FromOutputs(final);
        }

        using (var output = request.Output.BeginFile(inputs[0], ".mp4", "joined"))
        {
            var args = ReencodeArguments(sources, output.WorkingPath);
            var final = await RunToOutputAsync(output, args, new FfmpegRunOptions { ExpectedDuration = total, Progress = progress, Status = "Joining (re-encoding)…" }, cancellationToken)
                .ConfigureAwait(false);
            return OperationResult.FromOutputs(final) with
            {
                Warnings = ["The clips differ in format, so they were re-encoded to H.264 at the first clip's size and frame rate."],
            };
        }
    }

    /// <summary>Lossless concatenation needs identical codecs, sizes, pixel formats, rates and audio layouts.</summary>
    public static bool CanConcatenate(IReadOnlyList<MediaInfo> sources, IReadOnlyList<InputFile> inputs)
    {
        if (inputs.Select(i => MediaInputs.Family(i.Extension)).Distinct().Count() != 1)
            return false;
        var first = sources[0];
        var v0 = first.Video!;
        var a0 = first.Audio;
        foreach (var source in sources.Skip(1))
        {
            var v = source.Video!;
            if (v.Codec != v0.Codec || v.Width != v0.Width || v.Height != v0.Height || v.PixelFormat != v0.PixelFormat
                || v.Rotation != v0.Rotation || Math.Abs(v.Fps - v0.Fps) > 0.01 || v.CodecTag != v0.CodecTag)
                return false;
            var a = source.Audio;
            if ((a is null) != (a0 is null))
                return false;
            if (a is not null && a0 is not null && (a.Codec != a0.Codec || a.SampleRate != a0.SampleRate || a.Channels != a0.Channels))
                return false;
        }
        return true;
    }

    static List<string> ReencodeArguments(IReadOnlyList<MediaInfo> sources, string outputPath)
    {
        var first = sources[0].Video!;
        var width = VideoGeometry.Even(first.DisplayWidth);
        var height = VideoGeometry.Even(first.DisplayHeight);
        var fps = first.BestFrameRate.ToString();
        var withAudio = sources.Any(s => s.HasAudio);

        var args = new List<string>();
        foreach (var source in sources)
            args.AddRange(["-i", source.Path]);

        var graph = new StringBuilder();
        var concatInputs = new StringBuilder();
        for (var i = 0; i < sources.Count; i++)
        {
            var video = sources[i].Video!;
            var pre = string.Join(',', VideoEncodePlanner.PreFilters(video).Append(""));
            var matrix = PixelFormats.IsRgb(video.PixelFormat)
                ? ""
                : $":in_color_matrix={ColorSpaces.SourceMatrix(video)}:in_range={ColorSpaces.SourceRange(video)}";
            graph.Append(CultureInfo.InvariantCulture,
                $"[{i}:{video.Index}]{pre}scale={width}:{height}:force_original_aspect_ratio=decrease:force_divisible_by=2{matrix}:out_color_matrix=bt709:out_range=limited," +
                $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps={fps}:eof_action=pass,format=yuv420p[v{i}];");
            concatInputs.Append(CultureInfo.InvariantCulture, $"[v{i}]");
            if (withAudio)
            {
                if (sources[i].Audio is { } audio)
                    graph.Append(CultureInfo.InvariantCulture, $"[{i}:{audio.Index}]aresample=48000,aformat=sample_rates=48000:channel_layouts=stereo[a{i}];");
                else
                    graph.Append(CultureInfo.InvariantCulture,
                        $"anullsrc=r=48000:cl=stereo,atrim=duration={sources[i].DurationOrZero.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)}[a{i}];");
                concatInputs.Append(CultureInfo.InvariantCulture, $"[a{i}]");
            }
        }
        graph.Append(concatInputs).Append(CultureInfo.InvariantCulture, $"concat=n={sources.Count}:v=1:a={(withAudio ? 1 : 0)}[v]");
        if (withAudio)
            graph.Append("[a]");

        args.AddRange(["-filter_complex", graph.ToString(), "-map", "[v]"]);
        if (withAudio)
            args.AddRange(["-map", "[a]", "-c:a", "aac", "-b:a", "192k"]);
        args.AddRange([
            "-c:v", "libx264", "-preset", "medium", "-crf", "20",
            "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709", "-color_range", "tv",
            "-movflags", "+faststart", "-map_chapters", "-1", outputPath,
        ]);
        return args;
    }
}
