using System.Windows;
using Clementine.Core;
using Clementine.Core.Hosting;

namespace Clementine.Wheel.Tests;

public class DragDetectorTests
{
    static readonly ScreenPoint Origin = new(500, 500);
    const TriggerModifiers Shift = TriggerModifiers.Shift;
    const TriggerModifiers None = TriggerModifiers.None;

    static DragDetector Pressed()
    {
        var detector = new DragDetector(8, 8);
        detector.ButtonDown(Origin);
        return detector;
    }

    static ScreenPoint Offset(int dx, int dy) => new(Origin.X + dx, Origin.Y + dy);

    [Fact]
    public void A_click_never_signals()
    {
        var detector = new DragDetector();
        Assert.Equal(DragSignalKind.None, detector.ButtonDown(Origin).Kind);
        Assert.Equal(DragPhase.Pressed, detector.Phase);
        Assert.Equal(DragSignalKind.None, detector.ButtonUp(Origin).Kind);
        Assert.Equal(DragPhase.Idle, detector.Phase);
    }

    [Fact]
    public void Movement_within_the_threshold_is_not_a_drag()
    {
        var detector = Pressed();
        Assert.Equal(DragSignalKind.None, detector.Move(Offset(8, -8), Shift).Kind);
        Assert.Equal(DragPhase.Pressed, detector.Phase);
        Assert.Equal(DragSignalKind.None, detector.ButtonUp(Offset(8, -8)).Kind);
    }

    [Fact]
    public void Shift_drag_beyond_the_threshold_arms_the_convert_wheel()
    {
        var detector = Pressed();
        var signal = detector.Move(Offset(9, 0), Shift);
        Assert.Equal(DragSignalKind.Armed, signal.Kind);
        Assert.Equal(WheelMode.Convert, signal.Mode);
        Assert.Equal(Offset(9, 0), signal.Point);
        Assert.Equal(DragPhase.Armed, detector.Phase);

        var moved = detector.Move(Offset(40, 30), Shift);
        Assert.Equal(DragSignalKind.Moved, moved.Kind);
        Assert.Equal(Offset(40, 30), moved.Point);
    }

    [Theory]
    [InlineData(TriggerModifiers.Shift | TriggerModifiers.Control, WheelMode.Tools)]
    [InlineData(TriggerModifiers.Shift | TriggerModifiers.Alt, WheelMode.Actions)]
    public void Default_bindings_pick_the_mode(TriggerModifiers held, WheelMode mode)
    {
        var detector = Pressed();
        var signal = detector.Move(Offset(0, 20), held);
        Assert.Equal(DragSignalKind.Armed, signal.Kind);
        Assert.Equal(mode, signal.Mode);
    }

    [Theory]
    [InlineData(TriggerModifiers.None)]
    [InlineData(TriggerModifiers.Control)]
    [InlineData(TriggerModifiers.Shift | TriggerModifiers.Windows)]
    [InlineData(TriggerModifiers.Shift | TriggerModifiers.Control | TriggerModifiers.Alt)]
    public void Only_an_exact_match_arms(TriggerModifiers held)
    {
        var detector = Pressed();
        Assert.Equal(DragSignalKind.None, detector.Move(Offset(30, 0), held).Kind);
        Assert.Equal(DragPhase.Dragging, detector.Phase);
    }

    [Fact]
    public void Pressing_shift_mid_drag_arms_on_the_next_tick_without_moving()
    {
        var detector = Pressed();
        detector.Move(Offset(30, 0), None);
        Assert.Equal(DragSignalKind.None, detector.Tick(Offset(30, 0), None).Kind);
        var signal = detector.Tick(Offset(30, 0), Shift);
        Assert.Equal(DragSignalKind.Armed, signal.Kind);
        Assert.Equal(DragSignalKind.None, detector.Tick(Offset(30, 0), Shift).Kind);
    }

