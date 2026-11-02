using System.Diagnostics;
using System.Runtime.InteropServices;
using Clementine.Core.Hosting;

namespace Clementine.Wheel;

/// <summary>
/// Decides whether the catcher may appear for a drag, from the window the drag started in:
/// <list type="bullet">
/// <item>User Interface Privilege Isolation blocks OLE drag-and-drop between integrity levels (an elevated app can't
/// drop on us; an elevated Clementine can't receive drops from Explorer). The catcher would then sit under the cursor
/// for the whole drag as an invisible window that refuses the drop, blocking every target below it — so for such
/// drags it stays hidden and the wheel fails silently.</item>
/// <item>Over an exclusive full-screen Direct3D app (games), a topmost window can knock it out of full-screen mode. Only
/// the monitor that app covers is affected: a full-screen output on another display (a projector, a game on the second
/// screen) must not switch the wheel off for drags on this one.</item>
/// </list>
/// </summary>
internal static class DragSource
{
    /// <summary>Integrity level that couldn't be read (treated as unreachable: almost always a higher level).</summary>
    public const int Unknown = -1;

    public const int Low = 0x1000;
    public const int Medium = 0x2000;
    public const int High = 0x3000;
    public const int System = 0x4000;

    const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;

    static readonly Lazy<int> Own = new(() => IntegrityOfProcess((uint)Environment.ProcessId));

    /// <summary>This process's integrity level (mandatory label RID).</summary>
    public static int OwnIntegrity => Own.Value;

    /// <summary>
    /// Whether an OLE drag from a process at <paramref name="source"/> integrity can reach a drop target at
    /// <paramref name="own"/>: same level always; a lower level only when we run unelevated (Low/AppContainer → Medium
    /// works through the shell's broker); never upwards, never into an elevated Clementine from below, never from a
    /// source whose level can't be read. If our own level can't be read, nothing is blocked (behave as before).
    /// </summary>
    public static bool CanReach(int source, int own)
    {
        if (own == Unknown)
            return true;
        if (source == Unknown)
            return false;
        var sourceBand = source / 0x1000;
        var ownBand = own / 0x1000;
        if (sourceBand == ownBand)
            return true;
        return sourceBand < ownBand && ownBand <= Medium / 0x1000;
    }

    /// <summary>
    /// Null when the catcher may show at <paramref name="point"/> for a drag that started at <paramref name="origin"/>
    /// (the drag source is the window there), otherwise a short reason for the log. Never throws.
    /// </summary>
    public static string? ReasonToStayHidden(ScreenPoint origin, ScreenPoint point)
    {
        try
        {
            if (IsExclusiveFullScreen() && FullScreenCovers(point))
                return "an exclusive full-screen app covers this monitor";
            var window = NativeMethods.WindowFromPoint(new NativeMethods.POINT(origin.X, origin.Y));
            if (window == IntPtr.Zero)
                window = NativeMethods.GetForegroundWindow();
            if (window == IntPtr.Zero)
                return null;
            var source = IntegrityOfWindow(window);
            return CanReach(source, OwnIntegrity)
                ? null
                : $"the drag comes from a window at integrity 0x{source:X} and Clementine runs at 0x{OwnIntegrity:X} (Windows blocks drops between them)";
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ExternalException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether, with an exclusive full-screen app running somewhere, the monitor at <paramref name="point"/> is the one
    /// it owns. Exclusive full-screen apps are the foreground window and cover their monitor exactly; the desktop
    /// (which spans every monitor) doesn't count. If the foreground can't be read, assume the worst.
    /// </summary>
    public static bool FullScreenBlocks(PixelRect? foreground, string foregroundClass, PixelRect monitor)
    {
        if (foreground is not { } window)
            return true;
        if (foregroundClass is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            return false;
        return window.Left <= monitor.Left && window.Top <= monitor.Top && window.Right >= monitor.Right && window.Bottom >= monitor.Bottom;
    }

    static bool FullScreenCovers(ScreenPoint point)
    {
        var monitor = NativeMethods.MonitorFromPoint(new NativeMethods.POINT(point.X, point.Y), NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
            return true;
        var bounds = new PixelRect(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom);
        var foreground = NativeMethods.GetForegroundWindow();
        PixelRect? rect = foreground != IntPtr.Zero && NativeMethods.GetWindowRect(foreground, out var r)
            ? new PixelRect(r.Left, r.Top, r.Right, r.Bottom)
            : null;
        return FullScreenBlocks(rect, NativeMethods.ClassName(foreground), bounds);
    }

    /// <summary>True while a Direct3D game or app owns a monitor in exclusive full-screen mode.</summary>
    public static bool IsExclusiveFullScreen()
    {
        try
        {
            return NativeMethods.SHQueryUserNotificationState(out var state) == 0 && state == QUNS_RUNNING_D3D_FULL_SCREEN;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Integrity level of the process that owns <paramref name="window"/>, or <see cref="Unknown"/>.</summary>
    public static int IntegrityOfWindow(IntPtr window)
    {
        if (window == IntPtr.Zero || NativeMethods.GetWindowThreadProcessId(window, out var processId) == 0 || processId == 0)
            return Unknown;
        return processId == (uint)Environment.ProcessId ? OwnIntegrity : IntegrityOfProcess(processId);
    }

    /// <summary>Integrity level (mandatory label RID) of a process, or <see cref="Unknown"/> when it can't be read.</summary>
    public static int IntegrityOfProcess(uint processId)
    {
        var process = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process == IntPtr.Zero)
            return Unknown;
        try
        {
            if (!NativeMethods.OpenProcessToken(process, NativeMethods.TOKEN_QUERY, out var token))
                return Unknown;
            try
            {
                NativeMethods.GetTokenInformation(token, NativeMethods.TokenIntegrityLevel, IntPtr.Zero, 0, out var length);
                if (length <= 0 || length > 4096)
                    return Unknown;
                var buffer = Marshal.AllocHGlobal(length);
                try
                {
                    if (!NativeMethods.GetTokenInformation(token, NativeMethods.TokenIntegrityLevel, buffer, length, out _))
                        return Unknown;
                    // TOKEN_MANDATORY_LABEL starts with SID_AND_ATTRIBUTES { PSID Sid; DWORD Attributes; }.
                    var sid = Marshal.ReadIntPtr(buffer);
                    var count = Marshal.ReadByte(NativeMethods.GetSidSubAuthorityCount(sid));
                    return count == 0 ? Unknown : Marshal.ReadInt32(NativeMethods.GetSidSubAuthority(sid, (uint)(count - 1)));
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                NativeMethods.CloseHandle(token);
            }
        }
        catch (Exception ex) when (ex is ExternalException or EntryPointNotFoundException)
        {
            Debug.WriteLine(ex);
            return Unknown;
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }
}
