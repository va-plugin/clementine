using Clementine.Core;
using Clementine.Engines.Media.FFmpeg;

namespace Clementine.Engines.Media.Video;

/// <summary>Containers <see cref="RemuxOperation"/> can rewrap into.</summary>
public enum RemuxContainer
{
    Mov,
    Mkv,
}

/// <summary>
/// Rewraps streams into another container without re-encoding (seconds instead of minutes, no quality loss).
/// MOV keeps the video and every compatible audio track (others become AAC, with a warning) plus text subtitles;
/// MKV copies every stream.
/// </summary>
public sealed class RemuxOperation : MediaOperation
{
    static readonly HashSet<string> MovVideoCodecs = new(StringComparer.Ordinal)
    {
        "h264", "hevc", "prores", "mpeg4", "mjpeg", "dnxhd", "hap", "dxv", "qtrle", "png", "rawvideo", "dvvideo",
        "mpeg2video", "mpeg1video", "cfhd", "av1", "h263", "svq3",
    };

    static readonly HashSet<string> MovAudioCodecs = new(StringComparer.Ordinal) { "aac", "mp3", "alac", "ac3", "eac3", "mp2" };

    readonly RemuxContainer _container;

    public RemuxOperation(OperationInfo info, MediaToolkit kit, RemuxContainer container)
        : base(info, kit) => _container = container;

    string Extension => _container == RemuxContainer.Mov ? ".mov" : ".mkv";

    public override bool Accepts(InputFile file) =>
        MediaInputs.IsVideo(file) && MediaInputs.Family(file.Extension) != MediaInputs.Family(Extension);

    public override async Task<OperationResult> RunAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(JobProgress.Indeterminate("Reading the video…"));
        var source = await Kit.Probe.ProbeVideoAsync(request.Input.Path, cancellationToken).ConfigureAwait(false);
        var warnings = new List<string>();
        var arguments = _container == RemuxContainer.Mov ? MovArguments(source, warnings) : MkvArguments(source);

        using var output = request.Output.BeginFile(request.Input, Extension);
        arguments.Add(output.WorkingPath);
        var final = await RunToOutputAsync(output, arguments, new FfmpegRunOptions
        {
            ExpectedDuration = source.Duration,
            Progress = progress,
            Status = "Rewrapping…",
            FailureMessage = $"FFmpeg couldn't rewrap this file as {Extension.TrimStart('.').ToUpperInvariant()}. Try a conversion that re-encodes instead.",
        }, cancellationToken).ConfigureAwait(false);
        return OperationResult.FromOutputs(final) with { Warnings = warnings };
    }

    static List<string> MovArguments(MediaInfo source, List<string> warnings)
    {
        var video = source.Video!;
        if (!MovVideoCodecs.Contains(video.Codec))
        {
            throw new OperationException(
                $"This video's codec ({CodecName(video.Codec)}) can't go into a MOV without re-encoding. Use MOV › H.264 instead.");
        }

        var args = new List<string> { "-i", source.Path, "-map", $"0:{video.Index}" };
        foreach (var audio in source.AudioStreams)
            args.AddRange(["-map", $"0:{audio.Index}"]);
        var textSubtitles = source.SubtitleStreams.Where(s => s.IsText).ToList();
        foreach (var subtitle in textSubtitles)
            args.AddRange(["-map", $"0:{subtitle.Index}"]);
        if (source.SubtitleStreams.Count > textSubtitles.Count)
            warnings.Add("Picture-based subtitles were left out — MOV can only hold text subtitles.");

        args.AddRange(["-c:v", "copy"]);
        if (video.Codec == "hevc")
            args.AddRange(["-tag:v", "hvc1"]);
        for (var i = 0; i < source.AudioStreams.Count; i++)
        {
            var audio = source.AudioStreams[i];
            if (MovAudioCodecs.Contains(audio.Codec) || audio.IsPcm)
            {
                args.AddRange([$"-c:a:{i}", "copy"]);
            }
            else
            {
                args.AddRange([$"-c:a:{i}", "aac", $"-b:a:{i}", "256k"]);
                warnings.Add($"Audio track {i + 1} ({CodecName(audio.Codec)}) was converted to AAC — MOV can't hold it as-is.");
            }
        }
        if (textSubtitles.Count > 0)
            args.AddRange(["-c:s", "mov_text"]);
        args.AddRange(["-movflags", "+faststart"]);
        return args;
    }

    static List<string> MkvArguments(MediaInfo source)
    {
        var args = new List<string> { "-i", source.Path, "-map", "0", "-map", "-0:d?" };
        foreach (var picture in source.AttachedPictures)
            args.AddRange(["-map", $"-0:{picture.Index}"]);
        args.AddRange(["-c", "copy"]);
        for (var i = 0; i < source.SubtitleStreams.Count; i++)
        {
            if (source.SubtitleStreams[i].Codec == "mov_text")
                args.AddRange([$"-c:s:{i}", "srt"]);
        }
        return args;
    }

    static string CodecName(string codec) => codec switch
    {
        "vp9" => "VP9",
        "vp8" => "VP8",
        "wmv3" or "wmv2" or "wmv1" => "Windows Media",
        "vc1" => "VC-1",
        "theora" => "Theora",
        "flv1" => "Flash video",
        "opus" => "Opus",
        "vorbis" => "Vorbis",
        "wmav2" or "wmapro" => "Windows Media audio",
        "flac" => "FLAC",
        "dts" => "DTS",
        "truehd" => "TrueHD",
        _ => codec.ToUpperInvariant(),
    };
}
