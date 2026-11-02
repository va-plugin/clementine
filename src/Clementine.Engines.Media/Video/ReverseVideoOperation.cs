using System.Globalization;
using System.Text;
using Clementine.Core;
using Clementine.Engines.Media.Encoders;
using Clementine.Engines.Media.FFmpeg;

namespace Clementine.Engines.Media.Video;

/// <summary>Plays a clip backwards, or forwards-then-backwards as a seamless ping-pong loop.</summary>
public sealed class ReverseVideoOperation : MediaOperation
{
    /// <summary>Bytes of decoded frames FFmpeg's reverse filter may hold at once (it buffers a whole segment in memory).</summary>
    internal static long FrameMemoryBudget { get; set; } = 1_500_000_000;

    readonly bool _pingPong;

    public ReverseVideoOperation(OperationInfo info, MediaToolkit kit, bool pingPong)
        : base(info, kit) => _pingPong = pingPong;

    public override bool Accepts(InputFile file) => MediaInputs.IsVideo(file);

    public override async Task<OperationResult> RunAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(JobProgress.Indeterminate("Reading the video…"));
        var source = await Kit.Probe.ProbeVideoAsync(request.Input.Path, cancellationToken).ConfigureAwait(false);
        var video = source.Video!;
        var duration = source.DurationOrZero;
        if (duration <= TimeSpan.Zero)
            throw new OperationException("Couldn't work out how long this video is, so it can't be reversed.");

        var target = MatchingTargets.For(source, request.Input.Extension);
        var suffix = _pingPong ? "ping-pong" : "reversed";
        var status = _pingPong ? "Making a ping-pong loop…" : "Reversing…";
        var fps = video.Fps;
        var frameCount = video.FrameCount is > 0 ? video.FrameCount.Value : (long)Math.Round(duration.TotalSeconds * fps);
        var segment = SegmentLength(video);

        if (duration <= segment)
        {
            var edit = _pingPong ? PingPongEdit(frameCount, fps, duration) : ReverseEdit();
            return await EncodeToFileAsync(request, source, target, edit, suffix, progress, status, cancellationToken).ConfigureAwait(false);
        }

        // Long clips: reverse in memory-sized segments, then join them losslessly.
        var parts = new List<VideoEdit>();
        var frame = TimeSpan.FromSeconds(1 / fps);
        if (_pingPong)
            parts.Add(new VideoEdit());
        var reverseStart = _pingPong ? frame : TimeSpan.Zero;
        var reverseEnd = _pingPong ? duration - frame : duration;
        for (var end = reverseEnd; end > reverseStart; end -= segment)
        {
            var start = end - segment < reverseStart ? reverseStart : end - segment;
            parts.Add(ReverseEdit() with { Start = start, Duration = end - start });
        }

        using var output = request.Output.BeginFile(request.Input, target.Extension, suffix);
        var files = new List<string>();
        var warnings = new List<string>();
        for (var i = 0; i < parts.Count; i++)
        {
            var path = Path.Combine(request.TempDirectory, $"part{i:0000}{target.Extension}");
            var plan = await Kit.EncodeVideoAsync(source, target, parts[i], path, progress, status, cancellationToken,
                (double)i / (parts.Count + 1), 1.0 / (parts.Count + 1)).ConfigureAwait(false);
            warnings.AddRange(plan.Warnings);
            files.Add(path);
        }

        var list = Path.Combine(request.TempDirectory, "parts.txt");
        await File.WriteAllTextAsync(list, "ffconcat version 1.0\n" + string.Join('\n', files.Select(OutputFiles.ConcatEntry)) + "\n",
            new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        var final = await RunToOutputAsync(output, ["-f", "concat", "-safe", "0", "-i", list, "-map", "0", "-c", "copy", output.WorkingPath],
            new FfmpegRunOptions
            {
                Progress = progress,
                Status = "Joining the pieces…",
                ProgressStart = (double)parts.Count / (parts.Count + 1),
                ProgressSpan = 1.0 / (parts.Count + 1),
            }, cancellationToken).ConfigureAwait(false);
        return OperationResult.FromOutputs(final) with { Warnings = warnings.Distinct().ToList() };
    }

    /// <summary>How many seconds of this video fit in the reverse buffer (2–60 s).</summary>
    public static TimeSpan SegmentLength(VideoStreamInfo video)
    {
        var bytesPerFrame = Math.Max(1L, (long)video.DisplayWidth * video.DisplayHeight * (video.BitDepth > 8 ? 6 : 3));
        var frames = FrameMemoryBudget / bytesPerFrame;
        return TimeSpan.FromSeconds(Math.Clamp(frames / Math.Max(1, video.Fps), 2, 60));
    }

    static VideoEdit ReverseEdit() => new() { VideoFilters = ["reverse"], AudioFilters = ["areverse"] };

    /// <summary>
    /// Forward then backward, without repeating the turnaround frames: 0…N-1 then N-2…1, so the loop point back to
    /// frame 0 is seamless too.
    /// </summary>
    static VideoEdit PingPongEdit(long frameCount, double fps, TimeSpan duration)
    {
        var trimmed = frameCount >= 3;
        var reverseFrames = trimmed ? frameCount - 2 : frameCount;
        string Inv(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
        var videoTrim = trimmed ? $"trim=start_frame=1:end_frame={frameCount - 1},setpts=PTS-STARTPTS," : "";
        var audioTrim = trimmed ? $"atrim=start={Inv(1 / fps)}:end={Inv((frameCount - 1) / fps)},asetpts=PTS-STARTPTS," : "";
        return new VideoEdit
        {
            VideoGraph = input => $"{input}split[pf][pr];[pr]{videoTrim}reverse[prv];[pf][prv]concat=n=2:v=1:a=0[edited]",
            AudioGraph = input => $"{input}asplit[af][ar];[ar]{audioTrim}areverse[arv];[af][arv]concat=n=2:v=0:a=1[edited_a]",
            OutputDuration = duration + TimeSpan.FromSeconds(reverseFrames / fps),
        };
    }
}
