using Clementine.Core;
using Clementine.Engines.Media.Encoders;
using Clementine.Engines.Media.FFmpeg;

namespace Clementine.Engines.Media.Video;

/// <summary>Every frame as a numbered PNG ("clip_00001.png" …) in a "name (frames)" folder; alpha is kept.</summary>
public sealed class ExtractFramesOperation : MediaOperation
{
    /// <summary>Beyond this the folder gets unwieldy (and huge); ask the user to trim first.</summary>
    public const int MaxFrames = 20_000;

    public ExtractFramesOperation(OperationInfo info, MediaToolkit kit)
        : base(info, kit)
    {
    }

    public override bool Accepts(InputFile file) => MediaInputs.IsVideo(file);

    public override async Task<OperationResult> RunAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(JobProgress.Indeterminate("Reading the video…"));
        var source = await Kit.Probe.ProbeVideoAsync(request.Input.Path, cancellationToken).ConfigureAwait(false);
        var video = source.Video!;
        var estimated = video.FrameCount ?? (long)Math.Round(source.DurationOrZero.TotalSeconds * video.Fps);
        if (estimated > MaxFrames)
        {
            throw new OperationException(
                $"That's about {estimated:N0} frames — too many for a PNG sequence. Trim the clip first (the limit is {MaxFrames:N0}).");
        }

        using var output = request.Output.BeginDirectory(request.Input, "frames");
        var filters = VideoEncodePlanner.PreFilters(video).Append(VideoEncodePlanner.ToRgb(video, null, keepAlpha: true));
        var pattern = OutputFiles.NumberedPattern(request.Input.BaseName, "_%05d.png");
        var final = await RunToOutputAsync(output,
        [
            .. VideoEncodePlanner.DecoderArguments(video), "-i", source.Path, "-map", $"0:{video.Index}",
            "-vf", string.Join(',', filters), "-fps_mode", "passthrough", "-start_number", "1", pattern,
        ], new FfmpegRunOptions
        {
            ExpectedDuration = source.Duration,
            Progress = progress,
            Status = "Saving frames…",
            WorkingDirectory = output.WorkingPath,
        }, cancellationToken).ConfigureAwait(false);
        return OperationResult.FromOutputs(final);
    }
}
