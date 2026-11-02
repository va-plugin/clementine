using System.Windows;

namespace Clementine.Wheel;

/// <summary>An inner-ring segment on the current page: a group, or the "More" segment that flips pages.</summary>
public sealed record WheelSegment(WheelGroup? Group, int HiddenCount = 0)
{
    public bool IsMore => Group is null;

    public string Label => Group?.Name ?? "More";

    public string? IconKey => Group is null ? IconGlyphs.MoreKey : Group.IconKey;

    public IReadOnlyList<WheelOption> Options => Group?.Options ?? [];

    public bool HasVariants => Options.Count > 1;

    public bool IsAvailable => Group?.IsAvailable ?? true;
}

/// <summary>Keyboard moves in click mode.</summary>
public enum WheelNavigation
{
    /// <summary>Right arrow: next segment, or the next variant around the fan (wrapping), clockwise.</summary>
    Clockwise,

    /// <summary>Left arrow: previous segment or variant.</summary>
    CounterClockwise,

    /// <summary>Up arrow: from a group out to its variants.</summary>
    Outward,

    /// <summary>Down arrow: from a variant back to its group.</summary>
    Inward,

    /// <summary>Tab: next group (always on the inner ring).</summary>
    NextSegment,

    /// <summary>Shift+Tab: previous group.</summary>
    PreviousSegment,
}

/// <summary>
/// Interaction state of one open wheel: the current page and its layout, what the pointer (or keyboard) is on,
/// which segment's variants are fanned out, and what a drop/click would run. Pure and UI-free.
/// </summary>
/// <remarks>
/// <para><b>Aim detection.</b> The path from a group out to one of its off-axis variants crosses neighbouring
/// segments of the inner ring first. While a fan is open and the pointer comes onto something else, the open group
/// (and its fan) stays highlighted and the switch waits (<see cref="IsSwitchPending"/>); <see cref="PointerTrail"/>
/// decides how long. The switch happens at once when the pointer is seen moving anywhere but at the fan (along the
/// ring, inwards, away), or when it turns over a neighbour that has a fan of its own and goes straight out along its
/// middle. Otherwise it happens once <see cref="AimSettleDelay"/> has passed without proof that the pointer is closing
/// in on the fan (<see cref="Tick"/>): every hold gets one settle delay to start with (the pointer may only just have
/// set off, slowly, from near the group's edge), and moving on without being judged yet extends it for a little while
/// (<see cref="AimTuning.ApproachGrace"/>, <see cref="AimTuning.MovingGrace"/>: setting off from rest, slowing into a
/// short stop and setting off again). A drop or click (<see cref="Settle"/>) always takes what is under the pointer,
/// and <see cref="DropTarget"/> says so in advance (the view outlines that group while the fan is held).</para>
/// <para>The centre dead zone still clears the highlight at once (nothing is targeted, a drop there cancels); only
/// folding the fan waits while the pointer crosses it on a straight line towards the far side of a wide fan.</para>
/// <para>When the pointer leaves the wheel (<see cref="PointerLeft"/>) the open fan stays for <see cref="LeaveGrace"/>,
/// so overshooting past the outer edge and coming straight back still lands on the variant.</para>
/// </remarks>
public sealed class WheelSession
{
    /// <summary>Growth steps tried, in order, when the ring labels don't fit a page (see <see cref="WheelLayout.MaxLabelGrowth"/>).</summary>
    static readonly double[] GrowthSteps = [1.04, 1.08, 1.12, WheelLayout.MaxLabelGrowth];

    /// <summary>How long the pointer must rest on a neighbour (without heading for the open fan) before the fan gives way to it.</summary>
    public static readonly TimeSpan AimSettleDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>How long the open fan stays after the pointer leaves the wheel, so an overshoot can come back to it.</summary>
    public static readonly TimeSpan LeaveGrace = TimeSpan.FromMilliseconds(400);

    /// <summary>How far (DIPs) a highlighted fan reaches down into the inner ring (hysteresis).</summary>
    public const double FanHysteresis = 8;

    /// <summary>How far (DIPs along the outer band) a highlighted fan reaches beyond its two ends (hysteresis).</summary>
    public const double FanEndHysteresis = 12;

