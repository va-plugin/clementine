using Clementine.Core;
using Clementine.Core.Hosting;

namespace Clementine.Wheel;

/// <summary>Where the primary mouse button is in its press → drag → release cycle.</summary>
public enum DragPhase
{
    /// <summary>Button up.</summary>
    Idle,

    /// <summary>Button down, not yet moved far enough to be a drag.</summary>
    Pressed,

    /// <summary>A drag candidate whose modifiers don't match any wheel.</summary>
    Dragging,

    /// <summary>A drag candidate with a matching modifier combination: the catcher should follow the cursor.</summary>
    Armed,
}

/// <summary>What the drag monitor should do after an input event.</summary>
public enum DragSignalKind
{
    None,

    /// <summary>A drag with matching modifiers started (or the modifiers now match): show the catcher.</summary>
    Armed,

    /// <summary>Still armed; the cursor moved.</summary>
    Moved,

    /// <summary>Still armed, but a different mode's modifiers are held now.</summary>
    ModeChanged,

    /// <summary>The modifiers no longer match: hide the catcher.</summary>
    Disarmed,

    /// <summary>The button was released after a drag (armed or not): hide everything shortly.</summary>
    Released,
}

/// <param name="Drag">Which press/drag/release cycle this belongs to (increases with every button press), so the host
/// can remember per-drag decisions without relying on seeing every signal.</param>
/// <param name="Origin">Where the button went down: the drag source is the window there, whereas <paramref name="Point"/>
/// may already be over another window (Shift pressed late in the drag).</param>
public readonly record struct DragSignal(DragSignalKind Kind, ScreenPoint Point, WheelMode? Mode = null, int Drag = 0, ScreenPoint Origin = default)
{
    public static DragSignal None(ScreenPoint point) => new(DragSignalKind.None, point);
}

/// <summary>
/// Pure state machine behind the global drag monitor. Feed it primary-button and mouse events (plus periodic
/// modifier ticks); a press becomes a drag candidate once the pointer moves beyond the threshold while held, and an
/// armed candidate once the held modifiers exactly match a wheel binding. Not thread-safe: one thread drives it.
/// </summary>
public sealed class DragDetector
{
    ScreenPoint _origin;
    int _drag;

    /// <param name="thresholdX">Horizontal distance (exclusive) that turns a press into a drag; Windows uses 2 × SM_CXDRAG.</param>
    /// <param name="thresholdY">Vertical distance; 2 × SM_CYDRAG.</param>
    public DragDetector(int thresholdX = 8, int thresholdY = 8, ModifierBindings? bindings = null)
    {
        ThresholdX = Math.Max(1, thresholdX);
        ThresholdY = Math.Max(1, thresholdY);
        Bindings = bindings ?? ModifierBindings.Default;
    }

    public int ThresholdX { get; set; }

    public int ThresholdY { get; set; }

    public ModifierBindings Bindings { get; set; }

    public DragPhase Phase { get; private set; } = DragPhase.Idle;

    /// <summary>The armed mode while <see cref="Phase"/> is <see cref="DragPhase.Armed"/>.</summary>
    public WheelMode? Mode { get; private set; }

    /// <summary>Number of the current (or last) press/drag/release cycle; see <see cref="DragSignal.Drag"/>.</summary>
    public int DragNumber => _drag;

    /// <summary>Where the current (or last) press happened.</summary>
    public ScreenPoint Origin => _origin;

    public DragSignal ButtonDown(ScreenPoint point)
    {
        _drag++;
        _origin = point;
        Phase = DragPhase.Pressed;
        Mode = null;
        return DragSignal.None(point);
    }

    public DragSignal Move(ScreenPoint point, TriggerModifiers held)
    {
        switch (Phase)
        {
            case DragPhase.Idle:
                return DragSignal.None(point);
            case DragPhase.Pressed:
                if (Math.Abs(point.X - _origin.X) <= ThresholdX && Math.Abs(point.Y - _origin.Y) <= ThresholdY)
                    return DragSignal.None(point);
                Phase = DragPhase.Dragging;
                return Evaluate(point, held, moved: true);
            default:
                return Evaluate(point, held, moved: true);
        }
    }

    /// <summary>Periodic check while the button is held, so pressing Shift mid-drag works without moving the mouse.</summary>
    public DragSignal Tick(ScreenPoint point, TriggerModifiers held) =>
        Phase is DragPhase.Dragging or DragPhase.Armed ? Evaluate(point, held, moved: false) : DragSignal.None(point);

    public DragSignal ButtonUp(ScreenPoint point)
    {
        var wasDragging = Phase is DragPhase.Dragging or DragPhase.Armed;
        var mode = Mode;
        Reset();
        return wasDragging ? new DragSignal(DragSignalKind.Released, point, mode, _drag, _origin) : DragSignal.None(point);
    }

    /// <summary>
    /// Ends the current cycle because input was lost (the hook was reinstalled, the button went up unseen). Like a
    /// button-up it reports <see cref="DragSignalKind.Released"/> for a drag in progress, so nothing the drag showed
    /// (the catcher) can stay on screen.
    /// </summary>
    public DragSignal Abort(ScreenPoint point) => ButtonUp(point);

    public void Reset()
    {
        Phase = DragPhase.Idle;
        Mode = null;
    }

    DragSignal Evaluate(ScreenPoint point, TriggerModifiers held, bool moved)
    {
        var match = Bindings.Match(held);
        if (Phase == DragPhase.Armed)
        {
            if (match is null)
            {
                Phase = DragPhase.Dragging;
                Mode = null;
                return new DragSignal(DragSignalKind.Disarmed, point, null, _drag, _origin);
            }
            if (match != Mode)
            {
                Mode = match;
                return new DragSignal(DragSignalKind.ModeChanged, point, match, _drag, _origin);
            }
            return moved ? new DragSignal(DragSignalKind.Moved, point, match, _drag, _origin) : DragSignal.None(point);
        }

        if (match is null)
            return DragSignal.None(point);
        Phase = DragPhase.Armed;
        Mode = match;
        return new DragSignal(DragSignalKind.Armed, point, match, _drag, _origin);
    }
}