    [Fact]
    public void Changing_modifiers_while_armed_switches_mode_then_disarms()
    {
        var detector = Pressed();
        detector.Move(Offset(30, 0), Shift);
        var tools = detector.Tick(Offset(30, 0), Shift | TriggerModifiers.Control);
        Assert.Equal(DragSignalKind.ModeChanged, tools.Kind);
        Assert.Equal(WheelMode.Tools, tools.Mode);
        Assert.Equal(WheelMode.Tools, detector.Mode);

        var released = detector.Move(Offset(31, 0), None);
        Assert.Equal(DragSignalKind.Disarmed, released.Kind);
        Assert.Equal(DragPhase.Dragging, detector.Phase);

        Assert.Equal(DragSignalKind.Armed, detector.Move(Offset(32, 0), Shift).Kind);
    }

    [Fact]
    public void Button_up_after_any_drag_reports_released()
    {
        var armed = Pressed();
        armed.Move(Offset(30, 0), Shift);
        var signal = armed.ButtonUp(Offset(30, 0));
        Assert.Equal(DragSignalKind.Released, signal.Kind);
        Assert.Equal(WheelMode.Convert, signal.Mode);
        Assert.Equal(DragPhase.Idle, armed.Phase);

        var plain = Pressed();
        plain.Move(Offset(30, 0), None);
        Assert.Equal(DragSignalKind.Released, plain.ButtonUp(Offset(30, 0)).Kind);
    }

    [Fact]
    public void Events_while_idle_are_ignored()
    {
        var detector = new DragDetector();
        Assert.Equal(DragSignalKind.None, detector.Move(Origin, Shift).Kind);
        Assert.Equal(DragSignalKind.None, detector.Tick(Origin, Shift).Kind);
        Assert.Equal(DragSignalKind.None, detector.ButtonUp(Origin).Kind);
    }

    [Fact]
    public void A_new_press_restarts_from_its_own_origin()
    {
        var detector = Pressed();
        detector.Move(Offset(50, 0), Shift);
        detector.ButtonDown(Offset(50, 0));
        Assert.Equal(DragPhase.Pressed, detector.Phase);
        Assert.Null(detector.Mode);
        Assert.Equal(DragSignalKind.None, detector.Move(Offset(55, 0), Shift).Kind);
    }

    [Fact]
    public void Custom_bindings_and_thresholds_are_honoured()
    {
        var bindings = new ModifierBindings(TriggerModifiers.Control | TriggerModifiers.Alt, TriggerModifiers.None, TriggerModifiers.Windows);
        var detector = new DragDetector(2, 20, bindings);
        detector.ButtonDown(Origin);
        Assert.Equal(DragSignalKind.None, detector.Move(Offset(0, 20), TriggerModifiers.Control | TriggerModifiers.Alt).Kind);
        var signal = detector.Move(Offset(3, 0), TriggerModifiers.Control | TriggerModifiers.Alt);
        Assert.Equal(WheelMode.Convert, signal.Mode);
        Assert.Equal(DragSignalKind.ModeChanged, detector.Move(Offset(5, 0), TriggerModifiers.Windows).Kind);
        Assert.Equal(WheelMode.Actions, detector.Mode);
    }
}

public class ModifierBindingsTests
{
    [Fact]
    public void Defaults_match_the_spec()
    {
        var bindings = ModifierBindings.From(new ClementineSettings());
        Assert.Equal(WheelMode.Convert, bindings.Match(TriggerModifiers.Shift));
        Assert.Equal(WheelMode.Tools, bindings.Match(TriggerModifiers.Shift | TriggerModifiers.Control));
        Assert.Equal(WheelMode.Actions, bindings.Match(TriggerModifiers.Shift | TriggerModifiers.Alt));
        Assert.Null(bindings.Match(TriggerModifiers.None));
        Assert.False(bindings.IsEmpty);
    }