    readonly WheelMetrics _metrics;
    readonly Func<WheelLayout, IReadOnlyList<WheelSegment>, bool>? _labelsFit;
    readonly Func<TimeSpan> _clock;
    PointerTrail _trail = new();
    Vector? _lastPointer;
    WheelHit? _pending;
    TimeSpan _deadline;
    TimeSpan _holdSince;
    TimeSpan? _aimedAt;
    TimeSpan? _leftAt;

    /// <param name="labelsFit">
    /// Optional check that every inner-ring label fits a layout without "…"; when it fails the page grows a little
    /// (up to <see cref="WheelLayout.MaxLabelGrowth"/>) instead of cutting a label.
    /// </param>
    /// <param name="clock">Monotonic time for aim detection (tests pass a fake one); defaults to a stopwatch.</param>
    public WheelSession(WheelModel model, WheelMetrics? metrics = null, Func<WheelLayout, IReadOnlyList<WheelSegment>, bool>? labelsFit = null,
        Func<TimeSpan>? clock = null)
    {
        Model = model;
        _metrics = metrics ?? WheelMetrics.Default;
        _labelsFit = labelsFit;
        if (clock is null)
        {
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            clock = () => System.Diagnostics.Stopwatch.GetElapsedTime(start);
        }
        _clock = clock;
        Pages = WheelPager.Paginate(model.Groups.Count);
        ShowPage(0);
    }

    /// <summary>
    /// A session as the wheel window shows it: the page grows when a long label would otherwise be cut. Text is
    /// measured at <paramref name="pixelsPerDip"/> (the target monitor's scale), because hinted text widths vary with it.
    /// </summary>
    internal static WheelSession ForDisplay(WheelModel model, double pixelsPerDip = 1, Func<TimeSpan>? clock = null) =>
        new(model, labelsFit: (layout, segments) => WheelView.RingLabelsFit(layout, segments, pixelsPerDip), clock: clock);

    public WheelModel Model { get; }

    public IReadOnlyList<WheelPage> Pages { get; }

    public int PageIndex { get; private set; }

    public WheelPage Page => Pages[PageIndex];

    public WheelLayout Layout { get; private set; } = null!;

    public IReadOnlyList<WheelSegment> Segments { get; private set; } = [];

    public WheelHit Hover { get; private set; } = WheelHit.None;

    /// <summary>Segment whose variants are fanned out on the outer ring (-1 for none).</summary>
    public int ExpandedSegment { get; private set; } = -1;

    /// <summary>True while the keyboard (not the pointer) drives the highlight.</summary>
    public bool KeyboardActive { get; private set; }

    public WheelSegment? HoveredSegment =>
        Hover.Kind is WheelHitKind.Segment or WheelHitKind.Variant ? Segments[Hover.Segment] : null;

    /// <summary>The operation the highlight stands for, available or not (the centre describes it).</summary>
    public WheelOption? HoveredOption => OptionAt(Hover);

    /// <summary>The operation the highlight stands for, if it can run; null over the centre, "More", or an unavailable operation.</summary>
    public WheelOption? Target => HoveredOption is { IsAvailable: true } option ? option : null;

    /// <summary>
    /// What a drop or click right now would run: <see cref="Target"/>, except while the open fan is held, when a drop
    /// settles to what is under the pointer (<see cref="Settle"/>). Pure; drag feedback (Copy/None) follows it.
    /// </summary>
    public WheelOption? DropTarget => _pending is { } pending
        ? OptionAt(pending) is { IsAvailable: true } option ? option : null
        : Target;

    public bool IsOverMore => Hover.Kind == WheelHitKind.Segment && Segments[Hover.Segment].IsMore;

    /// <summary>True while the open fan is held because the pointer is heading for it; call <see cref="Tick"/> until it clears.</summary>
    public bool IsSwitchPending => _pending is not null;

    /// <summary>What is under the pointer while a switch is pending (null otherwise).</summary>
    public WheelHit? PendingHit => _pending;

