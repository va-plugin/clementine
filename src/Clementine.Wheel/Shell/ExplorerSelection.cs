using System.IO;
using System.Runtime.InteropServices;
using Clementine.Core;

namespace Clementine.Wheel;

/// <summary>
/// Reads the files selected in the foreground File Explorer window (or on the desktop) through the
/// <c>Shell.Application</c> automation model. Windows 11 tabs share one top-level window, so the active tab is
/// matched through its <c>IShellBrowser</c> window. Read-only; every failure yields an empty list.
/// </summary>
internal static class ExplorerSelection
{
    const int SWC_DESKTOP = 8;
    const int SWFO_NEEDDISPATCH = 1;
    const int CSIDL_DESKTOP = 0;

    static readonly Guid ShellWindowsClsid = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    static readonly Guid SidTopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    static readonly Guid IidShellBrowser = new("000214E2-0000-0000-C000-000000000046");

    /// <summary>Runs <see cref="GetSelectedPaths"/> on a private STA thread so a slow Explorer never blocks the UI.</summary>
    public static async Task<IReadOnlyList<string>> GetSelectedPathsAsync(IntPtr foregroundWindow, ILog log, TimeSpan timeout)
    {
        var completion = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(GetSelectedPaths(foregroundWindow, log));
            }
            catch (Exception ex)
            {
                log.Warn("Reading the Explorer selection failed", ex);
                completion.TrySetResult([]);
            }
        })
        {
            IsBackground = true,
            Name = "Clementine Explorer selection",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try
        {
            return await completion.Task.WaitAsync(timeout).ConfigureAwait(true);
        }
        catch (TimeoutException)
        {
            log.Warn("Explorer didn't report its selection in time.");
            return [];
        }
    }

    /// <summary>Selected file-system paths in <paramref name="foregroundWindow"/>'s Explorer window, or on the desktop. Call on an STA thread.</summary>
    public static IReadOnlyList<string> GetSelectedPaths(IntPtr foregroundWindow, ILog log)
    {
        var root = NativeMethods.GetAncestor(foregroundWindow, NativeMethods.GA_ROOT);
        if (root == IntPtr.Zero)
            root = foregroundWindow;
        var className = NativeMethods.ClassName(root);
        try
        {
            return className switch
            {
                "CabinetWClass" or "ExploreWClass" => FromExplorerWindow(root),
                "Progman" or "WorkerW" => FromDesktop(),
                _ => [],
            };
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidComObjectException)
        {
            log.Warn($"Couldn't read the selection from {className}", ex);
            return [];
        }
    }

    static IReadOnlyList<string> FromExplorerWindow(IntPtr root)
    {
        // The active tab's view window is the first ShellTabWindowClass child in z-order.
        var activeTab = NativeMethods.FindWindowEx(root, IntPtr.Zero, "ShellTabWindowClass", null);
        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType is null)
            return [];

        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic windows = shell.Windows();
            int count = windows.Count;
            IReadOnlyList<string>? fallback = null;
            for (var i = 0; i < count; i++)
            {
                dynamic? window = windows.Item(i);
                if (window is null)
                    continue;
                long hwnd = Convert.ToInt64(window.HWND);
                if (new IntPtr(hwnd) != root)
                    continue;

                var tab = TabWindowOf((object)window);
                if (activeTab == IntPtr.Zero || tab == activeTab)
                    return SelectedPaths(window.Document);
                fallback ??= SelectedPaths(window.Document);
            }
            return fallback ?? [];
        }
        finally
        {
            Marshal.FinalReleaseComObject((object)shell);
        }
    }

    static IReadOnlyList<string> FromDesktop()
    {
        var type = Type.GetTypeFromCLSID(ShellWindowsClsid);
        if (type is null)
            return [];
        dynamic shellWindows = Activator.CreateInstance(type)!;
        try
        {
            object location = CSIDL_DESKTOP;
            object root = null!;
            int hwnd;
            dynamic? desktop = shellWindows.FindWindowSW(ref location, ref root, SWC_DESKTOP, out hwnd, SWFO_NEEDDISPATCH);
            return desktop is null ? [] : SelectedPaths(desktop.Document);
        }
        finally
        {
            Marshal.FinalReleaseComObject((object)shellWindows);
        }
    }

    static IReadOnlyList<string> SelectedPaths(dynamic? document)
    {
        if (document is null)
            return [];
        dynamic items = document.SelectedItems();
        int count = items.Count;
        var paths = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            dynamic? item = items.Item(i);
            if (item is null)
                continue;
            string? path = item.Path as string;
            if (!string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path)))
                paths.Add(path);
        }
        return paths;
    }

    static IntPtr TabWindowOf(object browser)
    {
        try
        {
            if (browser is not IComServiceProvider provider)
                return IntPtr.Zero;
            var service = SidTopLevelBrowser;
            var iid = IidShellBrowser;
            var shellBrowser = provider.QueryService(ref service, ref iid);
            return shellBrowser is IOleWindow window ? window.GetWindow() : IntPtr.Zero;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return IntPtr.Zero;
        }
    }

    [ComImport]
    [Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IComServiceProvider
    {
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object QueryService(ref Guid guidService, ref Guid riid);
    }

    [ComImport]
    [Guid("00000114-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IOleWindow
    {
        IntPtr GetWindow();

        void ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool enterMode);
    }
}