    [Fact]
    public void None_bindings_are_disabled_and_conflicts_resolve_in_order()
    {
        var bindings = new ModifierBindings(TriggerModifiers.None, TriggerModifiers.Shift, TriggerModifiers.Shift);
        Assert.Null(bindings.Match(TriggerModifiers.None));
        Assert.Equal(WheelMode.Tools, bindings.Match(TriggerModifiers.Shift));
        Assert.True(new ModifierBindings(TriggerModifiers.None, TriggerModifiers.None, TriggerModifiers.None).IsEmpty);
    }

    [Theory]
    [InlineData(DragDropKeyStates.ShiftKey | DragDropKeyStates.LeftMouseButton, false, TriggerModifiers.Shift)]
    [InlineData(DragDropKeyStates.ShiftKey | DragDropKeyStates.ControlKey, false, TriggerModifiers.Shift | TriggerModifiers.Control)]
    [InlineData(DragDropKeyStates.AltKey | DragDropKeyStates.ShiftKey, true, TriggerModifiers.Shift | TriggerModifiers.Alt | TriggerModifiers.Windows)]
    [InlineData(DragDropKeyStates.LeftMouseButton, false, TriggerModifiers.None)]
    public void Ole_key_states_map_to_modifiers(DragDropKeyStates states, bool win, TriggerModifiers expected) =>
        Assert.Equal(expected, ModifierBindings.FromKeyStates(states, win));

    [Theory]
    [InlineData(true, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link, DragDropEffects.Copy)]
    [InlineData(true, DragDropEffects.Move, DragDropEffects.None)]
    [InlineData(false, DragDropEffects.Copy | DragDropEffects.Move, DragDropEffects.None)]
    [InlineData(true, DragDropEffects.All, DragDropEffects.Copy)]
    public void Drops_are_copy_or_nothing_never_move(bool accept, DragDropEffects allowed, DragDropEffects expected)
    {
        var effect = DropEffects.CopyOrNone(accept, allowed);
        Assert.Equal(expected, effect);
        Assert.Equal(DragDropEffects.None, effect & DragDropEffects.Move);
    }
}

public class HotkeyGestureTests
{
    [Theory]
    [InlineData("Ctrl+Alt+Space", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x20, "Ctrl+Alt+Space")]
    [InlineData("ctrl + shift + k", HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x4B, "Ctrl+Shift+K")]
    [InlineData("Win+Shift+F12", HotkeyModifiers.Windows | HotkeyModifiers.Shift, 0x7B, "Shift+Win+F12")]
    [InlineData("Alt+1", HotkeyModifiers.Alt, 0x31, "Alt+1")]
    [InlineData("Control+Alt+PgDn", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x22, "Ctrl+Alt+PageDown")]
    [InlineData("Ctrl++", HotkeyModifiers.Control, 0xBB, "Ctrl+Plus")]
    [InlineData("F13", HotkeyModifiers.None, 0x7C, "F13")]
    [InlineData("Ctrl+Alt+NumPad5", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x65, "Ctrl+Alt+NumPad5")]
    [InlineData("Ctrl+Alt+/", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0xBF, "Ctrl+Alt+/")]
    public void Parses_and_normalises(string text, HotkeyModifiers modifiers, int vk, string normalised)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture));
        Assert.Equal(modifiers, gesture.Modifiers);
        Assert.Equal(vk, gesture.VirtualKey);
        Assert.Equal(normalised, gesture.ToString());
        Assert.True(HotkeyGesture.TryParse(gesture.ToString(), out var again));
        Assert.Equal(gesture, again);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Alt")]
    [InlineData("Ctrl+Alt+Banana")]
    [InlineData("Ctrl+A+B")]
    [InlineData("Shift+A")]
    [InlineData("Space")]
    [InlineData("+")]
    public void Rejects_unusable_gestures(string? text) => Assert.False(HotkeyGesture.TryParse(text, out _));
}