    /// <summary>True while <see cref="Tick"/> has work to do later: a pending switch, or a fan kept open after the pointer left.</summary>
    public bool NeedsTick => _pending is not null || _leftAt is not null;

    /// <summary>Thresholds of the aim test (tests tune them).</summary>
    internal AimTuning AimTuning
    {
        get => _trail.Tuning;
        set
        {
            _trail = new PointerTrail(value);
            _pending = null;
            _aimedAt = null;
        }
    }

    /// <summary>Updates the highlight for a pointer at <paramref name="offset"/> from the centre. Returns true if anything changed.</summary>
    public bool MovePointer(Vector offset)
    {
        var now = _clock();
        _lastPointer = offset;
        var wasKeyboard = KeyboardActive;
        KeyboardActive = false;
        var folded = Return(offset, now);
        var intent = _trail.Add(offset, now, Layout, ExpandedSegment);
        if (intent == PointerIntent.TowardFan)
            _aimedAt = now;
        var held = HeldFanReach;
        return Steer(Layout.HitTest(offset, ExpandedSegment, held, held > 0 ? FanEndHysteresis : 0), offset, intent, now) || wasKeyboard || folded;
    }

    /// <summary>
    /// Re-evaluates a pending switch without a new pointer sample (the window calls this on a timer, because a drag
    /// sends no events while the mouse rests), and folds a fan the pointer left and didn't come back to. Returns true
    /// if the highlight changed.
    /// </summary>
    public bool Tick()
    {
        var now = _clock();
        if (_leftAt is { } left)
        {
            if (now - left < LeaveGrace)
                return false;
            _leftAt = null;
            return Apply(WheelHit.None, fold: true);
        }
        if (_pending is not { } pending || now < _deadline)
            return false;
        _pending = null;
        return Apply(pending);
    }

    /// <summary>Takes whatever is under the pointer right now, ending a pending switch (a drop or click). Returns true if anything changed.</summary>
    public bool Settle()
    {
        if (_pending is not { } pending)
            return false;
        _pending = null;
        return Apply(pending);
    }

    /// <summary>Removes the highlight and folds the fan away (the wheel closes).</summary>
    public bool ClearHover()
    {
        _lastPointer = null;
        _pending = null;
        _leftAt = null;
        _aimedAt = null;
        _trail.Clear();
        var changed = Hover != WheelHit.None || ExpandedSegment != -1;
        Hover = WheelHit.None;
        ExpandedSegment = -1;
        return changed;
    }

    /// <summary>
    /// The pointer left the wheel (DragLeave, MouseLeave): nothing is highlighted, but an open fan stays for
    /// <see cref="LeaveGrace"/> (<see cref="Tick"/> folds it), so overshooting past the outer edge and coming back
    /// lands on the variant rather than on whatever lies under that angle without the fan. Returns true if anything changed.
    /// </summary>
    public bool PointerLeft()
    {
        _lastPointer = null;
        _pending = null;
        _aimedAt = null;
        _trail.Clear();
        var changed = Hover != WheelHit.None;
        Hover = WheelHit.None;
        _leftAt = ExpandedSegment >= 0 ? _clock() : null;
        return changed;
    }

    /// <summary>
    /// The pointer is back after leaving: the fan it left stays open if it comes back onto that fan or its group within
    /// <see cref="LeaveGrace"/>; anywhere else it folds first, so what is there is hit as if no fan were open.
    /// </summary>
    bool Return(Vector offset, TimeSpan now)
    {
        if (_leftAt is not { } left)
            return false;
        _leftAt = null;
        var open = ExpandedSegment;
        var hit = Layout.HitTest(offset, open);
        if (now - left < LeaveGrace && hit.Kind is WheelHitKind.Segment or WheelHitKind.Variant && hit.Segment == open)
            return false;
        ExpandedSegment = -1;
        _aimedAt = null;
        return open != -1;
    }

    /// <summary>Shows the next page (wrapping). Returns false when everything fits on one page.</summary>
    public bool NextPage()
    {
        if (Pages.Count < 2)
            return false;
        var pointer = _lastPointer;
        ShowPage((PageIndex + 1) % Pages.Count);
        if (pointer is { } p)
        {
            _lastPointer = p;
            Apply(Layout.HitTest(p, ExpandedSegment));
        }
        return true;
    }

