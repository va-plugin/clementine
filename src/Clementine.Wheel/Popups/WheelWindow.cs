using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Clementine.Core;
using Clementine.Core.Hosting;

namespace Clementine.Wheel;

/// <summary>
/// The window that hosts a <see cref="WheelView"/>. In <see cref="WheelInteraction.Drag"/> mode it never activates,
/// follows the OLE drag (DragEnter/DragOver/Drop, always Copy or None) and switches mode live from the drag's key
/// states; in <see cref="WheelInteraction.Click"/> mode it takes focus and is driven by mouse and keyboard.
/// Every handler is exception-safe: a bug here must never break the user's drag.
/// </summary>
internal sealed class WheelWindow : PopupWindow
{
    static readonly TimeSpan MoreDwell = TimeSpan.FromMilliseconds(450);
    static readonly TimeSpan WatchdogInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>How often a pending aim switch is re-checked: OLE sends no DragOver while the mouse rests.</summary>
    static readonly TimeSpan AimInterval = TimeSpan.FromMilliseconds(25);

    readonly WheelInteraction _interaction;
    readonly ILog _log;
    readonly DispatcherTimer _dwell;
    readonly DispatcherTimer _watchdog;
    readonly DispatcherTimer _aim;
    readonly ReleaseWatch _release = new(4);
    ScreenPoint _centre;
    bool _dwellUsed;
    bool _closing;

