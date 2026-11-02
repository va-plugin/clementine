using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Clementine.Core.Hosting;

namespace Clementine.Wheel;

/// <summary>
/// Borderless, per-pixel-transparent, topmost tool window that never shows in the taskbar or Alt-Tab. Positioned in
/// physical pixels with <c>SetWindowPos</c> on the monitor under the target point, sized for that monitor's DPI.
/// Non-activatable windows also get <c>WS_EX_NOACTIVATE</c> so they never steal focus from a drag source.
/// </summary>
internal abstract class PopupWindow : Window
{
    readonly bool _activatable;

    protected PopupWindow(bool activatable)
    {
        _activatable = activatable;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        ShowActivated = activatable;
        SizeToContent = SizeToContent.Manual;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -32000;
        Top = -32000;
        Focusable = activatable;
        UseLayoutRounding = true;
        SourceInitialized += (_, _) => ApplyExtendedStyles();
    }

    /// <summary>The window handle (created on first use, without showing the window).</summary>
    public IntPtr Handle => new WindowInteropHelper(this).EnsureHandle();

    internal long ExtendedStyle => (long)NativeMethods.GetWindowLongPtr(Handle, NativeMethods.GWL_EXSTYLE);

    /// <summary>Work area and DPI scale (1.0 = 96 DPI) of the monitor nearest <paramref name="point"/>.</summary>
    internal static (PixelRect WorkArea, double Scale) MonitorAt(ScreenPoint point)
    {
        var monitor = NativeMethods.MonitorFromPoint(new NativeMethods.POINT(point.X, point.Y), NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        var work = NativeMethods.GetMonitorInfo(monitor, ref info)
            ? new PixelRect(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom)
            : new PixelRect(point.X - 2000, point.Y - 2000, point.X + 2000, point.Y + 2000);
        var scale = 1.0;
        try
        {
            if (NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0)
                scale = dpiX / 96.0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
        return (work, scale);
    }

    /// <summary>
    /// Centres the window (a square of <paramref name="sizeDip"/> DIPs) on <paramref name="centre"/>, nudged so a
    /// disc of <paramref name="keepInsideRadiusDip"/> stays inside the monitor's work area. Returns the actual
    /// centre in physical pixels.
    /// </summary>
    protected ScreenPoint PlaceCentred(ScreenPoint centre, double sizeDip, double keepInsideRadiusDip)
    {
        var hwnd = Handle;
        var (work, scale) = MonitorAt(centre);
        var sizePx = (int)Math.Round(sizeDip * scale);
        var actual = keepInsideRadiusDip > 0
            ? WheelPlacement.ClampCentre(centre, (int)Math.Ceiling(keepInsideRadiusDip * scale), work)
            : centre;
        var topLeft = WheelPlacement.TopLeft(actual, sizePx);
        // Twice: if the move crosses into a monitor with another DPI, WPF resizes the window while handling
        // WM_DPICHANGED during the first call; the second call restores the exact rectangle we want.
        for (var i = 0; i < 2; i++)
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, topLeft.X, topLeft.Y, sizePx, sizePx, NativeMethods.SWP_NOACTIVATE);
        return actual;
    }

    void ApplyExtendedStyles()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
        style |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TOPMOST;
        style &= ~NativeMethods.WS_EX_APPWINDOW;
        if (!_activatable)
            style |= NativeMethods.WS_EX_NOACTIVATE;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(style));
    }
}

/// <summary>Reads file paths from OLE drag data without ever throwing.</summary>
internal static class DragData
{
    public static IReadOnlyList<string>? Files(IDataObject? data)
    {
        if (data is null)
            return null;
        try
        {
            if (!data.GetDataPresent(DataFormats.FileDrop))
                return null;
            return data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths ? paths : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or OutOfMemoryException or ExternalException)
        {
            return null;
        }
    }

    public static bool HasFiles(IDataObject? data)
    {
        if (data is null)
            return false;
        try
        {
            return data.GetDataPresent(DataFormats.FileDrop);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ExternalException)
        {
            return false;
        }
    }
}
