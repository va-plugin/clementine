using Clementine.Core;
using Clementine.Engines.Media.Encoders;

namespace Clementine.Engines.Media.Video;

/// <summary>Re-encodes a video (or animated GIF/APNG) to one <see cref="VideoTarget"/>, saved next to the original.</summary>
public sealed class VideoConvertOperation : MediaOperation
{
    readonly Func<InputFile, bool>? _extraAccepts;

    /// <param name="nameLabel">Short codec label for the file name ("HAP Alpha" → "clip (HAP Alpha).mov").</param>
    /// <param name="extraAccepts">Additional filter on top of "is a video or animation".</param>
    /// <param name="alwaysLabel">
    /// Label the output even when its extension differs from the input's. Used for every target that shares its
    /// extension with other targets (all the .mov VJ codecs, H.265 next to H.264 in MP4, AV1 next to VP9 in WebM…),
    /// so converting one clip several ways gives "clip (DXV).mov", "clip (HAP).mov" instead of "clip (2).mov".
    /// Without it the label is only added when the extension equals the input's ("clip (H.264).mp4").
    /// </param>
    public VideoConvertOperation(OperationInfo info, MediaToolkit kit, VideoTarget target, string nameLabel, Func<InputFile, bool>? extraAccepts = null, bool alwaysLabel = false)
        : base(info, kit)
    {
        Target = target;
        NameLabel = nameLabel;
        AlwaysLabel = alwaysLabel;
        _extraAccepts = extraAccepts;
    }

    public VideoTarget Target { get; }

    public string NameLabel { get; }

    public bool AlwaysLabel { get; }

    public override IReadOnlyList<string> RequiredEncoders => EncoderRequirements.For(Target);

    public override bool Accepts(InputFile file) => MediaInputs.IsVideoOrAnimation(file) && (_extraAccepts?.Invoke(file) ?? true);

    /// <summary>The " (label)" part of the output name for <paramref name="input"/>, or null for a plain "clip.ext".</summary>
    public string? SuffixFor(InputFile input) =>
        AlwaysLabel || MediaInputs.Family(input.Extension) == MediaInputs.Family(Target.Extension) ? NameLabel : null;

    public override async Task<OperationResult> RunAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(JobProgress.Indeterminate("Reading the video…"));
        var source = await Kit.Probe.ProbeVideoAsync(request.Input.Path, cancellationToken).ConfigureAwait(false);
        return await EncodeToFileAsync(request, source, Target, VideoEdit.None, SuffixFor(request.Input), progress, $"Converting to {Target.DisplayName}…", cancellationToken)
            .ConfigureAwait(false);
    }
}
