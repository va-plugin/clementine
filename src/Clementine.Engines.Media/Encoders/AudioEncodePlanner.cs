using System.Globalization;
using Clementine.Core;
using Clementine.Engines.Media.FFmpeg;

namespace Clementine.Engines.Media.Encoders;

/// <summary>What an audio tool changes: a time range, filters, metadata handling.</summary>
public sealed record AudioEdit
{
    public static readonly AudioEdit None = new();

    public TimeSpan? Start { get; init; }

    public TimeSpan? Duration { get; init; }

    /// <summary>Simple filter chain (-af style).</summary>
    public IReadOnlyList<string> Filters { get; init; } = [];

    /// <summary>Custom graph: receives the input label ("[0:1]") and must end with "[edited_a]".</summary>
    public Func<string, string>? Graph { get; init; }

    /// <summary>Speed factor (for the expected duration).</summary>
    public double Speed { get; init; } = 1;

    public TimeSpan? OutputDuration { get; init; }

    /// <summary>Stream-copy when the source codec already fits the target and nothing is filtered.</summary>
    public bool AllowCopy { get; init; }

    /// <summary>Carry embedded cover art over when the target container supports it.</summary>
    public bool KeepCoverArt { get; init; } = true;

    /// <summary>Drop all tags, chapters and cover art.</summary>
    public bool StripMetadata { get; init; }

    /// <summary>Force a sample rate (loudnorm otherwise outputs 192 kHz).</summary>
    public int? SampleRate { get; init; }

    /// <summary>Force a channel count (mono/stereo tools).</summary>
    public int? Channels { get; init; }

    internal bool Filtered => Filters.Count > 0 || Graph is not null;
}

/// <summary>FFmpeg arguments for one audio encode.</summary>
public sealed record AudioEncodePlan(IReadOnlyList<string> Arguments, TimeSpan? ExpectedDuration, bool Copied);

/// <summary>Builds argument lists for audio conversions and tools (first audio stream, tags and cover art kept).</summary>
public static class AudioEncodePlanner
{
    static readonly HashSet<string> CoverCodecs = new(StringComparer.Ordinal) { "mjpeg", "png" };

    public static AudioEncodePlan Build(MediaInfo source, AudioTarget target, AudioEdit edit, string outputPath)
    {
        var audio = source.Audio ?? throw new OperationException("This file has no audio track.");
        var args = new List<string>();
        if (edit.Start is { } start && start > TimeSpan.Zero)
            args.AddRange(["-ss", Seconds(start)]);
        if (edit.Duration is { } length && length > TimeSpan.Zero)
            args.AddRange(["-t", Seconds(length)]);
        args.AddRange(["-i", source.Path]);

        var input = $"[0:{audio.Index}]";
        if (edit.Graph is { } graph)
            args.AddRange(["-filter_complex", graph(input), "-map", "[edited_a]"]);
        else if (edit.Filters.Count > 0)
            args.AddRange(["-filter_complex", $"{input}{string.Join(',', edit.Filters)}[aout]", "-map", "[aout]"]);
        else
            args.AddRange(["-map", $"0:{audio.Index}"]);

        var cover = !edit.StripMetadata && edit.KeepCoverArt && target.SupportsCoverArt
            ? source.AttachedPictures.FirstOrDefault(p => CoverCodecs.Contains(p.Codec))
            : null;
        if (cover is not null)
            args.AddRange(["-map", $"0:{cover.Index}", "-c:v", "copy", "-disposition:v:0", "attached_pic"]);

        var copy = edit.AllowCopy && !edit.Filtered && edit.SampleRate is null && edit.Channels is null
            && target.CopyableCodecs.Contains(audio.Codec);
        if (copy)
        {
            args.AddRange(["-c:a", "copy"]);
        }
        else
        {
            args.AddRange(["-c:a", target.Encoder]);
            args.AddRange(target.EncoderArguments);
            var channels = edit.Channels ?? (target.MaxChannels is { } max && audio.Channels > max ? max : null);
            if (channels is { } count)
                args.AddRange(["-ac", count.ToString(CultureInfo.InvariantCulture)]);
            else if (target.Encoder == "libopus" && !edit.Filtered && OpusSurroundLayout(audio.Channels) is { } layout)
                args.AddRange(["-ch_layout", layout]); // libopus refuses AC3/DTS/WAV "5.1(side)" etc.: remap to its Vorbis order
            if (edit.SampleRate is { } rate)
                args.AddRange(["-ar", rate.ToString(CultureInfo.InvariantCulture)]);
        }
        if (target.Extension is ".m4a" or ".m4b")
            args.AddRange(["-movflags", "+faststart"]);

        if (edit.StripMetadata)
        {
            args.AddRange(["-map_metadata", "-1", "-map_chapters", "-1", "-fflags", "+bitexact", "-flags:a", "+bitexact"]);
        }
        else if (source.IsFormat("ogg") && target.Extension is not (".ogg" or ".opus"))
        {
            args.AddRange(["-map_metadata", "0:s:a:0"]); // Ogg keeps its tags on the stream, other containers globally
        }
        if (!edit.StripMetadata && (edit.Start is not null || edit.Duration is not null || edit.Filtered))
            args.AddRange(["-map_chapters", "-1"]);
        args.AddRange(["-sn", "-dn"]);
        if (cover is null)
            args.Add("-vn");
        args.Add(outputPath);

        return new AudioEncodePlan(args, ExpectedDuration(source, edit), copy);
    }

    /// <summary>The surround layout libopus accepts for a channel count (Vorbis channel order), or null for mono/stereo.</summary>
    public static string? OpusSurroundLayout(int channels) => channels switch
    {
        3 => "3.0",
        4 => "quad",
        5 => "5.0",
        6 => "5.1",
        7 => "6.1",
        8 => "7.1",
        _ => null,
    };

    static TimeSpan? ExpectedDuration(MediaInfo source, AudioEdit edit)
    {
        if (edit.OutputDuration is { } exact)
            return exact;
        var total = source.Duration ?? source.Audio?.Duration;
        if (total is null)
            return null;
        var length = total.Value - (edit.Start ?? TimeSpan.Zero);
        if (edit.Duration is { } limit && limit < length)
            length = limit;
        if (length <= TimeSpan.Zero)
            return null;
        return edit.Speed > 0 ? TimeSpan.FromTicks((long)(length.Ticks / edit.Speed)) : length;
    }

    static string Seconds(TimeSpan time) => time.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture);
}
