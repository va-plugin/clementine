using Clementine.Core;
using Clementine.Engines.Media.Encoders;
using Clementine.Engines.Media.FFmpeg;

namespace Clementine.Engines.Media;

/// <summary>
/// Base class for every media operation: FFmpeg availability, the shared <see cref="MediaToolkit"/>, and the common
/// "probe → encode to a pending output → commit" flow.
/// </summary>
public abstract class MediaOperation : OperationBase
{
    protected MediaOperation(OperationInfo info, MediaToolkit kit)
        : base(info) => Kit = kit;

    protected MediaToolkit Kit { get; }

    /// <summary>
    /// FFmpeg encoders this operation always needs. Empty for operations whose encoder depends on the input
    /// (tools that keep the source's codec) or that only copy streams.
    /// </summary>
    public virtual IReadOnlyList<string> RequiredEncoders => [];

    public override Availability CheckAvailability() => Kit.CheckFfmpeg(RequiredEncoders);

    /// <summary>Encodes the request's input to one new file next to it and returns the committed result.</summary>
    protected async Task<OperationResult> EncodeToFileAsync(
        OperationRequest request,
        MediaInfo source,
        VideoTarget target,
        VideoEdit edit,
        string? suffix,
        IProgress<JobProgress> progress,
        string status,
        CancellationToken cancellationToken,
        IEnumerable<string>? extraWarnings = null,
        string? workingDirectory = null)
    {
        using var output = request.Output.BeginFile(request.Input, target.Extension, suffix);
        var plan = await Kit.EncodeVideoAsync(source, target, edit, output.WorkingPath, progress, status, cancellationToken,
            workingDirectory: workingDirectory).ConfigureAwait(false);
        var final = output.Commit();
        return OperationResult.FromOutputs(final) with { Warnings = [.. plan.Warnings, .. extraWarnings ?? []] };
    }

    /// <summary>Runs ffmpeg arguments that end in <c>output.WorkingPath</c> and commits the result.</summary>
    protected async Task<string> RunToOutputAsync(
        PendingOutput output,
        IReadOnlyList<string> arguments,
        FfmpegRunOptions options,
        CancellationToken cancellationToken)
    {
        await Kit.RunToFileAsync(arguments, output.WorkingPath, options, cancellationToken).ConfigureAwait(false);
        return output.Commit();
    }
}
