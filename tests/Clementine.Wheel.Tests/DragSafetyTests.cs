using System.Windows.Threading;
using Clementine.Core;
using Clementine.Core.Hosting;
using Clementine.TestSupport;

namespace Clementine.Wheel.Tests;

/// <summary>
/// The safety rules around the Shift-drag catcher: it never appears for drags Windows won't deliver to us, never
/// outlives the drag, and never appears while the wheel is paused or switched off.
/// </summary>
public class DragSafetyTests
{
    static readonly ScreenPoint Origin = new(40, 40);
    static readonly ScreenPoint Elsewhere = new(90, 60);

    static DragSignal Signal(DragSignalKind kind, int drag, ScreenPoint? point = null) =>
        new(kind, point ?? Elsewhere, kind is DragSignalKind.Disarmed ? null : WheelMode.Convert, drag, Origin);

    /// <summary>A host whose hotkey is off (so the user's real hotkey is never taken) and whose button state is scripted.</summary>
    static (WheelHost Host, Func<bool> ButtonDown, Action<bool> SetButton) NewHost(SettingsStore? settings = null)
    {
        settings ??= SettingsStore.InMemory(new ClementineSettings { SelectionHotkey = "" });
        var host = new WheelHost(HostServices.Create([], settings, new RecordingLog()));
        var down = true;
        host.IsButtonDown = () => down;
        host.CatcherBlocker = _ => null;
        return (host, () => down, value => down = value);
    }

    /// <summary>Runs the dispatcher (timers included) for a while.</summary>
    static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    [Fact]
    public void Signals_carry_the_press_origin_so_the_source_window_is_checked_not_the_window_under_the_pointer()
    {
        var detector = new DragDetector(8, 8);
        detector.ButtonDown(Origin);
        Assert.Equal(Origin, detector.Origin);
        detector.Move(new ScreenPoint(Origin.X + 300, Origin.Y), TriggerModifiers.None);
        // Shift pressed late, far from where the drag started.
        var armed = detector.Tick(new ScreenPoint(Origin.X + 300, Origin.Y), TriggerModifiers.Shift);
        Assert.Equal(DragSignalKind.Armed, armed.Kind);
        Assert.Equal(Origin, armed.Origin);
        Assert.Equal(Origin.X + 300, armed.Point.X);
        Assert.Equal(Origin, detector.Move(new ScreenPoint(Origin.X + 310, Origin.Y), TriggerModifiers.Shift).Origin);
        Assert.Equal(Origin, detector.ButtonUp(new ScreenPoint(Origin.X + 310, Origin.Y)).Origin);
    }

    [Fact]
    public void The_blocker_hides_the_catcher_for_that_whole_drag_only_and_sees_where_it_started()
    {
        Sta.Run(() =>
        {
            var (host, _, _) = NewHost();
            var asked = new List<DragSignal>();
            host.CatcherBlocker = signal =>
            {
                asked.Add(signal);
                return signal.Drag == 1 ? "the source is elevated" : null;
            };
            try
            {
                host.Start();
                host.OnDragSignal(Signal(DragSignalKind.Armed, 1));
                Assert.True(host.CatcherWindow?.IsVisible != true);
                host.OnDragSignal(Signal(DragSignalKind.Disarmed, 1));
                host.OnDragSignal(Signal(DragSignalKind.Armed, 1));
                Assert.True(host.CatcherWindow?.IsVisible != true, "Re-arming the same drag must not bring the catcher back.");
                Assert.Single(asked);
                Assert.Equal(Origin, asked[0].Origin);
                host.OnDragSignal(Signal(DragSignalKind.Released, 1));

                host.OnDragSignal(Signal(DragSignalKind.Armed, 2));
                Assert.True(host.CatcherWindow?.IsVisible == true, "The next drag is checked afresh.");
                host.OnDragSignal(Signal(DragSignalKind.Released, 2));
                Assert.False(host.CatcherWindow!.IsVisible);
            }
            finally
            {
                host.Stop();
            }
        });
    }

    [Fact]
    public void The_watchdog_hides_the_catcher_when_the_button_is_up_even_without_a_release_signal()
    {
        Sta.Run(() =>
        {
            var (host, _, setButton) = NewHost();
            try
            {
                host.Start();
                host.OnDragSignal(Signal(DragSignalKind.Armed, 7));
                Assert.True(host.CatcherWindow?.IsVisible == true);
                Pump(TimeSpan.FromMilliseconds(350));
                Assert.True(host.CatcherWindow!.IsVisible, "Still dragging: the catcher stays.");

                setButton(false); // the button-up never reached the hook
                Pump(TimeSpan.FromMilliseconds(450));
                Assert.False(host.CatcherWindow.IsVisible);
            }
            finally
            {
                host.Stop();
            }
        });
    }

