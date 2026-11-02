using System.Windows;
using Clementine.Core;
using Clementine.Core.Hosting;

namespace Clementine.Wheel;

/// <summary>
/// Which held modifiers open which wheel. Matching is exact (Shift+Ctrl never matches a Shift binding) and a binding
/// of <see cref="TriggerModifiers.None"/> is disabled — otherwise every ordinary drag would open a wheel.
/// When two modes share a combination the first of Convert, Tools, Actions wins.
/// </summary>
public sealed record ModifierBindings(TriggerModifiers Convert, TriggerModifiers Tools, TriggerModifiers Actions)
{
    public static ModifierBindings Default { get; } = From(new ClementineSettings());

    public static ModifierBindings From(ClementineSettings settings) =>
        new(settings.ConvertModifiers, settings.ToolsModifiers, settings.ActionsModifiers);

    /// <summary>True when no mode can be triggered (the drag monitor then doesn't need its hook).</summary>
    public bool IsEmpty => Convert == TriggerModifiers.None && Tools == TriggerModifiers.None && Actions == TriggerModifiers.None;

    public WheelMode? Match(TriggerModifiers held)
    {
        if (held == TriggerModifiers.None)
            return null;
        if (held == Convert)
            return WheelMode.Convert;
        if (held == Tools)
            return WheelMode.Tools;
        if (held == Actions)
            return WheelMode.Actions;
        return null;
    }

    /// <summary>Reads Shift/Ctrl/Alt/Win from the asynchronous key state (safe on any thread).</summary>
    public static TriggerModifiers ReadHeld()
    {
        var held = TriggerModifiers.None;
        if (NativeMethods.IsDown(NativeMethods.VK_SHIFT))
            held |= TriggerModifiers.Shift;
        if (NativeMethods.IsDown(NativeMethods.VK_CONTROL))
            held |= TriggerModifiers.Control;
        if (NativeMethods.IsDown(NativeMethods.VK_MENU))
            held |= TriggerModifiers.Alt;
        if (NativeMethods.IsDown(NativeMethods.VK_LWIN) || NativeMethods.IsDown(NativeMethods.VK_RWIN))
            held |= TriggerModifiers.Windows;
        return held;
    }

    /// <summary>Modifiers reported by an OLE drag (<see cref="DragEventArgs.KeyStates"/>); the Windows key comes from the key state.</summary>
    public static TriggerModifiers FromKeyStates(DragDropKeyStates keyStates, bool windowsKeyDown)
    {
        var held = TriggerModifiers.None;
        if ((keyStates & DragDropKeyStates.ShiftKey) != 0)
            held |= TriggerModifiers.Shift;
        if ((keyStates & DragDropKeyStates.ControlKey) != 0)
            held |= TriggerModifiers.Control;
        if ((keyStates & DragDropKeyStates.AltKey) != 0)
            held |= TriggerModifiers.Alt;
        if (windowsKeyDown)
            held |= TriggerModifiers.Windows;
        return held;
    }
}

/// <summary>Drop-effect rules shared by the catcher and the wheel.</summary>
public static class DropEffects
{
    /// <summary>
    /// Copy when we accept and the source allows it, otherwise None. Never Move: Shift usually means "move" in
    /// Explorer, and accepting a move can make the source delete the originals.
    /// </summary>
    public static DragDropEffects CopyOrNone(bool accept, DragDropEffects allowed) =>
        accept && (allowed & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : DragDropEffects.None;

    /// <summary>
    /// The last line of defence before an effect goes back to the drag source: exactly <see cref="DragDropEffects.Copy"/>
    /// when <paramref name="effects"/> includes Copy and the source allows it, otherwise None. Move, Link and Scroll
    /// bits never survive, whatever a handler set.
    /// </summary>
    public static DragDropEffects Sanitize(DragDropEffects effects, DragDropEffects allowed) =>
        CopyOrNone((effects & DragDropEffects.Copy) != 0, allowed);
}
