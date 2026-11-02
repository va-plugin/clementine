using Clementine.Core;
using Clementine.Engines.Media.Encoders;
using Clementine.Engines.Media.FFmpeg;

namespace Clementine.Engines.Media.Audio;

/// <summary>What a simple audio tool decided for one input.</summary>
public sealed record AudioEditPlan(AudioEdit Edit, string Suffix, IReadOnlyList<string>? Warnings = null, AudioTarget? Target = null);

/// <summary>
/// An audio tool that is "same format out, with these filters" (trim, volume, channels, speed, fade, bleep, reverse).
/// Tags and cover art are kept where the format allows.
/// </summary>
public sealed class AudioEditOperation : MediaOperation
{
    readonly Func<MediaInfo, OptionValues, AudioEditPlan> _plan;
    readonly string _status;
    readonly bool _endDefaultsToDuration;

    public AudioEditOperation(
        OperationInfo info,
        MediaToolkit kit,
        string status,
        Func<MediaInfo, OptionValues, AudioEditPlan> plan,
        OptionSchema? options = null,
        bool endDefaultsToDuration = false)
        : base(info, kit)
    {
        _plan = plan;
        _status = status;
        _endDefaultsToDuration = endDefaultsToDuration;
        Options = options;
    }

    public override OptionSchema? Options { get; }

    public override bool Accepts(InputFile file) => MediaInputs.IsAudio(file);

    public override async Task<OptionValues> GetDynamicDefaultsAsync(IReadOnlyList<InputFile> inputs, CancellationToken cancellationToken)
    {
        var values = new OptionValues();
        if (_endDefaultsToDuration && inputs.Count == 1)
        {
            try
            {
                var info = await Kit.Probe.ProbeAsync(inputs[0].Path, cancellationToken).ConfigureAwait(false);
                if (info.Duration is { } duration)
                    values["end"] = duration;
            }
            catch (OperationException)
            {
            }
        }
        return values;
    }

    public override async Task<OperationResult> RunAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(JobProgress.Indeterminate("Reading the audio…"));
        var source = await Kit.Probe.ProbeAudioAsync(request.Input.Path, cancellationToken).ConfigureAwait(false);
        var plan = _plan(source, request.Options);
        var target = plan.Target ?? AudioTarget.MatchSource(source, request.Input.Extension);
        return await EncodeAudioToFileAsync(Kit, request, source, target, plan.Edit, plan.Suffix, progress, _status, cancellationToken, plan.Warnings)
            .ConfigureAwait(false);
    }

    /// <summary>Encodes the request's input with <paramref name="edit"/> into one new file next to it.</summary>
    internal static async Task<OperationResult> EncodeAudioToFileAsync(
        MediaToolkit kit,
        OperationRequest request,
        MediaInfo source,
        AudioTarget target,
        AudioEdit edit,
        string? suffix,
        IProgress<JobProgress> progress,
        string status,
        CancellationToken cancellationToken,
        IEnumerable<string>? warnings = null,
        double progressStart = 0,
        double progressSpan = 1)
    {
        using var output = request.Output.BeginFile(request.Input, target.Extension, suffix);
        var plan = AudioEncodePlanner.Build(source, target, edit, output.WorkingPath);
        await kit.RunToFileAsync(plan.Arguments, output.WorkingPath, new FfmpegRunOptions
        {
            ExpectedDuration = plan.ExpectedDuration,
            Progress = progress,
            Status = status,
            ProgressStart = progressStart,
            ProgressSpan = progressSpan,
        }, cancellationToken).ConfigureAwait(false);
        return OperationResult.FromOutputs(output.Commit()) with { Warnings = [.. warnings ?? []] };
    }
}
