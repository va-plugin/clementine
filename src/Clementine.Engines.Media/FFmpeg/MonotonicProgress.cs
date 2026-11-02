using Clementine.Core;

namespace Clementine.Engines.Media.FFmpeg;

/// <summary>
/// Forwards progress so the fraction never goes backwards and never leaves 0..1. Once a fraction has been shown, a later
/// "indeterminate" report keeps the last fraction (only the status text changes), so the bar doesn't flicker.
/// </summary>
public sealed class MonotonicProgress : IProgress<JobProgress>
{
    readonly IProgress<JobProgress> _inner;
    readonly object _gate = new();
    double? _last;

    MonotonicProgress(IProgress<JobProgress> inner) => _inner = inner;

    /// <summary>Wraps <paramref name="inner"/> (an already monotonic reporter is returned as-is).</summary>
    public static IProgress<JobProgress> Wrap(IProgress<JobProgress> inner) => inner as MonotonicProgress ?? new MonotonicProgress(inner);

    public void Report(JobProgress value)
    {
        JobProgress forwarded;
        lock (_gate)
        {
            if (value.Fraction is { } fraction && !double.IsNaN(fraction))
                _last = Math.Max(_last ?? 0, Math.Clamp(fraction, 0, 1));
            forwarded = value with { Fraction = _last };
        }
        _inner.Report(forwarded);
    }
}
