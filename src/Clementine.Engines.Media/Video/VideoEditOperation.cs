using Clementine.Core;
using Clementine.Engines.Media.Encoders;
using Clementine.Engines.Media.FFmpeg;

namespace Clementine.Engines.Media.Video;

/// <summary>What a simple video tool decided for one input.</summary>
/// <param name="Edit">Filters, size and timing changes.</param>
/// <param name="Suffix">Output name suffix ("720p", "2x", "rotated").</param>
/// <param name="Warnings">Non-fatal notes for the toast.</param>
public sealed record VideoEditPlan(VideoEdit Edit, string Suffix, IReadOnlyList<string>? Warnings = null);

/// <summary>
/// A video tool that is "re-encode with the source's own codec, plus these filters" (resize, rotate, crop, speed,
/// frame rate…). The planning delegate is pure, so each tool is a few lines.
/// </summary>
public sealed class VideoEditOperation : MediaOperation
{
    readonly Func<MediaInfo, OptionValues, VideoEditPlan> _plan;
    readonly string _status;

    public VideoEditOperation(OperationInfo info, MediaToolkit kit, string status, Func<MediaInfo, OptionValues, VideoEditPlan> plan, OptionSchema? options = null)
        : base(info, kit)
    {
        _plan = plan;
        _status = status;
        Options = options;
    }

    public override OptionSchema? Options { get; }

    public override bool Accepts(InputFile file) => MediaInputs.IsVideo(file);

    public override async Task<OperationResult> RunAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(JobProgress.Indeterminate("Reading the video…"));
        var source = await Kit.Probe.ProbeVideoAsync(request.Input.Path, cancellationToken).ConfigureAwait(false);
        var plan = _plan(source, request.Options);
        var target = MatchingTargets.For(source, request.Input.Extension);
        return await EncodeToFileAsync(request, source, target, plan.Edit, plan.Suffix, progress, _status, cancellationToken, plan.Warnings)
            .ConfigureAwait(false);
    }
}
