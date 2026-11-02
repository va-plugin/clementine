using System.Globalization;
using System.Text;
using Clementine.Core;
using Clementine.Engines.Media.Encoders;
using Clementine.Engines.Media.FFmpeg;
using Clementine.Engines.Media.Video;

namespace Clementine.Engines.Media.Audio;

/// <summary>Makes audio smaller: a 128 kbps MP3, or the MP3 bit rate that fits a target size.</summary>
public sealed class CompressAudioOperation : MediaOperation
{
    static readonly int[] Mp3Rates = [32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
    readonly bool _targetSize;

    public CompressAudioOperation(OperationInfo info, MediaToolkit kit, bool targetSize)
        : base(info, kit) => _targetSize = targetSize;

    public override OptionSchema? Options => !_targetSize ? null : new OptionSchema("Compress to a target size",
        [new NumberField("sizeMb", "Size", 5, 0.1, 10_000) { Unit = "MB", Step = 0.5, Decimals = 1 }])
    {
        ConfirmLabel = "Compress",
    };

    public override IReadOnlyList<string> RequiredEncoders => ["libmp3lame"];

    public override bool Accepts(InputFile file) => MediaInputs.IsAudio(file);

    public override async Task<OperationResult> RunAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(JobProgress.Indeterminate("Reading the audio…"));
        var source = await Kit.Probe.ProbeAudioAsync(request.Input.Path, cancellationToken).ConfigureAwait(false);
        var sizeMb = request.Options.GetDouble("sizeMb", 5);
        var kbps = _targetSize
            ? BitrateFor(sizeMb * 1024 * 1024, source.DurationOrZero)
            : source.Audio!.Codec == "mp3" && source.Audio.BitRate is <= 160_000 ? 96 : 128;
        // "song (compressed).mp3" vs "song (5 MB).mp3", so both presets on one file never collide.
        var suffix = _targetSize ? $"{sizeMb.ToString("0.##", CultureInfo.InvariantCulture)} MB" : "compressed";
        var warnings = new List<string>();
        if (source.Audio!.BitRate is { } original && original > 0 && kbps * 1000 >= original && source.Audio.Codec is "mp3" or "aac" or "opus" or "vorbis")
            warnings.Add("The original is already this small or smaller, so the result won't shrink much.");
        return await AudioEditOperation.EncodeAudioToFileAsync(Kit, request, source, AudioTarget.Mp3(kbps), AudioEdit.None, suffix, progress,
            "Compressing…", cancellationToken, warnings).ConfigureAwait(false);
    }

    /// <summary>The highest standard MP3 bit rate whose output fits <paramref name="targetBytes"/>.</summary>
    public static int BitrateFor(double targetBytes, TimeSpan duration)
    {
        if (duration.TotalSeconds < 0.1)
            throw new OperationException("Couldn't work out how long this file is, so a target size can't be calculated.");
        var kbps = targetBytes * 8 * 0.98 / duration.TotalSeconds / 1000;
        var rate = Mp3Rates.LastOrDefault(r => r <= kbps);
        if (rate == 0)
        {
            var minimumMb = 32 * 1000 / 8.0 * duration.TotalSeconds / 0.98 / 1024 / 1024;
            throw new OperationException($"That target is too small for audio this long — aim for at least {Math.Ceiling(minimumMb * 10) / 10:0.#} MB.");
        }
        return rate;
    }
}

/// <summary>Splits audio into equal parts, fixed-length pieces, or at silences — without re-encoding.</summary>
public sealed class SplitAudioOperation : MediaOperation
{
    public SplitAudioOperation(OperationInfo info, MediaToolkit kit)
        : base(info, kit)
    {
    }

    public override OptionSchema Options { get; } = new("Split",
    [
        new ChoiceField("mode", "Split",
        [
            new OptionChoice("parts", "Into equal parts"), new OptionChoice("seconds", "Every N seconds"), new OptionChoice("silence", "At silences"),
        ], "parts"),
        new NumberField("parts", "Parts", 2, 2, 100) { VisibleWhen = new OptionCondition("mode", "parts") },
        new NumberField("seconds", "Length", 60, 1, 86_400) { Unit = "s", VisibleWhen = new OptionCondition("mode", "seconds") },
        new NumberField("threshold", "Silence below", -35, -80, -10) { Unit = "dB", VisibleWhen = new OptionCondition("mode", "silence") },
        new NumberField("minSilence", "Silence at least", 0.7, 0.1, 10) { Unit = "s", Step = 0.1, Decimals = 1, VisibleWhen = new OptionCondition("mode", "silence") },
    ])
    {
        ConfirmLabel = "Split",
    };