    [Fact]
    public void No_catcher_while_the_wheel_is_paused_or_switched_off()
    {
        Sta.Run(() =>
        {
            // Never started (the shell pauses with Stop; the hook may still run to watch a click-mode wheel).
            var (idle, _, _) = NewHost();
            idle.OnDragSignal(Signal(DragSignalKind.Armed, 1));
            Assert.True(idle.CatcherWindow?.IsVisible != true);
            idle.Stop();

            var settings = SettingsStore.InMemory(new ClementineSettings { SelectionHotkey = "" });
            var (host, _, _) = NewHost(settings);
            try
            {
                host.Start();
                host.Stop(); // paused from the tray
                host.OnDragSignal(Signal(DragSignalKind.Armed, 2));
                Assert.True(host.CatcherWindow?.IsVisible != true);

                host.Start();
                settings.Update(s => s.WheelEnabled = false);
                host.ApplySettings();
                host.OnDragSignal(Signal(DragSignalKind.Armed, 3));
                Assert.True(host.CatcherWindow?.IsVisible != true);
            }
            finally
            {
                host.Stop();
            }
        });
    }

    static readonly System.Reflection.ConstructorInfo DragArgs = typeof(System.Windows.DragEventArgs).GetConstructor(
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
        [typeof(System.Windows.IDataObject), typeof(System.Windows.DragDropKeyStates), typeof(System.Windows.DragDropEffects),
            typeof(System.Windows.DependencyObject), typeof(System.Windows.Point)])!;

    static System.Windows.DragDropEffects RaiseOn(CatcherWindow catcher, System.Windows.RoutedEvent routed,
        System.Windows.DragDropEffects allowed, System.Windows.IDataObject data)
    {
        var args = (System.Windows.DragEventArgs)DragArgs.Invoke([data, System.Windows.DragDropKeyStates.LeftMouseButton | System.Windows.DragDropKeyStates.ShiftKey,
            allowed, catcher, new System.Windows.Point(48, 48)]);
        args.RoutedEvent = routed;
        catcher.RaiseEvent(args);
        return args.Effects;
    }

    [Fact]
    public void The_catcher_only_ever_answers_copy_or_none_including_forwarded_drops()
    {
        Sta.Run(() =>
        {
            using var ws = new TestWorkspace("catcher");
            var files = new System.Windows.DataObject(System.Windows.DataFormats.FileDrop, new[] { ws.WriteText("clip.mp4", "x") });
            var text = new System.Windows.DataObject(System.Windows.DataFormats.UnicodeText, "just text");
            const System.Windows.DragDropEffects all = System.Windows.DragDropEffects.Copy | System.Windows.DragDropEffects.Move | System.Windows.DragDropEffects.Link;
            const System.Windows.DragDropEffects moveOnly = System.Windows.DragDropEffects.Move;
            var catcher = new CatcherWindow(new RecordingLog());
            var forwarded = System.Windows.DragDropEffects.Move | System.Windows.DragDropEffects.Link;
            catcher.Dropped += e => e.Effects = forwarded; // a misbehaving forward target
            var entered = 0;
            var foreign = 0;
            catcher.FilesEntered += (_, _) => entered++;
            catcher.ForeignDragEntered += () => foreign++;
            try
            {
                // Hidden (stepped aside): never accepts, never opens a wheel.
                Assert.Equal(System.Windows.DragDropEffects.None, RaiseOn(catcher, System.Windows.DragDrop.DragEnterEvent, all, files));

                catcher.ShowAt(new ScreenPoint(4, 4));
                Assert.Equal(System.Windows.DragDropEffects.Copy, RaiseOn(catcher, System.Windows.DragDrop.DragEnterEvent, all, files));
                Assert.Equal(System.Windows.DragDropEffects.None, RaiseOn(catcher, System.Windows.DragDrop.DragEnterEvent, moveOnly, files));
                Assert.Equal(System.Windows.DragDropEffects.None, RaiseOn(catcher, System.Windows.DragDrop.DragEnterEvent, all, text));
                Assert.Equal(System.Windows.DragDropEffects.Copy, RaiseOn(catcher, System.Windows.DragDrop.DragOverEvent, all, files));
                Assert.Equal(System.Windows.DragDropEffects.None, RaiseOn(catcher, System.Windows.DragDrop.DragOverEvent, moveOnly, files));

                Assert.Equal(System.Windows.DragDropEffects.None, RaiseOn(catcher, System.Windows.DragDrop.DropEvent, all, files));
                forwarded = System.Windows.DragDropEffects.Copy | System.Windows.DragDropEffects.Move;
                Assert.Equal(System.Windows.DragDropEffects.Copy, RaiseOn(catcher, System.Windows.DragDrop.DropEvent, all, files));
                Assert.Equal(System.Windows.DragDropEffects.None, RaiseOn(catcher, System.Windows.DragDrop.DropEvent, moveOnly, files));

                Pump(TimeSpan.FromMilliseconds(50));
                Assert.Equal(1, entered); // only the visible DragEnter with copyable files
                Assert.Equal(2, foreign); // the Move-only source and the text drag: the catcher steps aside
            }
            finally
            {
                catcher.Close();
            }
        });
    }

