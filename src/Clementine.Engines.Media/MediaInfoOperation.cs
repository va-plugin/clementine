using System.Globalization;
using System.Text;
using Clementine.Core;
using Clementine.Engines.Media.FFmpeg;

namespace Clementine.Engines.Media;

/// <summary>A readable text report of everything ffprobe knows about a video or audio file.</summary>
public sealed class MediaInfoOperation : MediaOperation
{
    readonly bool _audio;

    public MediaInfoOperation(OperationInfo info, MediaToolkit kit, bool audio)
        : base(info, kit) => _audio = audio;

    public override bool Accepts(InputFile file) => _audio ? MediaInputs.IsAudio(file) : MediaInputs.IsVideo(file);

    public override async Task<OperationResult> RunAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(JobProgress.Indeterminate("Reading the file…"));
        var info = await Kit.Probe.ProbeAsync(request.Input.Path, cancellationToken).ConfigureAwait(false);
        return OperationResult.FromText($"Media info · {request.Input.Name}", Report(info, request.Input.Name));
    }

    /// <summary>Formats the report (also used by tests).</summary>
    public static string Report(MediaInfo info, string name)
    {
        var text = new StringBuilder();
        Line(text, "File", name);
        Line(text, "Container", info.FormatLongName is { } longName ? $"{longName} ({info.FormatName})" : info.FormatName);
        Line(text, "Duration", info.Duration is { } d ? OptionValues.FormatTime(d) : "unknown");
        Line(text, "Size", Bytes(info.Size));
        if (info.BitRate is { } rate)
            Line(text, "Bit rate", Kbps(rate));
        if (info.ChapterCount > 0)
            Line(text, "Chapters", info.ChapterCount.ToString(CultureInfo.InvariantCulture));

        foreach (var video in info.VideoStreams)
        {
            text.AppendLine();
            text.AppendLine(CultureInfo.InvariantCulture, $"Video #{video.Index}");
            Line(text, "Codec", Codec(video));
            Line(text, "Resolution", video.HasNonSquarePixels || video.Rotation != 0
                ? $"{video.Width} × {video.Height} (shown as {video.DisplayWidth} × {video.DisplayHeight})"
                : $"{video.Width} × {video.Height}");
            Line(text, "Frame rate", $"{video.Fps:0.###} fps" + (video.IsVariableFrameRate ? " (variable)" : ""));
            Line(text, "Pixel format", $"{video.PixelFormat} · {video.BitDepth}-bit");
            Line(text, "Alpha", video.HasAlpha ? "yes" : "no");
            if (video.Rotation != 0)
                Line(text, "Rotation", $"{video.Rotation}°");
            Line(text, "Scan", !video.IsInterlaced ? "progressive"
                : video.FieldOrder is null or "unknown" ? "interlaced (DV)" : $"interlaced ({video.FieldOrder})");
            Line(text, "Colour", $"matrix {video.ColorSpace ?? "untagged"}, primaries {video.ColorPrimaries ?? "untagged"}, " +
                $"transfer {video.ColorTransfer ?? "untagged"}, range {video.ColorRange ?? "untagged"}");
            if (Encoders.VideoEncodePlanner.IsHdr(video))
                Line(text, "HDR", video.ColorTransfer == "smpte2084" ? "yes (PQ / HDR10)" : "yes (HLG)");
            if (video.FrameCount is { } frames)
                Line(text, "Frames", frames.ToString("N0", CultureInfo.InvariantCulture));
            if (video.BitRate is { } videoRate)
                Line(text, "Bit rate", Kbps(videoRate));
            StreamTags(text, video);
        }

        foreach (var audio in info.AudioStreams)
        {
            text.AppendLine();
            text.AppendLine(CultureInfo.InvariantCulture, $"Audio #{audio.Index}");
            Line(text, "Codec", Codec(audio));
            Line(text, "Sample rate", $"{audio.SampleRate.ToString(CultureInfo.InvariantCulture)} Hz");
            Line(text, "Channels", audio.ChannelLayout is { } layout ? $"{audio.Channels} ({layout})" : audio.Channels.ToString(CultureInfo.InvariantCulture));
            if (audio.BitsPerSample > 0)
                Line(text, "Bit depth", $"{audio.BitsPerSample}-bit");
            if (audio.BitRate is { } audioRate)
                Line(text, "Bit rate", Kbps(audioRate));
            StreamTags(text, audio);
        }

        foreach (var subtitle in info.SubtitleStreams)
        {
            text.AppendLine();
            text.AppendLine(CultureInfo.InvariantCulture, $"Subtitles #{subtitle.Index}");
            Line(text, "Format", $"{subtitle.Codec}{(subtitle.IsText ? " (text)" : " (pictures)")}");
            StreamTags(text, subtitle);
        }

        if (info.AttachedPictures.Count > 0)
        {
            text.AppendLine();
            Line(text, "Cover art", string.Join(", ", info.AttachedPictures.Select(p => $"{p.Codec} {p.Width} × {p.Height}")));
        }

        var tags = info.Tags.Where(t => !IsNoise(t.Key)).ToList();
        if (tags.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Tags");
            foreach (var (key, value) in tags)
                Line(text, key, value);
        }
        return text.ToString().TrimEnd();
    }

    static void StreamTags(StringBuilder text, StreamInfo stream)
    {
        if (stream.Language is { } language)
            Line(text, "Language", language);
        if (stream.Title is { } title)
            Line(text, "Title", title);
        if (stream.IsDefault)
            Line(text, "Default", "yes");
    }

    static string Codec(StreamInfo stream)
    {
        var name = stream.CodecLongName ?? stream.Codec;
        var extras = new[] { stream.Profile, stream.CodecTag is { } tag ? $"'{tag}'" : null }.Where(e => !string.IsNullOrEmpty(e));
        var suffix = string.Join(", ", extras);
        return suffix.Length > 0 ? $"{name} ({suffix})" : name;
    }

    static bool IsNoise(string key) => key is "major_brand" or "minor_version" or "compatible_brands";

    static void Line(StringBuilder text, string label, string value) =>
        text.AppendLine(CultureInfo.InvariantCulture, $"  {label + ":",-14} {value}");

    static string Kbps(long bitsPerSecond) => bitsPerSecond >= 10_000_000
        ? $"{bitsPerSecond / 1_000_000.0:0.#} Mbps"
        : $"{bitsPerSecond / 1000.0:0} kbps";

    static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.##} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} bytes",
    };
}