    /// <summary>Moves the highlight with the keyboard. Returns true if anything changed.</summary>
    public bool Navigate(WheelNavigation navigation)
    {
        var count = Segments.Count;
        if (count == 0)
            return false;
        KeyboardActive = true;
        _pending = null;
        _leftAt = null;
        var backwards = navigation is WheelNavigation.CounterClockwise or WheelNavigation.PreviousSegment;

        switch (Hover.Kind)
        {
            case WheelHitKind.Segment:
            {
                var s = Hover.Segment;
                return navigation switch
                {
                    WheelNavigation.Clockwise or WheelNavigation.NextSegment => Apply(WheelHit.OnSegment((s + 1) % count)),
                    WheelNavigation.CounterClockwise or WheelNavigation.PreviousSegment => Apply(WheelHit.OnSegment((s - 1 + count) % count)),
                    WheelNavigation.Outward when Segments[s].HasVariants => Apply(WheelHit.OnVariant(s, 0)),
                    _ => true,
                };
            }
            case WheelHitKind.Variant:
            {
                var (s, v) = (Hover.Segment, Hover.Variant);
                var last = Segments[s].Options.Count - 1;
                return navigation switch
                {
                    WheelNavigation.Clockwise => Apply(WheelHit.OnVariant(s, v == last ? 0 : v + 1)),
                    WheelNavigation.CounterClockwise => Apply(WheelHit.OnVariant(s, v == 0 ? last : v - 1)),
                    WheelNavigation.Inward => Apply(WheelHit.OnSegment(s)),
                    WheelNavigation.NextSegment => Apply(WheelHit.OnSegment((s + 1) % count)),
                    WheelNavigation.PreviousSegment => Apply(WheelHit.OnSegment((s - 1 + count) % count)),
                    _ => true,
                };
            }
            default:
                return Apply(WheelHit.OnSegment(backwards ? count - 1 : 0));
        }
    }

    /// <summary>Highlights a segment or variant directly (previews, tests).</summary>
    internal bool Highlight(WheelHit hit)
    {
        _pending = null;
        _leftAt = null;
        return Apply(hit);
    }

    /// <summary>While a variant is highlighted its fan reaches a little into the inner ring.</summary>
    double HeldFanReach => Hover.Kind == WheelHitKind.Variant && Hover.Segment == ExpandedSegment ? FanHysteresis : 0;

    /// <summary>Applies a pointer hit, holding the open fan while the pointer heads for it (see the class remarks).</summary>
    bool Steer(WheelHit hit, Vector offset, PointerIntent intent, TimeSpan now)
    {
        var open = ExpandedSegment;
        var holdable = open >= 0 && open < Segments.Count && Segments[open].HasVariants
            && hit.Kind != WheelHitKind.None
            && !(hit.Kind is WheelHitKind.Segment or WheelHitKind.Variant && hit.Segment == open);
        if (!holdable || intent == PointerIntent.Elsewhere || GoingForOwnFan(hit, offset))
        {
            _pending = null;
            return Apply(hit);
        }

        // A hold always gets one settle delay to show where the pointer is going (it may only just have set off, from
        // rest near the group's edge). Proof of aiming renews it; so, for a little while, does moving on without being
        // judged yet: heading at the fan since the hold began, or anyhow soon after such proof (slowing into a short
        // stop and setting off again).
        if (_pending is null)
        {
            _holdSince = now;
            _deadline = now + AimSettleDelay;
        }
        var previous = _pending;
        var tuning = _trail.Tuning;
        var renew = intent switch
        {
            PointerIntent.TowardFan => true,
            PointerIntent.Approaching => now - (_aimedAt is { } a && a > _holdSince ? a : _holdSince) <= tuning.ApproachGrace,
            PointerIntent.Unsure => _aimedAt is { } aimed && now - aimed <= tuning.MovingGrace,
            _ => false,
        };
        if (renew && now + AimSettleDelay > _deadline)
            _deadline = now + AimSettleDelay;
        _pending = hit;
        if (now >= _deadline)
        {
            _pending = null;
            return Apply(hit);
        }

        // Crossing the dead zone on the way to the fan's far side: nothing is targeted there (a drop cancels), but the fan stays.
        if (hit.Kind == WheelHitKind.Centre && Hover != WheelHit.Centre)
        {
            Hover = WheelHit.Centre;
            return true;
        }
        // The view outlines what is under the pointer while the fan is held.
        return previous != hit;
    }