    public WheelWindow(WheelInteraction interaction, ILog log)
        : base(activatable: interaction == WheelInteraction.Click)
    {
        _interaction = interaction;
        _log = log;
        Title = interaction == WheelInteraction.Click ? "Clementine" : "Clementine wheel";
        View = new WheelView { RenderFailed = ex => log.Warn("Drawing the wheel failed", ex) };
        Content = View;

        _dwell = new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = MoreDwell };
        _dwell.Tick += (_, _) => Guard(OnDwell);
        _watchdog = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = WatchdogInterval };
        _watchdog.Tick += (_, _) => Guard(OnWatchdog);
        _aim = new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = AimInterval };
        _aim.Tick += (_, _) => Guard(OnAimTick);

        if (interaction == WheelInteraction.Drag)
        {
            AllowDrop = true;
            DragEnter += (_, e) => GuardDrag(e, () => HandleDragOver(e));
            DragOver += (_, e) => GuardDrag(e, () => HandleDragOver(e));
            DragLeave += (_, e) => GuardDrag(e, HandleDragLeave);
            Drop += (_, e) => GuardDrag(e, () => HandleDrop(e));
        }
        else
        {
            MouseMove += (_, e) => Guard(() => Track(e.GetPosition(View)));
            MouseLeave += (_, _) => Guard(OnMouseLeave);
            MouseLeftButtonUp += (_, e) => Guard(() => OnClick(e));
            PreviewKeyDown += (_, e) => Guard(() => OnKey(e));
            Deactivated += (_, _) => Guard(Dismiss);
        }
    }

    public WheelView View { get; }

    public WheelSession? Session => View.Session;

    public WheelInteraction Interaction => _interaction;

    /// <summary>Device pixels per DIP of the monitor the wheel is on.</summary>
    public double PixelsPerDip => VisualTreeHelper.GetDpi(View).PixelsPerDip;

    /// <summary>Screen centre of the wheel in physical pixels.</summary>
    public ScreenPoint CentreOnScreen => _centre;

    /// <summary>Maps held modifiers to a mode (drag mode only); used for live mode switching.</summary>
    public Func<TriggerModifiers, WheelMode?>? MatchMode { get; set; }

    /// <summary>The user switched modifiers mid-drag; the owner should call <see cref="Replace"/> with a new session.</summary>
    public event Action<WheelMode>? ModeRequested;

    /// <summary>The user chose an operation (by drop, click or Enter). The window has already closed.</summary>
    public event Action<WheelOption, ScreenPoint>? Chosen;

    /// <summary>The wheel closed without a choice, or after one.</summary>
    public event Action? Dismissed;

    /// <summary>Shows <paramref name="session"/> centred on <paramref name="centre"/> (physical pixels) with the open animation.</summary>
    public void Open(WheelSession session, ScreenPoint centre)
    {
        _closing = false;
        _dwellUsed = false;
        _release.Reset();
        View.Show(session, _interaction);
        var metrics = session.Layout.Metrics;
        Width = Height = metrics.Size;
        View.Opacity = 0;
        if (!IsVisible)
            Show();
        _centre = PlaceCentred(centre, metrics.Size, metrics.OuterRingOuterRadius + 2);
        View.UpdateLayout();
        View.PlayOpenAnimation();

        if (_interaction == WheelInteraction.Click)
        {
            Activate();
            NativeMethods.SetForegroundWindow(Handle);
            Keyboard.Focus(this);
        }
        else
        {
            _watchdog.Start();
        }
    }

    /// <summary>Whether a screen point (physical pixels) lies on the wheel, including its pointer slack.</summary>
    public bool Covers(ScreenPoint point)
    {
        var scale = VisualTreeHelper.GetDpi(View).DpiScaleX;
        var m = View.Metrics;
        var radius = (m.OuterRingOuterRadius + m.PointerSlack) * scale;
        double dx = point.X - _centre.X;
        double dy = point.Y - _centre.Y;
        return dx * dx + dy * dy <= radius * radius;
    }

    /// <summary>Swaps in a new session (e.g. another mode) without moving the wheel.</summary>
    public void Replace(WheelSession session)
    {
        var sizeChanged = Math.Abs(session.Layout.Metrics.Size - View.Metrics.Size) > 0.5;
        _aim.Stop();
        View.Show(session, _interaction);
        if (sizeChanged)
            Resize();
    }

    /// <summary>Hides the wheel (it is reused for the next drag).</summary>
    public void Dismiss()
    {
        if (_closing || !IsVisible)
            return;
        _closing = true;
        _dwell.Stop();
        _watchdog.Stop();
        _aim.Stop();
        Session?.ClearHover();
        View.StopOpenAnimation();
        Hide();
        Dismissed?.Invoke();
    }

    /// <summary>A drop that the drag source delivered to the catcher instead of us (pointer never moved after we opened).</summary>
    public void AcceptForwardedDrop(DragEventArgs e)
    {
        if (!IsVisible || Session is null)
            return;
        var cursor = NativeMethods.CursorPosition();
        var local = View.PointFromScreen(new Point(cursor.X, cursor.Y));
        Track(local);
        CompleteDrop(e);
    }

    // ---------------------------------------------------------------- drag mode

    void HandleDragOver(DragEventArgs e)
    {
        if (Session is null || !DragData.HasFiles(e.Data))
            return;

        var held = ModifierBindings.FromKeyStates(e.KeyStates, NativeMethods.IsDown(NativeMethods.VK_LWIN) || NativeMethods.IsDown(NativeMethods.VK_RWIN));
        if (MatchMode?.Invoke(held) is { } mode && mode != Session.Model.Mode)
            ModeRequested?.Invoke(mode);

        Track(e.GetPosition(View));
        // What a drop here would run, even while the open fan is held (a drop settles to what is under the pointer):
        // OLE answers a release with Drop only if the last DragOver said Copy, and repeats no DragOver while the mouse rests.
        e.Effects = DropEffects.CopyOrNone(Session?.DropTarget is not null, e.AllowedEffects);
    }

    void HandleDragLeave()
    {
        _dwell.Stop();
        if (NativeMethods.IsDown(NativeMethods.VK_ESCAPE))
        {
            Dismiss();
            return;
        }
        PointerLeft();
    }

    void HandleDrop(DragEventArgs e)
    {
        if (Session is null)
            return;
        Track(e.GetPosition(View));
        CompleteDrop(e);
    }

    void CompleteDrop(DragEventArgs e)
    {
        // A drop takes what is under the pointer, even while the open fan was being held for an aimed move.
        Session?.Settle();
        var target = Session?.Target;
        e.Effects = DropEffects.CopyOrNone(target is not null, e.AllowedEffects);
        var at = NativeMethods.CursorPosition();
        Dismiss();
        if (target is not null && e.Effects == DragDropEffects.Copy)
            Chosen?.Invoke(target, at);
    }

    void OnWatchdog()
    {
        if (!IsVisible)
        {
            _watchdog.Stop();
            return;
        }
        if (NativeMethods.IsDown(NativeMethods.VK_ESCAPE))
        {
            Dismiss();
            return;
        }
        // Safety net in case the hook missed the button-up: give a pending Drop time to arrive first.
        if (_release.Poll(NativeMethods.IsPrimaryButtonDown()))
            Dismiss();
    }

    // ---------------------------------------------------------------- click mode

    void OnMouseLeave()
    {
        _dwell.Stop();
        PointerLeft();
    }

    void OnClick(MouseButtonEventArgs e)
    {
        e.Handled = true;
        Track(e.GetPosition(View));
        Commit(NativeMethods.CursorPosition());
    }

    void OnKey(KeyEventArgs e)
    {
        var session = Session;
        if (session is null)
            return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        WheelNavigation? move = key switch
        {
            Key.Right => WheelNavigation.Clockwise,
            Key.Left => WheelNavigation.CounterClockwise,
            Key.Up => WheelNavigation.Outward,
            Key.Down => WheelNavigation.Inward,
            Key.Tab => (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? WheelNavigation.PreviousSegment : WheelNavigation.NextSegment,
            _ => null,
        };
        if (move is { } navigation)
        {
            e.Handled = true;
            if (session.Navigate(navigation))
                View.Refresh();
            return;
        }
        switch (key)
        {
            case Key.Escape:
                e.Handled = true;
                Dismiss();
                break;
            case Key.Enter or Key.Space:
                e.Handled = true;
                Commit(_centre);
                break;
            case Key.PageDown or Key.PageUp:
                e.Handled = true;
                if (session.NextPage())
                    PageChanged();
                break;
        }
    }

    void Commit(ScreenPoint anchor)
    {
        var session = Session;
        if (session is null)
            return;
        if (session.Settle())
            View.Refresh();
        if (session.IsOverMore)
        {
            if (session.NextPage())
                PageChanged();
            return;
        }
        if (session.Target is { } target)
        {
            Dismiss();
            Chosen?.Invoke(target, anchor);
            return;
        }
        if (session.Hover.Kind is WheelHitKind.Centre or WheelHitKind.None)
            Dismiss();
        // Over an unavailable operation: stay open; the centre explains why.
    }

    // ---------------------------------------------------------------- shared

    void Track(Point position)
    {
        var session = Session;
        if (session is null)
            return;
        if (session.MovePointer(position - View.Centre))
            View.Refresh();
        UpdateTimers(session);
    }

    /// <summary>The pointer left the wheel: nothing is highlighted, the open fan stays a moment for an overshoot to come back.</summary>
    void PointerLeft()
    {
        if (Session is not { } session)
        {
            _aim.Stop();
            return;
        }
        if (session.PointerLeft())
            View.Refresh();
        UpdateTimers(session);
    }

    void UpdateTimers(WheelSession session)
    {
        if (session.NeedsTick)
        {
            if (!_aim.IsEnabled)
                _aim.Start();
        }
        else
        {
            _aim.Stop();
        }

        if (session.IsOverMore)
        {
            if (!_dwellUsed && !_dwell.IsEnabled)
                _dwell.Start();
        }
        else
        {
            _dwell.Stop();
            _dwellUsed = false;
        }
    }

    /// <summary>The pointer rests while the open fan is held (give way to what is under it once it has settled), or it left the wheel.</summary>
    void OnAimTick()
    {
        var session = Session;
        if (session is null)
        {
            _aim.Stop();
            return;
        }
        if (session.Tick())
            View.Refresh();
        UpdateTimers(session);
    }

    void OnDwell()
    {
        _dwell.Stop();
        var session = Session;
        if (session is null || !session.IsOverMore || _interaction == WheelInteraction.Click)
            return;
        if (session.NextPage())
        {
            _dwellUsed = true;
            PageChanged();
        }
    }

    void PageChanged()
    {
        var before = Width;
        View.Refresh();
        if (Session is { } session && Math.Abs(session.Layout.Metrics.Size - before) > 0.5)
            Resize();
    }

    void Resize()
    {
        if (Session is not { } session)
            return;
        var metrics = session.Layout.Metrics;
        Width = Height = metrics.Size;
        if (IsVisible)
            _centre = PlaceCentred(_centre, metrics.Size, metrics.OuterRingOuterRadius + 2);
    }

    protected override void OnClosed(EventArgs e)
    {
        // WheelHost.Stop closes the windows outright: no timer may keep ticking on (and rooting) a closed window.
        _dwell.Stop();
        _watchdog.Stop();
        _aim.Stop();
        base.OnClosed(e);
    }

    void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _log.Warn("Wheel window handler failed", ex);
        }
    }

    void GuardDrag(DragEventArgs e, Action action)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        try
        {
            action();
        }
        catch (Exception ex)
        {
            e.Effects = DragDropEffects.None;
            _log.Warn("Wheel drag handler failed", ex);
        }
        // Belt and braces: only Copy or None ever goes back to the source, whatever happened above.
        e.Effects = DropEffects.Sanitize(e.Effects, e.AllowedEffects);
    }
}
