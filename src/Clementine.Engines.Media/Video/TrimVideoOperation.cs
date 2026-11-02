using Clementine.Core;
using Clementine.Engines.Media.Encoders;
using Clementine.Engines.Media.FFmpeg;

namespace Clementine.Engines.Media.Video;

/// <summary>Cuts a video to [start, end]: instant stream copy by default, frame-exact re-encode when asked.</summary>
public sealed class TrimVideoOperation : MediaOperation
{
    public TrimVideoOperation(OperationInfo info, MediaToolkit kit)
        : base(info, kit)
    {
    }

    public override OptionSchema Options { get; } = new("Trim",
    [
        new TimeField("start", "Start", TimeSpan.Zero),
        new TimeField("end", "End", TimeSpan.Zero) { Help = "Defaults to the end of the clip (0 also means the end)." },
        new BoolField("precise", "Precise (re-encode)", false)
        {
            Help = "Frame-exact cuts. Without it the cut snaps to the nearest keyframe but is instant and lossless.",
        },
    ])
    {
        ConfirmLabel = "Trim",
    };

    public override bool Accepts(InputFile file) => MediaInputs.IsVideo(file);

    public override async Task<OptionValues> GetDynamicDefaultsAsync(IReadOnlyList<InputFile> inputs, CancellationToken cancellationToken)
    {
        var values = new OptionValues();
        if (inputs.Count == 1)
        {
            try
            {
                var info = await Kit.Probe.ProbeAsync(inputs[0].Path, cancellationToken).ConfigureAwait(false);
                if (info.Duration is { } duration)
                    values["end"] = duration;
            }
            catch (OperationException)
            {
                // The run will report the problem properly.
            }
        }
        return values;
    }

    public override async Task<OperationResult> RunAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(JobProgress.Indeterminate("Reading the video…"));
        var source = await Kit.Probe.ProbeVideoAsync(request.Input.Path, cancellationToken).ConfigureAwait(false);
        var (start, length, rangeWarning) = Range(request.Options, source.DurationOrZero);
        var video = source.Video!;
        var warnings = new List<string>();
        if (rangeWarning is not null)
            warnings.Add(rangeWarning);

        if (request.Options.GetBool("precise", false) && !MatchingTargets.IsIntraOnly(video))
        {
            var target = MatchingTargets.For(source, request.Input.Extension);
            return await EncodeToFileAsync(request, source, target, new VideoEdit { Start = start, Duration = length }, "trimmed", progress, "Trimming…", cancellationToken,
                warnings).ConfigureAwait(false);
        }

        var args = new List<string> { "-ss", Seconds(start) };
        if (length is { } limit)
            args.AddRange(["-t", Seconds(limit)]);
        args.AddRange(["-i", source.Path, "-map", $"0:{video.Index}"]);
        foreach (var audio in source.AudioStreams)
            args.AddRange(["-map", $"0:{audio.Index}"]);
        foreach (var subtitle in source.SubtitleStreams)
            args.AddRange(["-map", $"0:{subtitle.Index}"]);
        args.AddRange(["-c", "copy", "-avoid_negative_ts", "make_zero", "-map_chapters", "-1"]);

        using var output = request.Output.BeginFile(request.Input, request.Input.Extension, "trimmed");
        args.Add(output.WorkingPath);
        var final = await RunToOutputAsync(output, args, new FfmpegRunOptions
        {
            ExpectedDuration = length ?? (source.Duration is { } total && total > start ? total - start : null),
            Progress = progress,
            Status = "Trimming…",
        }, cancellationToken).ConfigureAwait(false);

        if (start > TimeSpan.Zero && !MatchingTargets.IsIntraOnly(video))
            warnings.Add("Cut at the nearest keyframe (lossless). Turn on Precise for a frame-exact start.");
        return OperationResult.FromOutputs(final) with { Warnings = warnings };
    }

    /// <summary>
    /// Validates and clamps the requested range against the file length (<paramref name="duration"/> zero = unknown).
    /// End 0 — what the dialog shows when the length couldn't be read — means "to the end", and so does an end at or
    /// before the start (with a warning): a trim never comes out empty. Length null = keep everything after the start.
    /// </summary>
    public static (TimeSpan Start, TimeSpan? Length, string? Warning) Range(OptionValues options, TimeSpan duration)
    {
        var start = options.GetTime("start", TimeSpan.Zero);
        if (start < TimeSpan.Zero)
            start = TimeSpan.Zero;
        var end = options.GetTime("end", TimeSpan.Zero);
        if (duration > TimeSpan.Zero && start >= duration)
            throw new OperationException($"The start ({OptionValues.FormatTime(start)}) is past the end of the clip ({OptionValues.FormatTime(duration)}).");

        string? warning = null;
        if (end > TimeSpan.Zero && end <= start)
        {
            warning = $"The end ({OptionValues.FormatTime(end)}) wasn't after the start, so everything from {OptionValues.FormatTime(start)} to the end was kept.";
            end = TimeSpan.Zero;
        }
        if (duration > TimeSpan.Zero && (end <= TimeSpan.Zero || end > duration))
            end = duration;
        return (start, end > TimeSpan.Zero ? end - start : null, warning);
    }

    static string Seconds(TimeSpan time) => time.TotalSeconds.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
}
