using Clementine.Core;
using Clementine.Engines.Media.FFmpeg;

namespace Clementine.Engines.Media.Video;

/// <summary>Copies the video (and subtitles) without the sound — instant and lossless.</summary>
public sealed class RemoveAudioOperation : MediaOperation
{
    public RemoveAudioOperation(OperationInfo info, MediaToolkit kit)
        : base(info, kit)
    {
    }

    public override bool Accepts(InputFile file) => MediaInputs.IsVideo(file);

    public override async Task<OperationResult> RunAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(JobProgress.Indeterminate("Reading the video…"));
        var source = await Kit.Probe.ProbeVideoAsync(request.Input.Path, cancellationToken).ConfigureAwait(false);
        if (!source.HasAudio)
            throw new OperationException("This video has no audio to remove.");

        using var output = request.Output.BeginFile(request.Input, request.Input.Extension, "no audio");
        var args = new List<string> { "-i", source.Path };
        foreach (var video in source.VideoStreams)
            args.AddRange(["-map", $"0:{video.Index}"]);
        foreach (var subtitle in source.SubtitleStreams)
            args.AddRange(["-map", $"0:{subtitle.Index}"]);
        args.AddRange(["-c", "copy", "-an", output.WorkingPath]);
        var final = await RunToOutputAsync(output, args, new FfmpegRunOptions { ExpectedDuration = source.Duration, Progress = progress, Status = "Removing audio…" },
            cancellationToken).ConfigureAwait(false);
        return OperationResult.FromOutputs(final);
    }
}

/// <summary>Saves the first text subtitle track as an .srt file.</summary>
public sealed class ExtractSubtitlesOperation : MediaOperation
{
    public ExtractSubtitlesOperation(OperationInfo info, MediaToolkit kit)
        : base(info, kit)
    {
    }

    public override bool Accepts(InputFile file) => MediaInputs.IsVideo(file);

    public override async Task<OperationResult> RunAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(JobProgress.Indeterminate("Reading the video…"));
        var source = await Kit.Probe.ProbeAsync(request.Input.Path, cancellationToken).ConfigureAwait(false);
        if (source.SubtitleStreams.Count == 0)
            throw new OperationException("This video has no subtitle track.");
        var subtitle = source.SubtitleStreams.FirstOrDefault(s => s.IsText)
            ?? throw new OperationException("This video's subtitles are pictures (DVD/Blu-ray style), so they can't be saved as text.");

        using var output = request.Output.BeginFile(request.Input, ".srt");
        var final = await RunToOutputAsync(output, ["-i", source.Path, "-map", $"0:{subtitle.Index}", "-c:s", "srt", output.WorkingPath],
            new FfmpegRunOptions { Progress = progress, Status = "Extracting subtitles…" }, cancellationToken).ConfigureAwait(false);

        var warnings = source.SubtitleStreams.Count > 1
            ? new[] { $"This video has {source.SubtitleStreams.Count} subtitle tracks; the first text track was saved." }
            : [];
        return OperationResult.FromOutputs(final) with { Warnings = warnings };
    }
}

/// <summary>Strips tags, chapters and embedded cover art without re-encoding (video or audio).</summary>
public sealed class RemoveMetadataOperation : MediaOperation
{
    readonly bool _audio;

    public RemoveMetadataOperation(OperationInfo info, MediaToolkit kit, bool audio)
        : base(info, kit) => _audio = audio;

    public override bool Accepts(InputFile file) => _audio ? MediaInputs.IsAudio(file) : MediaInputs.IsVideo(file);

    public override async Task<OperationResult> RunAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(JobProgress.Indeterminate("Reading the file…"));
        var source = await Kit.Probe.ProbeAsync(request.Input.Path, cancellationToken).ConfigureAwait(false);
        if (!_audio && !source.HasVideo)
            throw new OperationException("This file has no video stream.");
        if (_audio && !source.HasAudio)
            throw new OperationException("This file has no audio.");

        var args = new List<string> { "-i", source.Path };
        foreach (var stream in source.VideoStreams)
            args.AddRange(["-map", $"0:{stream.Index}"]);
        foreach (var stream in source.AudioStreams)
            args.AddRange(["-map", $"0:{stream.Index}"]);
        foreach (var stream in source.SubtitleStreams)
            args.AddRange(["-map", $"0:{stream.Index}"]);
        args.AddRange([
            "-c", "copy", "-map_metadata", "-1", "-map_chapters", "-1",
            "-fflags", "+bitexact", "-flags:v", "+bitexact", "-flags:a", "+bitexact",
        ]);
        if (request.Input.Extension == ".mp3")
            args.AddRange(["-id3v2_version", "0"]);

        using var output = request.Output.BeginFile(request.Input, request.Input.Extension, "no metadata");
        args.Add(output.WorkingPath);
        var final = await RunToOutputAsync(output, args, new FfmpegRunOptions { ExpectedDuration = source.Duration, Progress = progress, Status = "Removing metadata…" },
            cancellationToken).ConfigureAwait(false);
        return OperationResult.FromOutputs(final);
    }
}
