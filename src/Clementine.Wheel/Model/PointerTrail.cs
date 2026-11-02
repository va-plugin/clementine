using System.Windows;

namespace Clementine.Wheel;

/// <summary>What the pointer's latest movement says about the open fan.</summary>
public enum PointerIntent
{
    /// <summary>Not moving: no new position (OLE repeats DragOver while the mouse rests) or only hand jitter.</summary>
    Resting,

    /// <summary>
    /// Moving, but the evidence doesn't settle it yet: too slowly or too short a stretch to judge, just turned a
    /// corner, a single ray that only just misses the fan, or a stretch without progress that is still too short.
    /// </summary>
    Unsure,

    /// <summary>
    /// Moving along a line that enters the open fan, but not yet far enough (since setting off, or since turning) to
    /// tell whether it is closing in on it.
    /// </summary>
    Approaching,

    /// <summary>Moving along a line that enters the open fan, and closing in on it.</summary>
    TowardFan,

    /// <summary>Moving, but not at the fan: along the ring, inwards, through the centre or away.</summary>
    Elsewhere,
}

/// <summary>
/// Thresholds of the menu-aim test (DIPs, degrees, time). Tuned against every group → variant path of the real
/// catalogs (straight at several speeds, curved, overshooting, along the gap, from rest near the group's edge, slow,
/// hesitating, with hand tremor and jitter, overshooting past the catch area, snapped to 100–200 % pixel grids) and
/// against sweeps round the inner ring from 20 to 800 DIPs/s and hops onto a neighbour (see AimTests and AimAdversaryTests).
/// </summary>
internal sealed record AimTuning
{
    public static AimTuning Default { get; } = new();

    /// <summary>
    /// Path length the direction is fitted over (least squares through every sample on it): long enough that a hand
    /// tremor or the pixel grid hardly swings it, short enough that a sweep round the ring is seen to turn in time.
    /// </summary>
    public double DirectionTravel { get; init; } = 24;

    /// <summary>Smallest displacement that gives a direction at all.</summary>
    public double MinTravel { get; init; } = 5;

    /// <summary>How far back a direction may reach (the previous sample always counts), so a slow move still gets a full fit.</summary>
    public TimeSpan DirectionWindow { get; init; } = TimeSpan.FromMilliseconds(1000);

    /// <summary>Path length over which closing in on the fan is judged.</summary>
    public double ProgressTravel { get; init; } = 20;

