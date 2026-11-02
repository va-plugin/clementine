using System.Globalization;

namespace Clementine.Engines.Media.FFmpeg;

/// <summary>
/// Turns FFmpeg's "-progress pipe:1" key=value lines into a 0..1 fraction of the expected output duration.
/// Thread-safe: lines arrive on a reader thread.
/// </summary>
public sealed class FfmpegProgressParser
{
    readonly double _expectedMicroseconds;
    readonly object _gate = new();
    double _fraction;

    /// <param name="expectedDuration">How long the output will be; null or zero means progress is indeterminate.</param>
    public FfmpegProgressParser(TimeSpan? expectedDuration) =>
        _expectedMicroseconds = expectedDuration is { TotalMicroseconds: > 0 } d ? d.TotalMicroseconds : 0;

    /// <summary>Latest fraction (0..1), or null when the expected duration is unknown.</summary>
    public double? Fraction
    {
        get
        {
            lock (_gate)
                return _expectedMicroseconds > 0 || _fraction >= 1 ? _fraction : null;
        }
    }

    /// <summary>True once FFmpeg reported "progress=end".</summary>
    public bool IsFinished { get; private set; }

    /// <summary>Feeds one stdout line; returns true when the fraction changed.</summary>
    public bool Feed(string line)
    {
        var eq = line.IndexOf('=');
        if (eq <= 0)
            return false;
        var key = line.AsSpan(0, eq).Trim();
        var value = line.AsSpan(eq + 1).Trim();

        if (key.SequenceEqual("progress"))
        {
            if (value.SequenceEqual("end"))
            {
                IsFinished = true;
                lock (_gate)
                    _fraction = 1;
                return true;
            }
            return false;
        }

        // "out_time_ms" is (despite its name) also microseconds; prefer out_time_us when present.
        if (!key.SequenceEqual("out_time_us") && !key.SequenceEqual("out_time_ms"))
            return false;
        if (_expectedMicroseconds <= 0 || !long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds) || microseconds < 0)
            return false;

        var fraction = Math.Clamp(microseconds / _expectedMicroseconds, 0, 0.995);
        lock (_gate)
        {
            if (fraction <= _fraction)
                return false;
            _fraction = fraction;
            return true;
        }
    }
}
