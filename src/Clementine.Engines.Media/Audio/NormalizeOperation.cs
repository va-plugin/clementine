using System.Globalization;
using Clementine.Core;
using Clementine.Engines.Media.Encoders;
using Clementine.Engines.Media.FFmpeg;

namespace Clementine.Engines.Media.Audio;

/// <summary>Loudness targets.</summary>
public enum NormalizeTarget
{
    /// <summary>−14 LUFS (Spotify, YouTube, Apple Music).</summary>
    Streaming,

    /// <summary>−16 LUFS (podcasts).</summary>
    Podcast,

    /// <summary>−23 LUFS (EBU R128 broadcast).</summary>
    Broadcast,

    /// <summary>Loudest peak just under 0 dBFS.</summary>
    Peak,
}

/// <summary>Two-pass loudness normalisation (measure, then correct linearly) or peak normalisation.</summary>
public sealed class NormalizeOperation : MediaOperation
{
    readonly NormalizeTarget _target;

    public NormalizeOperation(OperationInfo info, MediaToolkit kit, NormalizeTarget target)
        : base(info, kit) => _target = target;

    public override bool Accepts(InputFile file) => MediaInputs.IsAudio(file);

    public override async Task<OperationResult> RunAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(JobProgress.Indeterminate("Reading the audio…"));
        var source = await Kit.Probe.ProbeAudioAsync(request.Input.Path, cancellationToken).ConfigureAwait(false);
        var audio = source.Audio!;
        var target = AudioTarget.MatchSource(source, request.Input.Extension);
        var measureOptions = new FfmpegRunOptions
        {
            ExpectedDuration = source.Duration,
            Progress = progress,
            Status = "Measuring loudness…",
            ProgressSpan = 0.5,
            LogLevel = "info",
        };

        AudioEdit edit;
        if (_target == NormalizeTarget.Peak)
        {
            var log = await Kit.Ffmpeg.RunAsync(["-i", source.Path, "-map", $"0:{audio.Index}", "-af", "volumedetect", "-f", "null", "NUL"], measureOptions, cancellationToken)
                .ConfigureAwait(false);
            var gain = -0.1 - AudioFilters.ParseMaxVolume(log);
            edit = new AudioEdit { Filters = [$"volume={gain.ToString("0.##", CultureInfo.InvariantCulture)}dB"] };
        }
        else
        {
            var (lufs, truePeak) = _target switch
            {
                NormalizeTarget.Podcast => (-16, -1.5),
                NormalizeTarget.Broadcast => (-23, -2.0),
                _ => (-14, -1.0),
            };
            var settings = $"I={lufs}:TP={truePeak.ToString("0.0", CultureInfo.InvariantCulture)}:LRA=11";
            var log = await Kit.Ffmpeg.RunAsync(["-i", source.Path, "-map", $"0:{audio.Index}", "-af", $"loudnorm={settings}:print_format=json", "-f", "null", "NUL"],
                measureOptions, cancellationToken).ConfigureAwait(false);
            var m = AudioFilters.ParseLoudnorm(log);
            edit = new AudioEdit
            {
                Filters =
                [
                    $"loudnorm={settings}:measured_I={m.IntegratedLufs}:measured_TP={m.TruePeak}:measured_LRA={m.Range}" +
                    $":measured_thresh={m.Threshold}:offset={m.Offset}:linear=true:print_format=none",
                ],
                SampleRate = audio.SampleRate > 0 ? audio.SampleRate : 48000, // loudnorm works (and outputs) at 192 kHz
            };
        }

        return await AudioEditOperation.EncodeAudioToFileAsync(Kit, request, source, target, edit, Suffix(_target), progress, "Normalising…",
            cancellationToken, progressStart: 0.5, progressSpan: 0.5).ConfigureAwait(false);
    }

    /// <summary>The name label per target ("song (-14 LUFS).wav"), so normalising one file several ways never collides.</summary>
    public static string Suffix(NormalizeTarget target) => target switch
    {
        NormalizeTarget.Podcast => "-16 LUFS",
        NormalizeTarget.Broadcast => "-23 LUFS",
        NormalizeTarget.Peak => "peak 0 dB",
        _ => "-14 LUFS",
    };
}