    /// <summary>How far back that judgement may reach: slow moves and short stops on the way still get judged.</summary>
    public TimeSpan ProgressWindow { get; init; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>Share of the distance travelled that must be gained on the fan.</summary>
    public double ProgressRatio { get; init; } = 0.3;

    /// <summary>
    /// Path over which every judgement must show no progress before the pointer counts as sweeping past (at least
    /// <see cref="Strikes"/> judgements). Measured in DIPs, not samples, so a densely sampled slow hand with a tremor
    /// isn't judged on a few pixels.
    /// </summary>
    public double StrikeTravel { get; init; } = 16;

    /// <summary>Fewest judgements without progress before the pointer counts as sweeping past.</summary>
    public int Strikes { get; init; } = 2;

    /// <summary>Judgements in a row that must show progress before the pointer counts as heading for the fan.</summary>
    public int Proofs { get; init; } = 2;

    /// <summary>
    /// Progress is only judged against samples moving within this many degrees of the current direction: a sweep along
    /// the ring turns a few degrees per sample, a deliberate change of course (out of the centre onto a group, then
    /// off towards a variant) turns at once, and the distance measured before such a corner says nothing.
    /// </summary>
    public double MaxTurn { get; init; } = 25;

    /// <summary>How far (DIPs) a sample may lie off the line through the stretch a direction is fitted over before that stretch counts as turning a corner.</summary>
    public double CornerDeviation { get; init; } = 5;

    /// <summary>Degrees the fan's arc is widened by for the aim test, so a slightly curved path still counts.</summary>
    public double ArcMargin { get; init; } = 4;

    /// <summary>A ray missing the widened arc by less than this many degrees more is a near miss: it takes two in a row.</summary>
    public double NearMissMargin { get; init; } = 12;

    /// <summary>
    /// Turning over a neighbour that has a fan of its own and going straight out along its axis lets that fan open at
    /// once: the pointer turned by at least <see cref="OwnAxisCorner"/> degrees inside that neighbour, is in its middle
    /// (within this share of its sweep from its axis), and heads within <see cref="OwnAxisTurn"/> degrees of the axis
    /// over at least <see cref="OwnAxisTravel"/> DIPs. That is how one hops onto a group and goes for its variants; a
    /// path to the open fan's variant beyond it crosses the neighbour without turning there.
    /// </summary>
    public double OwnAxisShare { get; init; } = 0.25;

    public double OwnAxisTurn { get; init; } = 15;

    public double OwnAxisTravel { get; init; } = 10;

    public double OwnAxisCorner { get; init; } = 45;

    /// <summary>Rays passing closer than this share of the dead zone's radius to the centre aim at the centre.</summary>
    public double CentreClearanceShare { get; init; } = 0.5;

    /// <summary>Net movement within <see cref="StillWindow"/> below which the pointer counts as resting (hand jitter).</summary>
    public double StillTravel { get; init; } = 1.5;

    public TimeSpan StillWindow { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// For how long after the last proof of aiming (or after the hold began) a pointer that keeps moving, but too slowly
    /// or briefly to be judged (setting off from rest, slowing into a short stop, setting off again), still keeps the
    /// fan held. Once it rests, the settle delay runs.
    /// </summary>
    public TimeSpan MovingGrace { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// For how long after the hold began (or the last proof) a pointer heading at the fan but not yet far enough to
    /// be judged keeps the fan held: a slow hand setting off from rest takes a while to cover a judgeable stretch,
    /// while a sweep along the ring gets judged, and given way to, after the same stretch whatever its speed.
    /// </summary>
    public TimeSpan ApproachGrace { get; init; } = TimeSpan.FromMilliseconds(600);
}

/// <summary>
/// Recent pointer samples (newest last) and the menu-aim ("safe triangle") test on them. A sample counts as heading
/// for the fan when the ray from the pointer along its recent direction enters the fan's arc on the outer band
/// (<see cref="WheelLayout.AimDistance"/>) and the distance left along that ray shrinks about as fast as the pointer
/// travels. That second test separates a straight move towards a variant from sweeping along the inner ring: a
/// sweep's ray turns with the ring, so it keeps pointing at the fan without ever getting closer. Evidence either way
/// has to hold over a stretch of path before it counts, so tremor, jitter and the pixel grid don't decide. Pure;
/// time comes from the caller.
/// </summary>
internal sealed class PointerTrail(AimTuning? tuning = null)
{
    /// <summary>Enough samples for the windows below even at half a DIP per sample (a 200 % monitor, a slow hand).</summary>
    const int Capacity = 128;

    readonly AimTuning _tuning = tuning ?? AimTuning.Default;
    readonly List<Sample> _samples = new(Capacity + 1);
    int _nearMisses;
    int _stalls;
    double _stallFrom;
    (double Path, double Progress) _stallReference;
    int _proofs;

    /// <summary>
    /// One pointer sample. <see cref="Progress"/> is the distance left to the fan along the fitted line, measured from
    /// the middle of the stretch it was fitted over (<see cref="FitPath"/>): that point lies on the line however long the
    /// stretch, so a fit that grows after a corner doesn't make a sweep look as if it were closing in.
    /// </summary>
    readonly record struct Sample(Vector Position, TimeSpan Time, double Path, int Fan, Vector Heading, double? Progress, double FitPath);

    /// <summary>
    /// A fitted line: its heading (unit vector), the middle of the stretch it was fitted over, that stretch's length and
    /// where it starts, and, when it starts at a corner, how many degrees the pointer turned there.
    /// </summary>
    readonly record struct Fit(Vector Heading, Vector Centre, double CentrePath, double Span, Vector Start, double? Turn);

    public AimTuning Tuning => _tuning;

    /// <summary>
    /// The pointer's heading at the last <see cref="Add"/> (unit vector), the straight stretch it was fitted over (its
    /// length and start), and how sharply the pointer turned into that stretch (degrees; null when it didn't start at a
    /// corner); null without a heading.
    /// </summary>
    public (Vector Heading, double Span, Vector Start, double? Turn)? Heading { get; private set; }

    public void Clear()
    {
        _samples.Clear();
        Heading = null;
        _nearMisses = 0;
        _stalls = 0;
        _proofs = 0;
    }

    /// <summary>Records the pointer at <paramref name="position"/> and classifies the movement against the fan of <paramref name="fan"/>.</summary>
    public PointerIntent Add(Vector position, TimeSpan time, WheelLayout layout, int fan)
    {
        var path = 0.0;
        if (_samples.Count > 0)
        {
            var last = _samples[^1];
            var step = (position - last.Position).Length;
            // OLE repeats DragOver while the mouse rests: no news, and the rest shows as a gap before the next move.
            if (step < 1e-6 && last.Fan == fan)
                return PointerIntent.Resting;
            path = last.Path + step;
            if (last.Fan != fan)
            {
                // Evidence about another fan says nothing about this one.
                _nearMisses = 0;
                _stalls = 0;
                _proofs = 0;
            }
        }

        var intent = IsMoving(position, time) ? PointerIntent.Unsure : PointerIntent.Resting;
        double? progress = null;
        var fit = new Fit(default, position, path, 0, position, null);
        Heading = null;
        if (fan >= 0 && Direction(position, time, path) is { } direction)
        {
            Heading = (direction.Heading, direction.Span, direction.Start, direction.Turn);
            fit = direction;
            intent = Classify(layout, fan, position, fit, time, out progress);
        }

        _samples.Add(new Sample(position, time, path, fan, fit.Heading, progress, fit.CentrePath));
        if (_samples.Count > Capacity)
            _samples.RemoveAt(0);
        return intent;
    }

    PointerIntent Classify(WheelLayout layout, int fan, Vector position, Fit fit, TimeSpan time, out double? progress)
    {
        // With a lone group the far side of the centre is that group's ring and fan again, so crossing it is fine.
        var clearance = layout.SegmentCount > 1 ? layout.Metrics.InnerRingInnerRadius * _tuning.CentreClearanceShare : 0;
        var heading = fit.Heading;
        progress = null;
        if (layout.AimDistance(fan, position, heading, _tuning.ArcMargin, clearance) is not { } left)
        {
            // A clear miss (inwards, through the centre, away) counts at once; one that only just misses takes two.
            var near = layout.AimDistance(fan, position, heading, _tuning.ArcMargin + _tuning.NearMissMargin, clearance) is not null;
            return near && ++_nearMisses < 2 ? PointerIntent.Unsure : PointerIntent.Elsewhere;
        }
        _nearMisses = 0;
        if (left <= 0)
        {
            // Already out on the band, just beside the fan (overshooting past its end): there is nothing left to gain.
            _stalls = 0;
            return PointerIntent.TowardFan;
        }

        // Judge progress from the middle of the fitted stretch, where the fitted line really runs.
        progress = layout.AimDistance(fan, fit.Centre, heading, _tuning.ArcMargin, clearance);
        if (progress is not { } remaining || Reference(heading, fit.CentrePath, time, fan) is not { } reference)
            return PointerIntent.Approaching;
        var path = fit.CentrePath;
        if (Gains(reference, remaining, path))
        {
            // One noisy judgement isn't proof either way, but it's no reason to doubt the pointer either.
            if (++_proofs < _tuning.Proofs)
                return PointerIntent.Approaching;
            _stalls = 0;
            return PointerIntent.TowardFan;
        }

        // Pointing at the fan without getting closer: sweeping round under it, once that has gone on for a while and
        // the whole stretch since it began shows no progress either (a shaky hand's misjudgements don't add up).
        _proofs = 0;
        if (_stalls++ == 0)
        {
            _stallFrom = path;
            _stallReference = reference;
        }
        return _stalls >= _tuning.Strikes && path - _stallFrom >= _tuning.StrikeTravel && !Gains(_stallReference, remaining, path)
            ? PointerIntent.Elsewhere
            : PointerIntent.Unsure;
    }

    /// <summary>Whether the distance left to the fan dropped by at least <see cref="AimTuning.ProgressRatio"/> of the path travelled since <paramref name="reference"/>.</summary>
    bool Gains((double Path, double Progress) reference, double remaining, double path) =>
        reference.Progress - remaining >= _tuning.ProgressRatio * (path - reference.Path);

    /// <summary>Whether the pointer got at least <see cref="AimTuning.StillTravel"/> away from where it was a moment ago.</summary>
    bool IsMoving(Vector position, TimeSpan time)
    {
        if (_samples.Count == 0)
            return false;
        var anchor = _samples[^1].Position;
        for (var i = _samples.Count - 2; i >= 0 && time - _samples[i].Time <= _tuning.StillWindow; i--)
            anchor = _samples[i].Position;
        return (position - anchor).Length >= _tuning.StillTravel;
    }

    /// <summary>
    /// The pointer's heading: the least-squares line through the samples on the last
    /// <see cref="AimTuning.DirectionTravel"/> of path (within <see cref="AimTuning.DirectionWindow"/>; the previous sample
    /// always counts), back to a corner at most (<see cref="Bends"/>), pointing the way it moved; null when that stretch
    /// is shorter than <see cref="AimTuning.MinTravel"/>.
    /// </summary>
    Fit? Direction(Vector position, TimeSpan time, double path)
    {
        var first = _samples.Count;
        var corner = false;
        for (var i = _samples.Count - 1; i >= 0; i--)
        {
            var sample = _samples[i];
            if (i < _samples.Count - 1 && time - sample.Time > _tuning.DirectionWindow)
                break;
            if (i < _samples.Count - 1 && Bends(i, position))
            {
                corner = true;
                break;
            }
            first = i;
            if (path - sample.Path >= _tuning.DirectionTravel)
                break;
        }
        if (first == _samples.Count)
            return null;
        var displacement = position - _samples[first].Position;
        if (displacement.Length < _tuning.MinTravel)
            return null;

        var n = _samples.Count - first + 1;
        var mean = position;
        var meanPath = path;
        for (var i = first; i < _samples.Count; i++)
        {
            mean += _samples[i].Position;
            meanPath += _samples[i].Path;
        }
        mean /= n;
        meanPath /= n;
        double sxx = 0, syy = 0, sxy = 0;
        void Accumulate(Vector p)
        {
            var d = p - mean;
            sxx += d.X * d.X;
            syy += d.Y * d.Y;
            sxy += d.X * d.Y;
        }
        Accumulate(position);
        for (var i = first; i < _samples.Count; i++)
            Accumulate(_samples[i].Position);
        var angle = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
        var axis = new Vector(Math.Cos(angle), Math.Sin(angle));
        if (Vector.Multiply(axis, displacement) < 0)
            axis = -axis;
        double? turn = null;
        if (corner && _samples[first - 1].Heading is { LengthSquared: > 0 } before)
            turn = Angles.ToDegrees(Math.Acos(Math.Clamp(Vector.Multiply(before, axis), -1, 1)));
        return new Fit(axis, mean, meanPath, path - _samples[first].Path, _samples[first].Position, turn);
    }

    /// <summary>
    /// Whether the path from sample <paramref name="from"/> to <paramref name="position"/> turns a corner: a sample on
    /// it lies more than <see cref="AimTuning.CornerDeviation"/> off the straight line between the two, so the fit stops
    /// there (out of the centre onto a group, then off towards a variant; the ring's curve and a tremor stay well under).
    /// </summary>
    bool Bends(int from, Vector position)
    {
        var start = _samples[from].Position;
        var chord = position - start;
        var length = chord.Length;
        if (length < 1e-9)
            return false;
        for (var j = from + 1; j < _samples.Count; j++)
        {
            var offset = _samples[j].Position - start;
            if (Math.Abs(chord.X * offset.Y - chord.Y * offset.X) / length > _tuning.CornerDeviation)
                return true;
        }
        return false;
    }

    /// <summary>
    /// The sample (path and distance left, both at the middle of its fit) to judge progress against: about
    /// <see cref="AimTuning.ProgressTravel"/> back, heading the same way; null with nothing to compare against yet (just
    /// started, just turned a corner, or too slow to have covered that stretch within <see cref="AimTuning.ProgressWindow"/>).
    /// </summary>
    (double Path, double Progress)? Reference(Vector heading, double path, TimeSpan time, int fan)
    {
        var minCos = Math.Cos(Angles.ToRadians(_tuning.MaxTurn));
        for (var i = _samples.Count - 1; i >= 0; i--)
        {
            var sample = _samples[i];
            if (time - sample.Time > _tuning.ProgressWindow || sample.Fan != fan)
                return null;
            if (sample.Progress is not { } before)
                continue;
            if (Vector.Multiply(sample.Heading, heading) < minCos)
                return null;
            if (path - sample.FitPath < _tuning.ProgressTravel)
                continue;
            return (sample.FitPath, before);
        }
        return null;
    }
}
