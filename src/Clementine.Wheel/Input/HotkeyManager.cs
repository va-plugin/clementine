using System.Runtime.InteropServices;
using System.Windows.Interop;
using Clementine.Core;

namespace Clementine.Wheel;

/// <summary>
/// Owns one global hotkey (<c>RegisterHotKey</c>) on a message-only window of the UI thread and raises
/// <see cref="Pressed"/> there. Re-registering replaces the previous gesture.
/// </summary>
internal sealed class HotkeyManager : IDisposable
{
    const int HotkeyId = 0x4C43; // "CL"
    const uint MOD_NOREPEAT = 0x4000;

    readonly ILog _log;
    HwndSource? _window;
    HotkeyGesture? _registered;

    public HotkeyManager(ILog log) => _log = log;

    /// <summary>Raised on the UI thread when the hotkey is pressed.</summary>
    public event Action? Pressed;

    public HotkeyGesture? Registered => _registered;

    /// <summary>Why the last <see cref="Register"/> failed (shown in Settings), or null.</summary>
    public string? Problem { get; private set; }

    /// <summary>Registers <paramref name="gesture"/>, replacing any previous one. Returns false (and logs) if another app owns it.</summary>
    public bool Register(HotkeyGesture gesture)
    {
        if (_registered == gesture)
            return true;
        Unregister();

        _window ??= CreateWindow();
        if (!NativeMethods.RegisterHotKey(_window.Handle, HotkeyId, (uint)gesture.Modifiers | MOD_NOREPEAT, (uint)gesture.VirtualKey))
        {
            var error = Marshal.GetLastWin32Error();
            Problem = error == 1409
                ? $"{gesture} is already used by another app — pick a different shortcut."
                : $"Couldn't register {gesture} (error {error}).";
            _log.Warn($"Selection hotkey: {Problem}");
            return false;
        }
        _registered = gesture;
        Problem = null;
        return true;
    }

    public void Unregister()
    {
        Problem = null;
        if (_registered is null || _window is null)
            return;
        NativeMethods.UnregisterHotKey(_window.Handle, HotkeyId);
        _registered = null;
    }

    public void Dispose()
    {
        Unregister();
        if (_window is not null)
        {
            _window.RemoveHook(WndProc);
            _window.Dispose();
            _window = null;
        }
    }

    HwndSource CreateWindow()
    {
        var parameters = new HwndSourceParameters("Clementine hotkey")
        {
            ParentWindow = NativeMethods.HWND_MESSAGE,
            WindowStyle = 0,
            Width = 0,
            Height = 0,
        };
        var source = new HwndSource(parameters);
        source.AddHook(WndProc);
        return source;
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            try
            {
                Pressed?.Invoke();
            }
            catch (Exception ex)
            {
                _log.Warn("Selection hotkey handler failed", ex);
            }
        }
        return IntPtr.Zero;
    }
}
