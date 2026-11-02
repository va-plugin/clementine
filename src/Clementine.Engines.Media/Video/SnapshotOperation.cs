using System.Globalization;
using Clementine.Core;
using Clementine.Engines.Media.Encoders;
using Clementine.Engines.Media.FFmpeg;

namespace Clementine.Engines.Media.Video;

/// <summary>Which frame a snapshot takes.</summary>
public enum SnapshotPosition
{
    First,
    Middle,
    AtTime,
}

/// <summary>Saves one frame as a full-size PNG (alpha kept), colour-converted with the source's matrix.</summary>
public sealed class SnapshotOperation : MediaOperation
{
    readonly SnapshotPosition _position;

    public SnapshotOperation(OperationInfo info, MediaToolkit kit, SnapshotPosition position)
        : base(info, kit) => _position = position;

    public override OptionSchema? Options => _position != SnapshotPosition.AtTime ? null : new OptionSchema("Snapshot",
        [new TimeField("time", "Time", TimeSpan.Zero)])
    {
        ConfirmLabel = "Save frame",
    };

    public override bool Accepts(InputFile file) => MediaInputs.IsVideo(file);

    public override async Task<OptionValues> GetDynamicDefaultsAsync(IReadOnlyList<InputFile> inputs, CancellationToken cancellationToken)
    {
        var values = new OptionValues();
        if (_position == SnapshotPosition.AtTime && inputs.Count == 1)
        {
            try
            {
                var info = await Kit.Probe.ProbeAsync(inputs[0].Path, cancellationToken).ConfigureAwait(false);
                if (info.Duration is { } duration)
                    values["time"] = TimeSpan.FromMilliseconds(Math.Floor(duration.TotalMilliseconds / 2));
            }
            catch (OperationException)
            {
            }
        }
        return values;
    }

    public override async Task<OperationResult> RunAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(JobProgress.Indeterminate("Reading the video…"));
        var source = await Kit.Probe.ProbeVideoAsync(request.Input.Path, cancellationToken).ConfigureAwait(false);
        var video = source.Video!;
        var duration = source.DurationOrZero;
        var lastFrame = duration - TimeSpan.FromSeconds(1 / video.Fps);
        var time = _position switch
        {
            SnapshotPosition.First => TimeSpan.Zero,
            SnapshotPosition.Middle => duration / 2,
            _ => request.Options.GetTime("time", TimeSpan.Zero),
        };
        if (duration > TimeSpan.Zero && time > lastFrame)
            time = lastFrame > TimeSpan.Zero ? lastFrame : TimeSpan.Zero;

        var suffix = _position switch
        {
            SnapshotPosition.First => "first frame",
            SnapshotPosition.Middle => "middle frame",
            _ => "frame " + OptionValues.FormatTime(time).Replace(':', '.'),
        };
        using var output = request.Output.BeginFile(request.Input, ".png", suffix);
        var filters = VideoEncodePlanner.PreFilters(video).Append(VideoEncodePlanner.ToRgb(video, null, keepAlpha: true));
        List<string> args =
        [
            .. VideoEncodePlanner.DecoderArguments(video),
            "-ss", time.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture), "-i", source.Path,
            "-map", $"0:{video.Index}", "-frames:v", "1", "-vf", string.Join(',', filters), "-update", "1", output.WorkingPath,
        ];
        var final = await RunToOutputAsync(output, args, new FfmpegRunOptions { Progress = progress, Status = "Saving the frame…" }, cancellationToken)
            .ConfigureAwait(false);
        return OperationResult.FromOutputs(final);
    }
}