    /// <summary>The pointer turned over a neighbour that has a fan of its own and goes straight out along its middle (see <see cref="AimTuning.OwnAxisShare"/>).</summary>
    bool GoingForOwnFan(WheelHit hit, Vector offset)
    {
        if (hit.Kind != WheelHitKind.Segment || !Segments[hit.Segment].HasVariants || _trail.Heading is not { } heading)
            return false;
        var tuning = _trail.Tuning;
        var range = Layout.Segments[hit.Segment];
        return heading.Turn >= tuning.OwnAxisCorner
            && Layout.HitTest(heading.Start) == hit
            && heading.Span >= tuning.OwnAxisTravel
            && Math.Abs(Angles.Normalize(Angles.Of(offset) - range.Mid + 180) - 180) <= range.Sweep * tuning.OwnAxisShare
            && Math.Abs(Angles.Normalize(Angles.Of(heading.Heading) - range.Mid + 180) - 180) <= tuning.OwnAxisTurn;
    }

    /// <param name="fold">Also fold the fan when <paramref name="hit"/> is <see cref="WheelHit.None"/> (which otherwise keeps it).</param>
    bool Apply(WheelHit hit, bool fold = false)
    {
        if (hit.Kind is WheelHitKind.Segment or WheelHitKind.Variant && (hit.Segment < 0 || hit.Segment >= Segments.Count))
            hit = WheelHit.None;

        var expanded = hit.Kind switch
        {
            WheelHitKind.Segment => Segments[hit.Segment].HasVariants ? hit.Segment : -1,
            WheelHitKind.Variant => hit.Segment,
            WheelHitKind.Centre => -1,
            _ => fold ? -1 : ExpandedSegment,
        };
        var changed = hit != Hover || expanded != ExpandedSegment;
        if (expanded != ExpandedSegment)
            _aimedAt = null;
        Hover = hit;
        ExpandedSegment = expanded;
        return changed;
    }

    WheelOption? OptionAt(WheelHit hit) => hit.Kind switch
    {
        WheelHitKind.Segment when hit.Segment >= 0 && hit.Segment < Segments.Count => Segments[hit.Segment].Group?.Default,
        WheelHitKind.Variant when hit.Segment >= 0 && hit.Segment < Segments.Count && hit.Variant >= 0 && hit.Variant < Segments[hit.Segment].Options.Count
            => Segments[hit.Segment].Options[hit.Variant],
        _ => null,
    };

    void ShowPage(int index)
    {
        PageIndex = index;
        var page = Pages[index];
        var segments = Model.Groups.Skip(page.First).Take(page.Count).Select(g => new WheelSegment(g)).ToList();
        if (page.HasMore)
            segments.Add(new WheelSegment(null, Model.Groups.Count - page.Count));
        Segments = segments;
        var counts = segments.Select(s => s.Options.Count).ToList();
        var labels = segments.Select(s => (IReadOnlyList<string>)s.Options.Select(o => o.Label).ToList()).ToList();
        Layout = WheelLayout.Create(counts, _metrics, variantLabels: labels);
        if (_labelsFit is not null && segments.Count > 0)
        {
            try
            {
                foreach (var growth in GrowthSteps)
                {
                    if (_labelsFit(Layout, segments))
                        break;
                    Layout = WheelLayout.Create(counts, _metrics, growth, labels);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Measuring text must never stop the wheel from opening; keep the layout we have.
                System.Diagnostics.Debug.WriteLine(ex);
            }
        }
        Hover = WheelHit.None;
        ExpandedSegment = -1;
        _pending = null;
        _leftAt = null;
        _aimedAt = null;
        _trail.Clear();
    }
}