    public override bool Accepts(InputFile file) => MediaInputs.IsAudio(file);

    public override async Task<OperationResult> RunAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(JobProgress.Indeterminate("Reading the audio…"));
        var source = await Kit.Probe.ProbeAudioAsync(request.Input.Path, cancellationToken).ConfigureAwait(false);
        var audio = source.Audio!;
        var options = request.Options;
        IReadOnlyList<TimeSpan> cuts;
        var splitStart = 0.0;
        if (options.GetString("mode", "parts") == "silence")
        {
            var threshold = options.GetDouble("threshold", -35).ToString("0.#", CultureInfo.InvariantCulture);
            var minimum = options.GetDouble("minSilence", 0.7).ToString("0.###", CultureInfo.InvariantCulture);
            var log = await Kit.Ffmpeg.RunAsync(["-i", source.Path, "-map", $"0:{audio.Index}", "-af", $"silencedetect=noise={threshold}dB:d={minimum}", "-f", "null", "NUL"],
                new FfmpegRunOptions { ExpectedDuration = source.Duration, Progress = progress, Status = "Finding silences…", ProgressSpan = 0.5, LogLevel = "info" },
                cancellationToken).ConfigureAwait(false);
            cuts = AudioFilters.SilenceCuts(log, source.DurationOrZero);
            if (cuts.Count == 0)
                throw new OperationException("No silences were found. Try a higher threshold (e.g. −30 dB) or a shorter minimum length.");
            splitStart = 0.5;
        }
        else
        {
            cuts = SplitVideoOperation.CutPoints(options, source.DurationOrZero);
        }

        using var output = request.Output.BeginDirectory(request.Input, "parts");
        var digits = cuts.Count + 1 > 99 ? 3 : 2;
        var pattern = OutputFiles.NumberedPattern(request.Input.BaseName, $" %0{digits}d{request.Input.Extension}");
        var times = string.Join(',', cuts.Select(c => c.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)));
        var final = await RunToOutputAsync(output,
        [
            "-i", source.Path, "-map", $"0:{audio.Index}", "-c", "copy", "-f", "segment", "-segment_times", times,
            "-reset_timestamps", "1", "-segment_start_number", "1", "-map_chapters", "-1", pattern,
        ], new FfmpegRunOptions
        {
            ExpectedDuration = source.Duration,
            Progress = progress,
            Status = "Splitting…",
            ProgressStart = splitStart,
            ProgressSpan = 1 - splitStart,
            WorkingDirectory = output.WorkingPath,
        }, cancellationToken).ConfigureAwait(false);
        return OperationResult.FromOutputs(final);
    }
}

/// <summary>Joins audio files in natural name order (lossless when they share a format, otherwise re-encoded like the first).</summary>
public sealed class JoinAudioOperation : MediaOperation
{
    public JoinAudioOperation(OperationInfo info, MediaToolkit kit)
        : base(info, kit)
    {
    }

    public override InputMode InputMode => InputMode.Combined;

    public override bool Accepts(InputFile file) => MediaInputs.IsAudio(file);

    public override async Task<OperationResult> RunAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(JobProgress.Indeterminate("Reading the files…"));
        var inputs = request.Inputs.OrderBy(i => i.Name, NaturalComparer.Instance).ToList();
        var sources = new List<MediaInfo>();
        foreach (var input in inputs)
            sources.Add(await Kit.Probe.ProbeAudioAsync(input.Path, cancellationToken).ConfigureAwait(false));
        var total = TimeSpan.FromTicks(sources.Sum(s => s.DurationOrZero.Ticks));
        var first = sources[0].Audio!;
        var target = AudioTarget.MatchSource(sources[0], inputs[0].Extension);

