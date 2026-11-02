using System.Globalization;
using Clementine.Core;
using Clementine.Engines.Media.Encoders;
using Clementine.Engines.Media.FFmpeg;

namespace Clementine.Engines.Media.Video;

/// <summary>Cuts a video into N equal parts or N-second pieces, saved into a "name (parts)" folder.</summary>
public sealed class SplitVideoOperation : MediaOperation
{
    public SplitVideoOperation(OperationInfo info, MediaToolkit kit)
        : base(info, kit)
    {
    }

    public override OptionSchema Options { get; } = new("Split",
    [
        new ChoiceField("mode", "Split into", [new OptionChoice("parts", "Equal parts"), new OptionChoice("seconds", "Pieces of a set length")], "parts"),
        new NumberField("parts", "Parts", 2, 2, 100) { VisibleWhen = new OptionCondition("mode", "parts") },
        new NumberField("seconds", "Length", 10, 1, 86_400) { Unit = "s", VisibleWhen = new OptionCondition("mode", "seconds") },
        new BoolField("precise", "Precise (re-encode)", false)
        {
            Help = "Exact cut points. Without it each cut moves to the next keyframe but nothing is re-encoded.",
        },
    ])
    {
        ConfirmLabel = "Split",
    };

    public override bool Accepts(InputFile file) => MediaInputs.IsVideo(file);

    public override async Task<OperationResult> RunAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(JobProgress.Indeterminate("Reading the video…"));
        var source = await Kit.Probe.ProbeVideoAsync(request.Input.Path, cancellationToken).ConfigureAwait(false);
        var cuts = CutPoints(request.Options, source.DurationOrZero);
        var precise = request.Options.GetBool("precise", false) && !MatchingTargets.IsIntraOnly(source.Video!);
        var expectedParts = cuts.Count + 1;
        var times = string.Join(',', cuts.Select(c => c.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)));

        using var output = request.Output.BeginDirectory(request.Input, "parts");
        var digits = expectedParts > 99 ? 3 : 2;
        var extension = precise ? MatchingTargets.For(source, request.Input.Extension).Extension : request.Input.Extension;
        var pattern = OutputFiles.NumberedPattern(request.Input.BaseName, $" %0{digits}d{extension}");
        // Half a frame of tolerance, as the segment muxer docs recommend, so forced keyframes land in the right piece.
        var delta = (0.5 / source.Video!.Fps).ToString("0.######", CultureInfo.InvariantCulture);
        var segmentArgs = new List<string>
        {
            "-f", "segment", "-segment_times", times, "-segment_time_delta", delta, "-reset_timestamps", "1",
            "-segment_start_number", "1", "-map_chapters", "-1",
        };
        if (extension is ".mp4" or ".mov" or ".m4v")
            segmentArgs.AddRange(["-segment_format_options", "movflags=+faststart"]);

        var warnings = new List<string>();
        try
        {
            if (precise)
            {
                var target = MatchingTargets.For(source, request.Input.Extension);
                var edit = new VideoEdit
                {
                    OutputKind = EncodeOutputKind.Custom,
                    ExtraOutputArguments = ["-force_key_frames", times, .. ForcedIdr(target), .. segmentArgs],
                };
                var plan = await Kit.EncodeVideoAsync(source, target, edit, pattern, progress, "Splitting…", cancellationToken,
                    workingDirectory: output.WorkingPath).ConfigureAwait(false);
                warnings.AddRange(plan.Warnings);
            }
            else
            {
                var args = new List<string> { "-i", source.Path, "-map", $"0:{source.Video!.Index}" };
                foreach (var audio in source.AudioStreams)
                    args.AddRange(["-map", $"0:{audio.Index}"]);
                args.AddRange(["-c", "copy", .. segmentArgs, pattern]);
                await Kit.Ffmpeg.RunAsync(args, new FfmpegRunOptions
                {
                    ExpectedDuration = source.Duration,
                    Progress = progress,
                    Status = "Splitting…",
                    WorkingDirectory = output.WorkingPath,
                }, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            await OutputFiles.DeleteQuietlyAsync(output.WorkingPath).ConfigureAwait(false);
            throw;
        }

        var produced = Directory.GetFiles(output.WorkingPath).Length;
        if (produced < expectedParts)
            warnings.Add($"Made {produced} parts instead of {expectedParts}: without Precise, cuts can only happen at keyframes.");
        return OperationResult.FromOutputs(output.Commit()) with { Warnings = warnings };
    }

    // x264/x265 turn forced keyframes into plain I-frames unless asked for IDRs, and only IDRs start a clean segment.
    static IEnumerable<string> ForcedIdr(VideoTarget target) =>
        target.Codec is VideoCodec.H264 or VideoCodec.Hevc ? ["-forced-idr", "1"] : [];

    /// <summary>Cut times (excluding 0 and the end) for the chosen mode.</summary>
    public static IReadOnlyList<TimeSpan> CutPoints(OptionValues options, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
            throw new OperationException("Couldn't work out how long this file is, so it can't be split.");
        var cuts = new List<TimeSpan>();
        if (options.GetString("mode", "parts") == "seconds")
        {
            var length = TimeSpan.FromSeconds(Math.Max(0.5, options.GetDouble("seconds", 10)));
            if (length >= duration)
                throw new OperationException($"The clip is only {OptionValues.FormatTime(duration)} long — choose a shorter piece length.");
            for (var t = length; t < duration - TimeSpan.FromMilliseconds(100); t += length)
                cuts.Add(t);
        }
        else
        {
            var parts = Math.Clamp(options.GetInt("parts", 2), 2, 1000);
            for (var i = 1; i < parts; i++)
                cuts.Add(duration * i / parts);
        }
        if (cuts.Count > 999)
            throw new OperationException("That would make more than 1000 pieces. Choose a longer piece length.");
        return cuts;
    }
}
