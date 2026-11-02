using System.Windows;
using System.Windows.Interop;
using Clementine.Core.Hosting;
using static Clementine.App.Interop.NativeMethods;

namespace Clementine.App.Interop;

/// <summary>A monitor's work area in physical pixels plus its DPI scale (1.0 = 96 DPI).</summary>
internal readonly record struct MonitorArea(Int32Rect WorkArea, Int32Rect Bounds, double Scale);

/// <summary>Cursor and monitor queries, and pixel-exact window placement on per-monitor-DPI setups.</summary>
internal static class ScreenGeometry
{
    /// <summary>The cursor position in physical pixels.</summary>
    public static ScreenPoint CursorPosition() =>
        GetCursorPos(out var p) ? new ScreenPoint(p.X, p.Y) : new ScreenPoint(0, 0);

    /// <summary>The monitor that contains (or is nearest to) <paramref name="point"/>.</summary>
    public static MonitorArea MonitorAt(ScreenPoint point)
    {
        var monitor = MonitorFromPoint(new POINT { X = point.X, Y = point.Y }, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            var work = SystemParameters.WorkArea;
            return new MonitorArea(new Int32Rect((int)work.X, (int)work.Y, (int)work.Width, (int)work.Height),
                new Int32Rect(0, 0, (int)SystemParameters.PrimaryScreenWidth, (int)SystemParameters.PrimaryScreenHeight), 1);
        }
        var scale = 1.0;
        try
        {
            if (GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0)
                scale = dpiX / 96.0;
        }
        catch (DllNotFoundException)
        {
        }
        return new MonitorArea(ToRect(info.rcWork), ToRect(info.rcMonitor), scale);
    }

    /// <summary>
    /// The current state of the monitor <paramref name="area"/> was read from (work area and DPI change with the
    /// taskbar and display settings); the nearest connected monitor when that one is gone.
    /// </summary>
    public static MonitorArea Refresh(MonitorArea area) =>
        MonitorAt(new ScreenPoint(area.Bounds.X + area.Bounds.Width / 2, area.Bounds.Y + area.Bounds.Height / 2));

    /// <summary>
    /// Moves <paramref name="window"/> so its top-left corner sits at <paramref name="pixelX"/>, <paramref name="pixelY"/>
    /// (physical pixels). The window must have a handle (call after <c>SourceInitialized</c>).
    /// </summary>
    public static void MoveTo(Window window, int pixelX, int pixelY, bool topmost = false)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;
        var flags = SWP_NOSIZE | SWP_NOACTIVATE | (topmost ? 0 : SWP_NOZORDER);
        SetWindowPos(hwnd, topmost ? HWND_TOPMOST : IntPtr.Zero, pixelX, pixelY, 0, 0, flags);
    }

    /// <summary>The window's current size in physical pixels.</summary>
    public static (int Width, int Height) PixelSize(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var rect))
            return (rect.Right - rect.Left, rect.Bottom - rect.Top);
        var scale = DpiScale(window);
        return ((int)Math.Ceiling(window.ActualWidth * scale), (int)Math.Ceiling(window.ActualHeight * scale));
    }

    public static double DpiScale(System.Windows.Media.Visual visual)
    {
        try
        {
            return System.Windows.Media.VisualTreeHelper.GetDpi(visual).DpiScaleX;
        }
        catch (InvalidOperationException)
        {
            return 1;
        }
    }

    /// <summary>
    /// Places a window of the given pixel size next to <paramref name="anchor"/>, preferring below-right of it,
    /// and keeps it entirely inside the monitor's work area.
    /// </summary>
    public static (int X, int Y) PlaceNear(ScreenPoint anchor, int width, int height, Int32Rect work, int offset = 16)
    {
        var x = anchor.X + offset;
        var y = anchor.Y - height / 3;
        if (x + width > work.X + work.Width)
            x = anchor.X - offset - width;
        return (Clamp(x, work.X, work.X + work.Width - width), Clamp(y, work.Y, work.Y + work.Height - height));
    }

    /// <summary>Bottom-right corner placement inside a work area with a margin (toasts).</summary>
    public static (int X, int Y) BottomRight(Int32Rect work, int width, int height, int margin) =>
        (work.X + work.Width - width - margin, work.Y + work.Height - height - margin);

    static int Clamp(int value, int min, int max) => max < min ? min : Math.Clamp(value, min, max);

    static Int32Rect ToRect(RECT r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
}