        var sameFormat = inputs.Select(i => MediaInputs.Family(i.Extension)).Distinct().Count() == 1
            && sources.All(s => s.Audio!.Codec == first.Codec && s.Audio.SampleRate == first.SampleRate && s.Audio.Channels == first.Channels);

        using var output = request.Output.BeginFile(inputs[0], sameFormat ? inputs[0].Extension : target.Extension, "joined");
        List<string> args;
        if (sameFormat)
        {
            var list = Path.Combine(request.TempDirectory, "join.txt");
            await File.WriteAllTextAsync(list, "ffconcat version 1.0\n" + string.Join('\n', sources.Select(s => OutputFiles.ConcatEntry(s.Path))) + "\n",
                new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            args = ["-f", "concat", "-safe", "0", "-i", list, "-map", "0:a:0", "-c", "copy", output.WorkingPath];
        }
        else
        {
            var layout = first.Channels == 1 ? "mono" : "stereo";
            var rate = first.SampleRate > 0 ? first.SampleRate : 48000;
            args = [];
            var graph = new StringBuilder();
            for (var i = 0; i < sources.Count; i++)
            {
                args.AddRange(["-i", sources[i].Path]);
                graph.Append(CultureInfo.InvariantCulture, $"[{i}:{sources[i].Audio!.Index}]aresample={rate},aformat=sample_rates={rate}:channel_layouts={layout}[j{i}];");
            }
            for (var i = 0; i < sources.Count; i++)
                graph.Append(CultureInfo.InvariantCulture, $"[j{i}]");
            graph.Append(CultureInfo.InvariantCulture, $"concat=n={sources.Count}:v=0:a=1[joined]");
            args.AddRange(["-filter_complex", graph.ToString(), "-map", "[joined]", "-c:a", target.Encoder, .. target.EncoderArguments, "-vn", output.WorkingPath]);
        }

        var final = await RunToOutputAsync(output, args, new FfmpegRunOptions { ExpectedDuration = total, Progress = progress, Status = "Joining…" }, cancellationToken)
            .ConfigureAwait(false);
        return OperationResult.FromOutputs(final) with
        {
            Warnings = sameFormat ? [] : [$"The files differ in format, so they were re-encoded as {target.DisplayName}."],
        };
    }
}

/// <summary>An MP4 of the sound's waveform in Clementine orange — for sharing audio where only video is allowed.</summary>
public sealed class WaveformVideoOperation : MediaOperation
{
    public WaveformVideoOperation(OperationInfo info, MediaToolkit kit)
        : base(info, kit)
    {
    }

    public override IReadOnlyList<string> RequiredEncoders => ["libx264", "aac"];

    public override bool Accepts(InputFile file) => MediaInputs.IsAudio(file);

    public override async Task<OperationResult> RunAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(JobProgress.Indeterminate("Reading the audio…"));
        var source = await Kit.Probe.ProbeAudioAsync(request.Input.Path, cancellationToken).ConfigureAwait(false);
        var audio = source.Audio!;
        using var output = request.Output.BeginFile(request.Input, ".mp4", "waveform");
        var graph =
            "color=c=0x151515:s=1280x720:r=30,format=rgba[bg];" +
            $"[0:{audio.Index}]showwaves=s=1280x720:mode=cline:rate=30:colors=0xFF8A1F:draw=full,format=rgba[wave];" +
            "[bg][wave]overlay=shortest=1:format=rgb,scale=out_color_matrix=bt709:out_range=limited,format=yuv420p[v]";
        var final = await RunToOutputAsync(output,
        [
            "-i", source.Path, "-filter_complex", graph, "-map", "[v]", "-map", $"0:{audio.Index}",
            "-c:v", "libx264", "-preset", "medium", "-crf", "20", "-tune", "animation",
            "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709",
            "-c:a", "aac", "-b:a", "192k", "-shortest", "-movflags", "+faststart", output.WorkingPath,
        ], new FfmpegRunOptions { ExpectedDuration = source.Duration, Progress = progress, Status = "Drawing the waveform…" }, cancellationToken).ConfigureAwait(false);
        return OperationResult.FromOutputs(final);
    }
}
