using Clementine.Core.Hosting;

namespace Clementine.App.Startup;

/// <summary>
/// Collects <c>--wheel</c> requests into one wheel. Explorer starts one Clementine process per selected file for a
/// context-menu verb, so requests that arrive close together (each within <see cref="QuietPeriod"/> of the previous
/// one, at most <see cref="MaxDelay"/> after the first) belong to the same selection. Pure logic: the caller supplies
/// the clock and polls <see cref="DueIn"/> / <see cref="TryFlush"/>.
/// </summary>
public sealed class WheelRequestAggregator
{
    readonly List<string> _paths = [];
    readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    DateTimeOffset? _first;
    DateTimeOffset _last;
    WheelMode _mode;

    // A batch flushed by MaxDelay while launches were still streaming in (a big Explorer selection on a busy PC).
    // The next requests continue that selection: their batch repeats these paths so the wheel ends up with all files.
    (List<string> Paths, WheelMode Mode, DateTimeOffset Last)? _cutShort;

    public WheelRequestAggregator(TimeSpan? quietPeriod = null, TimeSpan? maxDelay = null)
    {
        QuietPeriod = quietPeriod ?? TimeSpan.FromMilliseconds(400);
        MaxDelay = maxDelay ?? TimeSpan.FromSeconds(2.5);
    }

    public TimeSpan QuietPeriod { get; }

    public TimeSpan MaxDelay { get; }

    public bool HasPending => _first is not null;

    /// <summary>Adds a request. The first request of a batch decides the wheel mode.</summary>
    public void Add(IEnumerable<string> paths, WheelMode mode, DateTimeOffset now)
    {
        if (_first is null)
        {
            _first = now;
            _mode = mode;
            if (_cutShort is { } cut && cut.Mode == mode && now - cut.Last <= QuietPeriod)
            {
                foreach (var earlier in cut.Paths)
                {
                    if (_seen.Add(earlier))
                        _paths.Add(earlier);
                }
            }
            _cutShort = null;
        }
        _last = now;
        foreach (var path in paths)
        {
            if (_seen.Add(path))
                _paths.Add(path);
        }
    }

    /// <summary>Time until the pending batch should be shown (zero if overdue), or null when nothing is pending.</summary>
    public TimeSpan? DueIn(DateTimeOffset now)
    {
        if (_first is not { } first)
            return null;
        var due = Min(_last + QuietPeriod, first + MaxDelay);
        var remaining = due - now;
        return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
    }

    /// <summary>
    /// Returns the batch (paths in arrival order, duplicates removed) once it is due, and resets. When the batch was cut
    /// short by <see cref="MaxDelay"/> and more requests follow within <see cref="QuietPeriod"/>, the next batch
    /// contains these paths again plus the new ones, so showing it replaces the partial wheel with the whole selection.
    /// </summary>
    public (IReadOnlyList<string> Paths, WheelMode Mode)? TryFlush(DateTimeOffset now)
    {
        if (DueIn(now) is not { } remaining || remaining > TimeSpan.Zero)
            return null;
        var paths = _paths.ToList();
        var batch = (Paths: (IReadOnlyList<string>)paths, Mode: _mode);
        _cutShort = now < _last + QuietPeriod ? (paths, _mode, _last) : null;
        _paths.Clear();
        _seen.Clear();
        _first = null;
        return batch;
    }

    static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
