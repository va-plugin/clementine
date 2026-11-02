using System.Globalization;
using System.Text.RegularExpressions;
using Clementine.Core;
using Clementine.Engines.Media.FFmpeg;

namespace Clementine.Engines.Media.Audio;

/// <summary>A time range to bleep.</summary>
public readonly record struct TimeRange(TimeSpan Start, TimeSpan End);

/// <summary>Filter text builders and parsers for the audio tools (pure, unit-tested).</summary>
public static partial class AudioFilters
{
    /// <summary>Parses "0:12.5-0:13.2, 1:05-1:06" (commas, semicolons or new lines between ranges; "-" or "–" inside).</summary>
    public static IReadOnlyList<TimeRange> ParseRanges(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new OperationException("Enter at least one range to bleep, like 0:12.5-0:13.2.");
        var ranges = new List<TimeRange>();
        foreach (var raw in text.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = raw.Split(['-', '–', '—'], StringSplitOptions.TrimEntries);
            if (parts.Length != 2
                || !OptionValues.TryParseTime(parts[0], out var start)
                || !OptionValues.TryParseTime(parts[1], out var end))
            {
                throw new OperationException($"Couldn't read \"{raw}\". Write ranges like 0:12.5-0:13.2, 1:05-1:06.");
            }
            if (end <= start)
                throw new OperationException($"In \"{raw}\" the end comes before the start.");
            ranges.Add(new TimeRange(start, end));
        }
        return ranges.OrderBy(r => r.Start).ToList();
    }

    /// <summary>"between(t,12.5,13.2)+between(t,65,66)" — true while inside any range.</summary>
    public static string Between(IEnumerable<TimeRange> ranges) =>
        string.Join('+', ranges.Select(r => $"between(t,{Inv(r.Start.TotalSeconds)},{Inv(r.End.TotalSeconds)})"));

    /// <summary>Graph that silences the ranges and, for <paramref name="tone"/>, lays a 1 kHz beep over them.</summary>
    public static Func<string, string> BleepGraph(IReadOnlyList<TimeRange> ranges, bool tone, AudioStreamInfo audio, TimeSpan duration)
    {
        var inside = Between(ranges);
        if (!tone)
            return input => $"{input}volume=enable='{inside}':volume=0[edited_a]";
        var layout = audio.ChannelLayout is { Length: > 0 } l && !l.Contains('(') ? l : audio.Channels == 1 ? "mono" : "stereo";
        var rate = audio.SampleRate > 0 ? audio.SampleRate : 48000;
        return input =>
            $"{input}volume=enable='{inside}':volume=0[bleep_muted];" +
            $"sine=frequency=1000:sample_rate={rate}:duration={Inv(Math.Max(0.1, duration.TotalSeconds))},volume=2," + // sine is −18 dBFS; beep at −12
            $"volume=enable='not({inside})':volume=0,aformat=sample_rates={rate}:channel_layouts={layout}[bleep_tone];" +
            "[bleep_muted][bleep_tone]amix=inputs=2:duration=first:normalize=0[edited_a]";
    }

    /// <summary>Fade in at the start and out at the end (each clamped to half the clip).</summary>
    public static IReadOnlyList<string> Fades(TimeSpan fadeIn, TimeSpan fadeOut, TimeSpan duration)
    {
        if (fadeIn <= TimeSpan.Zero && fadeOut <= TimeSpan.Zero)
            throw new OperationException("Set a fade-in, a fade-out, or both.");
        var half = duration > TimeSpan.Zero ? duration / 2 : TimeSpan.MaxValue;
        var filters = new List<string>();
        if (fadeIn > TimeSpan.Zero)
            filters.Add($"afade=t=in:st=0:d={Inv(Min(fadeIn, half).TotalSeconds)}");
        if (fadeOut > TimeSpan.Zero)
        {
            if (duration <= TimeSpan.Zero)
                throw new OperationException("Couldn't work out how long this file is, so it can't fade out.");
            var length = Min(fadeOut, half);
            filters.Add($"afade=t=out:st={Inv((duration - length).TotalSeconds)}:d={Inv(length.TotalSeconds)}");
        }
        return filters;
    }

    /// <summary>Reads loudnorm's first-pass JSON from FFmpeg's log.</summary>
    public static LoudnessMeasurement ParseLoudnorm(string stderr)
    {
        var start = stderr.LastIndexOf('{');
        var end = stderr.LastIndexOf('}');
        if (start < 0 || end < start)
            throw new OperationException("Couldn't measure the loudness of this file.", stderr);
        var json = stderr[start..(end + 1)];
        string Value(string key)
        {
            var match = Regex.Match(json, $"\"{key}\"\\s*:\\s*\"([^\"]*)\"");
            return match.Success ? match.Groups[1].Value : throw new OperationException("Couldn't measure the loudness of this file.", json);
        }
        var integrated = Value("input_i");
        if (integrated.Contains("inf", StringComparison.OrdinalIgnoreCase))
            throw new OperationException("This file is silent, so there's nothing to normalise.");
        return new LoudnessMeasurement(integrated, Value("input_tp"), Value("input_lra"), Value("input_thresh"), Value("target_offset"));
    }

    /// <summary>Reads "max_volume: -3.2 dB" from volumedetect output.</summary>
    public static double ParseMaxVolume(string stderr)
    {
        var match = MaxVolume().Match(stderr);
        if (!match.Success)
            throw new OperationException("Couldn't measure the peak level of this file.", stderr);
        if (match.Groups[1].Value.Contains("inf", StringComparison.OrdinalIgnoreCase))
            throw new OperationException("This file is silent, so there's nothing to normalise.");
        return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>Cut points in the middle of each detected silence (silences touching the start or end are ignored).</summary>
    public static IReadOnlyList<TimeSpan> SilenceCuts(string stderr, TimeSpan duration)
    {
        var cuts = new List<TimeSpan>();
        double? start = null;
        foreach (Match match in SilenceEvent().Matches(stderr))
        {
            var value = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            if (match.Groups[1].Value == "start")
            {
                start = value;
                continue;
            }
            if (start is { } from && from > 0.05 && (duration <= TimeSpan.Zero || value < duration.TotalSeconds - 0.05))
                cuts.Add(TimeSpan.FromSeconds((from + value) / 2));
            start = null;
        }
        return cuts;
    }

    static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    static string Inv(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"max_volume:\s*(-?[\d.]+|-?inf)\s*dB")]
    private static partial Regex MaxVolume();

    [GeneratedRegex(@"silence_(start|end):\s*(-?[\d.]+)")]
    private static partial Regex SilenceEvent();
}

/// <summary>loudnorm first-pass measurements (kept as FFmpeg printed them).</summary>
public sealed record LoudnessMeasurement(string IntegratedLufs, string TruePeak, string Range, string Threshold, string Offset);
