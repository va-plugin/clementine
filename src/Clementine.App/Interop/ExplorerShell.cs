using System.Diagnostics;
using System.IO;
using Clementine.Core;
using static Clementine.App.Interop.NativeMethods;

namespace Clementine.App.Interop;

/// <summary>Talks to Explorer and the shell: reveal/select files, open with the default app, "Open with", terminals.</summary>
internal static class ExplorerShell
{
    /// <summary>Opens Explorer with the items selected (grouped per folder). Never throws.</summary>
    public static void Reveal(IEnumerable<string> paths, ILog? log = null)
    {
        foreach (var folder in paths.Where(p => File.Exists(p) || Directory.Exists(p))
                     .GroupBy(p => Path.GetDirectoryName(p.TrimEnd('\\')) ?? p, StringComparer.OrdinalIgnoreCase)
                     .Take(8))
        {
            try
            {
                if (!SelectInFolder(folder.Key, folder.ToList()))
                    StartExplorer(["/select,", folder.First()]);
            }
            catch (Exception ex)
            {
                log?.Warn($"Couldn't reveal {folder.Key}", ex);
            }
        }
    }

    /// <summary>Opens a file or folder with its default handler.</summary>
    public static void Open(string path, ILog? log = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            log?.Warn($"Couldn't open {path}", ex);
            throw new OperationException($"Windows couldn't open {Path.GetFileName(path)}.", ex.Message, ex);
        }
    }

    /// <summary>Opens a URL in the default browser (only ever on an explicit user click).</summary>
    public static void OpenUrl(string url, ILog? log = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log?.Warn($"Couldn't open {url}", ex);
        }
    }

    /// <summary>Shows the system "Open with" dialog for <paramref name="file"/>.</summary>
    public static void OpenWith(string file, IntPtr owner)
    {
        var info = new OPENASINFO { pcszFile = file, pcszClass = null, oaifInFlags = OAIF_EXEC | OAIF_HIDE_REGISTRATION };
        var hr = SHOpenWithDialog(owner, ref info);
        // HRESULT_FROM_WIN32(ERROR_CANCELLED) = 0x800704C7: the user closed the dialog.
        if (hr != 0 && hr != unchecked((int)0x800704C7))
        {
            StartProcess(RundllPath, ["shell32.dll,OpenAs_RunDLL", file]);
        }
    }

    /// <summary>System32\rundll32.exe by full path, so a rundll32.exe in the working directory or on PATH is never picked.</summary>
    internal static string RundllPath => Path.Combine(Environment.SystemDirectory, "rundll32.exe");

    /// <summary>Opens Windows Terminal in <paramref name="directory"/>, falling back to PowerShell.</summary>
    public static void OpenTerminal(string directory)
    {
        var wt = ToolLocator.FindOnPath("wt.exe")
                 ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
        var pwsh = ToolLocator.FindOnPath("pwsh.exe");
        var shell = pwsh ?? Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        foreach (var launch in TerminalLaunches(directory, File.Exists(wt) ? wt : null, shell))
        {
            if (StartProcess(launch.Exe, launch.Arguments, launch.WorkingDirectory))
                return;
        }
        throw new OperationException("Couldn't open a terminal (Windows Terminal and PowerShell weren't found).");
    }

    /// <summary>A program to start for "Open terminal here".</summary>
    internal sealed record TerminalLaunch(string Exe, IReadOnlyList<string> Arguments, string? WorkingDirectory);

    /// <summary>
    /// What to try, in order: Windows Terminal (<c>wt -d &lt;folder&gt;</c>), then PowerShell started in the folder.
    /// wt splits its command line at every ';' — even inside the -d value — so a folder whose path contains one
    /// goes straight to PowerShell, which receives the folder as its working directory and never parses it.
    /// </summary>
    internal static IReadOnlyList<TerminalLaunch> TerminalLaunches(string directory, string? windowsTerminal, string shell)
    {
        var launches = new List<TerminalLaunch>();
        if (windowsTerminal is not null && !directory.Contains(';', StringComparison.Ordinal))
            launches.Add(new TerminalLaunch(windowsTerminal, ["-d", directory], null));
        launches.Add(new TerminalLaunch(shell, ["-NoExit", "-NoLogo"], directory));
        return launches;
    }

    static bool SelectInFolder(string folder, IReadOnlyList<string> items)
    {
        var folderPidl = ILCreateFromPathW(folder);
        if (folderPidl == IntPtr.Zero)
            return false;
        var itemPidls = items.Select(ILCreateFromPathW).Where(p => p != IntPtr.Zero).ToArray();
        try
        {
            return itemPidls.Length > 0 && SHOpenFolderAndSelectItems(folderPidl, (uint)itemPidls.Length, itemPidls, 0) == 0;
        }
        finally
        {
            foreach (var pidl in itemPidls)
                ILFree(pidl);
            ILFree(folderPidl);
        }
    }

    static void StartExplorer(IEnumerable<string> arguments) =>
        StartProcess(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), arguments);

    /// <summary>Starts a program; a console program gets its own new console window because Clementine has none.</summary>
    static bool StartProcess(string exe, IEnumerable<string> arguments, string? workingDirectory = null)
    {
        try
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = workingDirectory ?? "" };
            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);
            Process.Start(start)?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