    [Theory]
    [InlineData(DragSource.Medium, DragSource.Medium, true)]
    [InlineData(DragSource.Low, DragSource.Medium, true)]       // browser sandbox → us
    [InlineData(DragSource.High, DragSource.Medium, false)]     // elevated app → us: UIPI drops it
    [InlineData(DragSource.System, DragSource.Medium, false)]
    [InlineData(DragSource.Unknown, DragSource.Medium, false)]  // can't read it: almost always elevated
    [InlineData(DragSource.Medium, DragSource.High, false)]     // elevated Clementine never gets Explorer's drops
    [InlineData(DragSource.High, DragSource.High, true)]
    [InlineData(DragSource.High, DragSource.Unknown, true)]     // our own level unknown: don't block anything
    [InlineData(DragSource.Medium + 0x100, DragSource.Medium, true)] // Medium Plus is still Medium
    public void Integrity_levels_decide_whether_a_drop_can_reach_us(int source, int own, bool expected) =>
        Assert.Equal(expected, DragSource.CanReach(source, own));

    [Fact]
    public void Exclusive_full_screen_only_blocks_the_monitor_it_covers()
    {
        var primary = new PixelRect(0, 0, 1920, 1080);
        var projector = new PixelRect(1920, 0, 3840, 1080);
        var game = new PixelRect(1920, 0, 3840, 1080);
        Assert.True(DragSource.FullScreenBlocks(game, "UnityWndClass", projector));
        Assert.False(DragSource.FullScreenBlocks(game, "UnityWndClass", primary));
        // Explorer in front (the user is dragging from it): the full-screen app is elsewhere or not exclusive any more.
        Assert.False(DragSource.FullScreenBlocks(new PixelRect(100, 100, 1200, 900), "CabinetWClass", primary));
        // The desktop spans every monitor but is never a full-screen app.
        Assert.False(DragSource.FullScreenBlocks(new PixelRect(0, 0, 3840, 1080), "WorkerW", primary));
        Assert.False(DragSource.FullScreenBlocks(new PixelRect(0, 0, 3840, 1080), "Progman", projector));
        // No foreground window to look at: stay out of the way, as before.
        Assert.True(DragSource.FullScreenBlocks(null, "", primary));
    }

    [Fact]
    public void Source_checks_on_the_real_desktop_never_throw()
    {
        Assert.NotEqual(DragSource.Unknown, DragSource.OwnIntegrity);
        var cursor = NativeMethods.CursorPosition();
        var reason = DragSource.ReasonToStayHidden(cursor, cursor);
        Assert.True(reason is null || reason.Length > 0);
        // Off every monitor: falls back to the foreground window.
        var far = DragSource.ReasonToStayHidden(new ScreenPoint(-1_000_000, -1_000_000), cursor);
        Assert.True(far is null || far.Length > 0);
        // Our own windows always reach us.
        Assert.Equal(DragSource.OwnIntegrity, DragSource.IntegrityOfProcess((uint)Environment.ProcessId));
    }

    [Fact]
    public void Release_watch_needs_consecutive_button_up_polls()
    {
        var watch = new ReleaseWatch(3);
        Assert.False(watch.Poll(buttonDown: false));
        Assert.False(watch.Poll(buttonDown: false));
        Assert.False(watch.Poll(buttonDown: true)); // pressed again: start over
        Assert.False(watch.Poll(buttonDown: false));
        Assert.False(watch.Poll(buttonDown: false));
        Assert.True(watch.Poll(buttonDown: false));
        watch.Reset();
        Assert.False(watch.Poll(buttonDown: false));
        Assert.Equal(1, new ReleaseWatch(0).Required);
    }
}
