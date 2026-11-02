using System.Windows;
using System.Windows.Interop;
using static Clementine.App.Interop.NativeMethods;

namespace Clementine.App.Interop;

/// <summary>DWM and window-style tweaks: dark title bars, Mica, non-activating tool windows, reliable activation.</summary>
internal static class WindowEffects
{
    /// <summary>Dark caption + Mica backdrop for a standard framed window (Windows 11; harmless elsewhere).</summary>
    public static void UseDarkChrome(Window window)
    {
        void Apply()
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
                return;
            TrySetAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, 1);
            TrySetAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, DWMSBT_MAINWINDOW);
        }

        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
            Apply();
        else
            window.SourceInitialized += (_, _) => Apply();
    }

    /// <summary>
    /// Makes a borderless window a non-activating tool window: it never steals focus from the app the user is in
    /// and never appears in the taskbar or Alt-Tab.
    /// </summary>
    public static void MakeNoActivateToolWindow(Window window)
    {
        void Apply()
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
                return;
            var style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            style = (style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW;
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style));
        }

        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
            Apply();
        else
            window.SourceInitialized += (_, _) => Apply();
    }

    /// <summary>
    /// Brings a window to the foreground even when another app (Explorer, after a drop) currently owns it.
    /// Falls back to attaching to the foreground thread's input queue, the documented way to be allowed to.
    /// </summary>
    public static void ForceActivate(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            window.Activate();
            return;
        }
        if (IsIconic(hwnd))
            ShowWindow(hwnd, SW_RESTORE);
        if (SetForegroundWindow(hwnd))
        {
            window.Activate();
            return;
        }

        var foreground = GetForegroundWindow();
        var foregroundThread = GetWindowThreadProcessId(foreground, out _);
        var thisThread = GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != thisThread && AttachThreadInput(thisThread, foregroundThread, true);
        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
                AttachThreadInput(thisThread, foregroundThread, false);
        }
        window.Activate();
    }

    static void TrySetAttribute(IntPtr hwnd, int attribute, int value)
    {
        try
        {
            DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }
}
