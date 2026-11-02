using System.IO;
using Clementine.Core.Hosting;

namespace Clementine.App.Startup;

/// <summary>What a launch of Clementine.exe asks for.</summary>
public enum ShellCommandKind
{
    /// <summary>No arguments: open the main window.</summary>
    OpenMainWindow,

    /// <summary><c>--background</c>: tray only (launch at login).</summary>
    Background,

    /// <summary><c>--wheel &lt;files…&gt;</c>: click-mode wheel at the cursor (context menu, Send To).</summary>
    Wheel,

    /// <summary><c>--open &lt;files…&gt;</c>: main window with these files.</summary>
    OpenFiles,

    /// <summary><c>--settings</c>: open the settings window.</summary>
    Settings,

    /// <summary><c>--quit</c>: ask the running instance to exit.</summary>
    Quit,

    /// <summary><c>--snapshot-ui &lt;dir&gt;</c>: render previews and exit (no single-instance check, no tray).</summary>
    Snapshot,

    /// <summary><c>--make-icon &lt;dir&gt;</c>: write clementine.ico and PNGs and exit.</summary>
    MakeIcon,

    /// <summary>
    /// <c>--unregister</c>: remove the Run value, Explorer verbs and Send To shortcut for this user and exit 0
    /// (what the uninstaller does; handy after deleting a portable copy).
    /// </summary>
    Unregister,
}

/// <summary>A parsed command line. Paths are absolute (resolved against the launching process's working directory).</summary>
public sealed record ShellCommand(ShellCommandKind Kind, IReadOnlyList<string> Paths, WheelMode Mode = WheelMode.Convert, string? Directory = null)
{
    /// <summary>Commands that run in-process and never talk to a running instance.</summary>
    public bool IsStandalone => Kind is ShellCommandKind.Snapshot or ShellCommandKind.MakeIcon or ShellCommandKind.Unregister;
}

/// <summary>Parses Clementine.exe arguments.</summary>
public static class CommandLine
{
    /// <summary>
    /// Parses <paramref name="args"/>. Unknown switches are ignored; bare paths with no command mean <c>--open</c>
    /// (e.g. files dropped onto Clementine.exe).
    /// </summary>
    public static ShellCommand Parse(IReadOnlyList<string> args, string? workingDirectory = null)
    {
        var kind = ShellCommandKind.OpenMainWindow;
        var explicitKind = false;
        var mode = WheelMode.Convert;
        string? directory = null;
        var paths = new List<string>();

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            switch (arg.ToLowerInvariant())
            {
                case "--background":
                    SetKind(ShellCommandKind.Background);
                    break;
                case "--wheel":
                    SetKind(ShellCommandKind.Wheel);
                    break;
                case "--open":
                    SetKind(ShellCommandKind.OpenFiles);
                    break;
                case "--settings":
                    SetKind(ShellCommandKind.Settings);
                    break;
                case "--quit":
                    SetKind(ShellCommandKind.Quit);
                    break;
                case "--unregister":
                    SetKind(ShellCommandKind.Unregister);
                    break;
                case "--snapshot-ui":
                    SetKind(ShellCommandKind.Snapshot);
                    if (i + 1 < args.Count)
                        directory = Resolve(args[++i], workingDirectory);
                    break;
                case "--make-icon":
                    SetKind(ShellCommandKind.MakeIcon);
                    if (i + 1 < args.Count)
                        directory = Resolve(args[++i], workingDirectory);
                    break;
                case "--mode" when i + 1 < args.Count:
                    mode = args[++i].ToLowerInvariant() switch
                    {
                        "tools" or "tool" => WheelMode.Tools,
                        "actions" or "action" => WheelMode.Actions,
                        _ => WheelMode.Convert,
                    };
                    break;
                case "--tools":
                    mode = WheelMode.Tools;
                    break;
                case "--actions":
                    mode = WheelMode.Actions;
                    break;
                default:
                    if (!arg.StartsWith("--", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(arg))
                        paths.Add(Resolve(arg, workingDirectory));
                    break;
            }
        }

        if (!explicitKind && paths.Count > 0)
            kind = ShellCommandKind.OpenFiles;
        return new ShellCommand(kind, paths, mode, directory);

        void SetKind(ShellCommandKind value)
        {
            // The first command wins; "--background --wheel x" still shows the wheel.
            if (!explicitKind || kind == ShellCommandKind.Background)
                kind = value;
            explicitKind = true;
        }
    }

    /// <summary>Formats a command back into arguments (used when forwarding to the running instance).</summary>
    public static IReadOnlyList<string> Format(ShellCommand command)
    {
        var args = new List<string>();
        switch (command.Kind)
        {
            case ShellCommandKind.Background: args.Add("--background"); break;
            case ShellCommandKind.Wheel: args.Add("--wheel"); break;
            case ShellCommandKind.OpenFiles: args.Add("--open"); break;
            case ShellCommandKind.Settings: args.Add("--settings"); break;
            case ShellCommandKind.Quit: args.Add("--quit"); break;
            case ShellCommandKind.Snapshot: args.Add("--snapshot-ui"); break;
            case ShellCommandKind.MakeIcon: args.Add("--make-icon"); break;
            case ShellCommandKind.Unregister: args.Add("--unregister"); break;
        }
        if (command.Directory is not null)
            args.Add(command.Directory);
        if (command.Mode != WheelMode.Convert)
        {
            args.Add("--mode");
            args.Add(command.Mode.ToString().ToLowerInvariant());
        }
        args.AddRange(command.Paths);
        return args;
    }

    static string Resolve(string path, string? workingDirectory)
    {
        var trimmed = path.Trim().Trim('"');
        // Explorer passes a drive as "%1" = "C:\", which the C runtime's quoting rules turn into C:" (the \" is an
        // escaped quote). Without its backslash, "C:" would mean "the current directory on drive C".
        if (trimmed.Length == 2 && char.IsAsciiLetter(trimmed[0]) && trimmed[1] == ':')
            trimmed += Path.DirectorySeparatorChar;
        try
        {
            if (Path.IsPathFullyQualified(trimmed))
                return Path.GetFullPath(trimmed);
            return Path.GetFullPath(Path.Combine(workingDirectory ?? Environment.CurrentDirectory, trimmed));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return trimmed;
        }
    }
}
