using Clementine.Core;
using Clementine.Engines.Media.Encoders;
using Clementine.Engines.Media.FFmpeg;

namespace Clementine.Engines.Media.Audio;

/// <summary>Where an <see cref="AudioConvertOperation"/> takes its audio from.</summary>
public enum AudioSourceKind
{
    /// <summary>Audio files: a format conversion (tags and cover art kept).</summary>
    AudioFile,

    /// <summary>Videos: extract the first sound track (stream-copied when it already fits).</summary>
    VideoSoundTrack,
}

/// <summary>Converts the first audio stream of a file to one <see cref="AudioTarget"/>.</summary>
public sealed class AudioConvertOperation : MediaOperation
{
    readonly AudioSourceKind _sourceKind;
    readonly Func<AudioStreamInfo, AudioTarget>? _targetFor;

    /// <param name="targetFor">Optional per-source refinement (WAV extraction keeps 24-bit sources 24-bit).</param>
    /// <param name="nameLabel">
    /// Always added to the output name ("song (128 kbps).mp3") — for the non-default variants of a format, so two
    /// conversions of one file never collide as "song (2).mp3".
    /// </param>
    public AudioConvertOperation(OperationInfo info, MediaToolkit kit, AudioTarget target, AudioSourceKind sourceKind, Func<AudioStreamInfo, AudioTarget>? targetFor = null, string? nameLabel = null)
        : base(info, kit)
    {
        Target = target;
        _sourceKind = sourceKind;
        _targetFor = targetFor;
        NameLabel = nameLabel;
    }

    public AudioTarget Target { get; }

    public string? NameLabel { get; }

    public override IReadOnlyList<string> RequiredEncoders => EncoderRequirements.For(Target);

    public override bool Accepts(InputFile file) => _sourceKind == AudioSourceKind.VideoSoundTrack
        ? MediaInputs.IsVideo(file)
        : MediaInputs.IsAudio(file) && MediaInputs.Family(file.Extension) != MediaInputs.Family(Target.Extension);

    public override async Task<OperationResult> RunAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(JobProgress.Indeterminate("Reading the file…"));
        var source = await Kit.Probe.ProbeAudioAsync(request.Input.Path, cancellationToken).ConfigureAwait(false);
        var target = _targetFor?.Invoke(source.Audio!) ?? Target;
        var edit = new AudioEdit { AllowCopy = _sourceKind == AudioSourceKind.VideoSoundTrack };

        using var output = request.Output.BeginFile(request.Input, target.Extension,
            NameLabel ?? (MediaInputs.Family(request.Input.Extension) == MediaInputs.Family(target.Extension) ? target.DisplayName : null));
        var plan = AudioEncodePlanner.Build(source, target, edit, output.WorkingPath);
        var status = _sourceKind == AudioSourceKind.VideoSoundTrack ? "Extracting the sound track…" : $"Converting to {target.DisplayName}…";
        var final = await RunToOutputAsync(output, plan.Arguments,
            new FfmpegRunOptions { ExpectedDuration = plan.ExpectedDuration, Progress = progress, Status = status },
            cancellationToken).ConfigureAwait(false);

        var warnings = new List<string>();
        if (source.AudioStreams.Count > 1)
            warnings.Add($"This file has {source.AudioStreams.Count} audio tracks; only the first was converted.");
        if (!target.SupportsCoverArt && source.AttachedPictures.Count > 0 && _sourceKind == AudioSourceKind.AudioFile)
            warnings.Add($"Cover art was left out — {target.Extension.TrimStart('.').ToUpperInvariant()} files can't hold it.");
        return OperationResult.FromOutputs(final) with { Warnings = warnings };
    }
}
