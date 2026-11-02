using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Clementine.App.Actions;
using Clementine.App.Dialogs;
using Clementine.App.Interop;
using Clementine.App.Main;
using Clementine.App.Preferences;
using Clementine.App.Snapshots;
using Clementine.App.Startup;
using Clementine.Core;
using Clementine.Core.Hosting;
using Clementine.TestSupport;
using Microsoft.Win32;

namespace Clementine.App.Tests;

// ===== Installer integration =====

/// <summary>
/// The installer hooks' cleanup and refresh, run only against a throw-away key under HKCU\Software\ClementineTests
/// and a temp Send To folder. The real Run key, HKCU\Software\Classes and SendTo folder are never touched.
/// </summary>
public sealed class UserIntegrationTests : IDisposable
{
    const string TestRoot = @"Software\ClementineTests";
    const string OldExe = @"C:\Users\Me\AppData\Local\Clementine\app-0.1.0\Clementine.exe";
    const string NewExe = @"C:\Users\Me\AppData\Local\Clementine\current\Clementine.exe";
    readonly string _root = $@"{TestRoot}\Integration-{Guid.NewGuid():N}";
    readonly TestWorkspace _ws = new("integration");

    ShellRegistration Registration(string exe) => new(exe, Registry.CurrentUser, _root + @"\Run", _root + @"\Classes");

    SendToShortcut SendTo(string exe) => new(_ws.Path("SendTo"), exe);

    [Fact]
    public void Remove_all_takes_away_every_registration_and_leaves_other_apps_alone()
    {
        var registration = Registration(OldExe);
        var sendTo = SendTo(_ws.WriteText(@"app\Clementine.exe", "exe"));
        registration.SetLaunchAtLogin(true);
        registration.SetContextMenu(true);
        sendTo.Create();
        using (var run = Registry.CurrentUser.CreateSubKey(_root + @"\Run"))
            run.SetValue("OtherApp", "other.exe");
        using (Registry.CurrentUser.CreateSubKey(_root + @"\Classes\*\shell\OtherVerb")) { }
        var otherShortcut = _ws.WriteText(@"SendTo\Other.lnk", "not ours");

        Assert.Empty(UserIntegration.RemoveAll(registration, sendTo));

        Assert.False(registration.IsLaunchAtLoginEnabled());
        Assert.False(registration.IsContextMenuInstalled());
        using (var verb = Registry.CurrentUser.OpenSubKey(_root + @"\Classes\Directory\shell\Clementine"))
            Assert.Null(verb);
        Assert.False(sendTo.Exists);
        using (var run = Registry.CurrentUser.OpenSubKey(_root + @"\Run"))
            Assert.Equal("other.exe", run!.GetValue("OtherApp"));
        using (var other = Registry.CurrentUser.OpenSubKey(_root + @"\Classes\*\shell\OtherVerb"))
            Assert.NotNull(other);
        Assert.True(File.Exists(otherShortcut));

        // Nothing registered (a second uninstall, or --unregister on a clean machine): still fine.
        Assert.Empty(UserIntegration.RemoveAll(registration, sendTo));
    }

    [Fact]
    public void Remove_all_never_throws_and_reports_what_it_could_not_remove()
    {
        var registration = Registration(OldExe);
        var sendTo = SendTo(_ws.WriteText(@"app\Clementine.exe", "exe"));
        registration.SetContextMenu(true);
        sendTo.Create();
        File.SetAttributes(sendTo.ShortcutPath, FileAttributes.ReadOnly);
        try
        {
            var problems = UserIntegration.RemoveAll(registration, sendTo);
            Assert.Contains("Send To", Assert.Single(problems), StringComparison.Ordinal);
            Assert.False(registration.IsContextMenuInstalled()); // the rest still went
        }
        finally
        {
            File.SetAttributes(sendTo.ShortcutPath, FileAttributes.Normal);
        }
    }

    [Fact]
    public void After_an_update_the_enabled_integrations_point_at_the_new_exe()
    {
        var newExe = _ws.WriteText(@"current\Clementine.exe", "exe");
        var oldExe = _ws.WriteText(@"app-0.1.0\Clementine.exe", "exe");
        Registration(oldExe).SetLaunchAtLogin(true);
        Registration(oldExe).SetContextMenu(true);
        SendTo(oldExe).Create();
        var settings = new ClementineSettings { LaunchAtLogin = true, ExplorerContextMenu = true, SendToShortcut = true };

        Assert.Empty(UserIntegration.Refresh(settings, Registration(newExe), SendTo(newExe)));

        using (var run = Registry.CurrentUser.OpenSubKey(_root + @"\Run"))
            Assert.Equal($"\"{newExe}\" --background", run!.GetValue("Clementine"));
        foreach (var target in new[] { @"*\shell\Clementine", @"Directory\shell\Clementine" })
        {
            using var command = Registry.CurrentUser.OpenSubKey($@"{_root}\Classes\{target}\command");
            Assert.Equal($"\"{newExe}\" --wheel \"%1\"", command!.GetValue(null));
        }
        Assert.Equal(newExe, ShellLink.Read(SendTo(newExe).ShortcutPath).Target, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void After_an_update_integrations_that_are_off_stay_off()
    {
        Assert.Empty(UserIntegration.Refresh(new ClementineSettings(), Registration(NewExe), SendTo(NewExe)));
        Assert.False(Registration(NewExe).IsLaunchAtLoginEnabled());
        Assert.False(Registration(NewExe).IsContextMenuInstalled());
        Assert.False(SendTo(NewExe).Exists);
    }

    [Fact]
    public void Unregister_switch_is_parsed_formatted_and_runs_without_the_instance_check()
    {
        var command = CommandLine.Parse(["--unregister"]);
        Assert.Equal(ShellCommandKind.Unregister, command.Kind);
        Assert.True(command.IsStandalone);
        Assert.Equal(["--unregister"], CommandLine.Format(command));
        Assert.Equal(ShellCommandKind.Unregister, CommandLine.Parse(["--UNREGISTER"]).Kind);
        // A stray --background (e.g. copied from the Run value) doesn't win over it.
        Assert.Equal(ShellCommandKind.Unregister, CommandLine.Parse(["--background", "--unregister"]).Kind);
    }

    [Fact]
    public void Velopack_runs_for_hook_launches_and_installed_copies_only()
    {
        var installed = _ws.Path(@"Clementine\current");
        Directory.CreateDirectory(installed);
        _ws.WriteText(@"Clementine\Update.exe", "velopack");
        var portable = Directory.CreateDirectory(_ws.Path(@"dev\bin\debug")).FullName;

        Assert.True(VelopackHooks.Applies([], installed + @"\"));
        Assert.True(VelopackHooks.Applies(["--wheel", @"C:\a.mov"], installed));
        Assert.True(VelopackHooks.Applies(["--veloapp-uninstall", "0.1.0"], portable));
        Assert.True(VelopackHooks.Applies(["--VELOAPP-updated", "0.2.0"], portable));
        Assert.False(VelopackHooks.Applies([], portable));
        Assert.False(VelopackHooks.Applies(["--unregister"], portable + @"\"));
        Assert.False(VelopackHooks.Applies([], @"C:\"));
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false);
        using (var parent = Registry.CurrentUser.OpenSubKey(TestRoot, writable: true))
        {
            if (parent is not null && parent.SubKeyCount == 0 && parent.ValueCount == 0)
            {
                parent.Dispose();
                Registry.CurrentUser.DeleteSubKey(TestRoot, throwOnMissingSubKey: false);
            }
        }
        _ws.Dispose();
    }
}

// ===== Quit / sign-out =====

public class JobDrainTests
{
    /// <summary>A job that, once cancelled, takes a while to stop its tool and remove its partial output.</summary>
    static (DelegateOperation Op, TaskCompletionSource<string> Partial) SlowToStop(TimeSpan cleanup) =>
        SlowToStop(() => Task.Delay(cleanup, CancellationToken.None));

    /// <summary>A job that, once cancelled, stops only when <paramref name="stopping"/> completes, then removes its partial output.</summary>
    static (DelegateOperation Op, TaskCompletionSource<string> Partial) SlowToStop(Func<Task> stopping)
    {
        var partial = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var op = new DelegateOperation(
            new OperationInfo { Id = "test.slow-stop", Category = OperationCategory.Convert, Group = "Slow", Title = "Slow", IconKey = "convert" },
            _ => true,
            async (request, _, ct) =>
            {
                var file = Path.Combine(request.TempDirectory, "partial.mov");
                await File.WriteAllTextAsync(file, "half an encode", CancellationToken.None);
                partial.TrySetResult(file);
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    await stopping(); // killing ffmpeg, flushing, deleting…
                    File.Delete(file);
                    throw;
                }
                return new OperationResult();
            });
        return (op, partial);
    }

    static (JobQueue Jobs, TestWorkspace Ws) Queue()
    {
        var ws = new TestWorkspace("drain");
        return (HostServices.Create([], SettingsStore.InMemory(), ws.Log).Jobs, ws);
    }

    [Fact]
    public async Task Quit_waits_for_cancelled_jobs_to_clean_up()
    {
        var (jobs, ws) = Queue();
        using var cleanup = ws;
        var (op, partial) = SlowToStop(TimeSpan.FromMilliseconds(600));
        var job = Assert.Single(jobs.Enqueue(op, [InputFile.FromPath(ws.WriteText("clip.mov", "x"))]));
        var partialFile = await partial.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var tempFolder = Path.GetDirectoryName(partialFile)!;

        var clock = Stopwatch.StartNew();
        Assert.True(await JobDrain.CancelAndWaitAsync(jobs, TimeSpan.FromSeconds(5)));

        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(500), $"returned after {clock.ElapsedMilliseconds} ms, before the job stopped");
        Assert.Equal(JobState.Canceled, job.State);
        Assert.False(File.Exists(partialFile));
        Assert.False(Directory.Exists(tempFolder)); // the job queue removes the scratch folder before completing
    }

    [Fact]
    public async Task Quit_gives_up_after_the_grace_period()
    {
        var (jobs, ws) = Queue();
        using var cleanup = ws;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stubborn = new DelegateOperation(
            new OperationInfo { Id = "test.stubborn", Category = OperationCategory.Convert, Group = "Stubborn", Title = "Stubborn", IconKey = "convert" },
            _ => true,
            async (_, _, _) =>
            {
                started.TrySetResult();
                await release.Task; // ignores cancellation
                return new OperationResult();
            });
        jobs.Enqueue(stubborn, [InputFile.FromPath(ws.WriteText("clip.mov", "x"))]);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var clock = Stopwatch.StartNew();
            Assert.False(await JobDrain.CancelAndWaitAsync(jobs, TimeSpan.FromMilliseconds(300)));
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"waited {clock.ElapsedMilliseconds} ms");

            clock.Restart();
            Assert.False(JobDrain.CancelAndWait(jobs, TimeSpan.FromMilliseconds(300)));
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"waited {clock.ElapsedMilliseconds} ms");
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task Session_end_waits_synchronously_and_nothing_running_returns_at_once()
    {
        var (jobs, ws) = Queue();
        using var cleanup = ws;
        var clock = Stopwatch.StartNew();
        Assert.True(JobDrain.CancelAndWait(jobs, TimeSpan.FromSeconds(5)));
        Assert.True(await JobDrain.CancelAndWaitAsync(jobs, TimeSpan.FromSeconds(5)));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1));

        var (op, partial) = SlowToStop(TimeSpan.FromMilliseconds(300));
        var job = Assert.Single(jobs.Enqueue(op, [InputFile.FromPath(ws.WriteText("clip.mov", "x"))]));
        var partialFile = await partial.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(JobDrain.CancelAndWait(jobs, JobDrain.SessionEndGrace));
        Assert.Equal(JobState.Canceled, job.State);
        Assert.False(File.Exists(partialFile));
    }

    [Fact]
    public async Task Quit_waits_while_the_ui_thread_keeps_pumping()
    {
        var (jobs, ws) = Queue();
        using var cleanup = ws;
        // The cancelled job can't finish stopping until the UI thread has run work queued after the wait began. A wait
        // that blocked the UI thread could never get there (the job would only give up after 30 s, and the test would
        // fail), and the outcome no longer depends on timing, so a loaded machine running every test project at once
        // can't make it flaky.
        var uiRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (op, partial) = SlowToStop(async () =>
        {
            try
            {
                await uiRan.Task.WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (TimeoutException)
            {
                // Stop anyway; the assertions below report what went wrong.
            }
        });
        var job = Assert.Single(jobs.Enqueue(op, [InputFile.FromPath(ws.WriteText("clip.mov", "x"))]));
        var partialFile = await partial.Task.WaitAsync(TimeSpan.FromSeconds(30));
        WpfHost.Run(() =>
        {
            var frame = new DispatcherFrame();
            bool? drained = null;
            var uiWorkRanWhileWaiting = false;
            _ = Run();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
            {
                uiWorkRanWhileWaiting = drained is null;
                uiRan.TrySetResult();
            });
            Dispatcher.PushFrame(frame);
            Assert.True(uiWorkRanWhileWaiting, "the UI thread didn't run queued work while waiting for the jobs");
            Assert.True(drained, "the drain gave up before the cancelled job had stopped");

            async Task Run()
            {
                // Generous: the drain returns as soon as the job has stopped, so this only matters when something is wrong.
                drained = await JobDrain.CancelAndWaitAsync(jobs, TimeSpan.FromSeconds(60));
                frame.Continue = false;
            }
        });
        Assert.Equal(JobState.Canceled, job.State);
        Assert.False(File.Exists(partialFile)); // the drain returned only after the job removed its partial output
    }
}

// ===== Copy / move =====

public class FileTransferHardeningTests
{
    readonly FakeShellActions _shell = new();

    IOperation Op(TestWorkspace ws, string id) => ws.Operation(new ActionsOperationProvider(_shell), id);

    /// <summary>A junction (no admin rights needed), or a symbolic link with Developer Mode; false when neither works.</summary>
    internal static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            using var mklink = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
            {
                ArgumentList = { "/c", "mklink", "/J", link, target },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            mklink.WaitForExit(10_000);
            if (mklink.ExitCode == 0 && Directory.Exists(link))
                return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [Fact]
    public async Task Copy_skips_junctions_and_symlinks_instead_of_following_them()
    {
        using var ws = new TestWorkspace("links");
        var folder = ws.Path("Project");
        ws.WriteText(@"Project\real.txt", "real");
        ws.WriteText(@"Project\sub\deep.txt", "deep");
        var hidden = ws.WriteText(@"Project\.hidden", "hidden");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        ws.WriteText(@"Elsewhere\outside.txt", "outside");
        // A loop back to the folder itself (the old copy recursed until the path was too long) and a link out of it.
        if (!TryCreateDirectoryLink(Path.Combine(folder, "loop"), folder) || !TryCreateDirectoryLink(Path.Combine(folder, "sub", "out"), ws.Path("Elsewhere")))
            return;
        var destination = Directory.CreateDirectory(ws.Path("dest")).FullName;

        var result = await ws.RunAsync(Op(ws, "action.copy-to"), [folder], new Dictionary<string, object?> { ["destination"] = destination });

        var copy = Path.Combine(destination, "Project");
        Assert.Equal([copy], result.Outputs);
        Assert.Equal("real", File.ReadAllText(Path.Combine(copy, "real.txt")));
        Assert.Equal("deep", File.ReadAllText(Path.Combine(copy, "sub", "deep.txt")));
        Assert.True(File.Exists(Path.Combine(copy, ".hidden"))); // hidden files are still copied
        Assert.False(Directory.Exists(Path.Combine(copy, "loop")));
        Assert.False(Directory.Exists(Path.Combine(copy, "sub", "out")));
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("2 links", warning, StringComparison.Ordinal);
        Assert.Contains("loop", warning, StringComparison.Ordinal);

        var duplicate = await ws.RunAsync(Op(ws, "action.duplicate"), [folder]);
        Assert.Contains("links", Assert.Single(duplicate.Warnings), StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(Assert.Single(duplicate.Outputs), "loop")));
    }

    [Fact]
    public async Task Copy_to_refuses_a_destination_inside_the_folder_being_copied()
    {
        using var ws = new TestWorkspace("copyto");
        var folder = Directory.CreateDirectory(ws.Path("Shots")).FullName;
        ws.WriteText(@"Shots\a.png", "a");

        foreach (var destination in new[] { Path.Combine(folder, "inner"), folder, folder + @"\" })
        {
            var ex = await Assert.ThrowsAsync<OperationException>(() => ws.RunAsync(Op(ws, "action.copy-to"), [folder],
                new Dictionary<string, object?> { ["destination"] = destination }));
            Assert.Equal("Can't copy the folder \"Shots\" into itself. Choose a folder outside it.", ex.Message);
        }
        Assert.Equal(["a.png"], Directory.EnumerateFileSystemEntries(folder).Select(p => Path.GetFileName(p)));

        // A sibling whose name merely starts the same is fine.
        var result = await ws.RunAsync(Op(ws, "action.copy-to"), [folder], new Dictionary<string, object?> { ["destination"] = folder + " backup" });
        Assert.Equal([Path.Combine(folder + " backup", "Shots")], result.Outputs);
    }

    [Fact]
    public async Task Moving_a_file_that_is_open_elsewhere_says_so_and_leaves_it_in_place()
    {
        using var ws = new TestWorkspace("moveto");
        var clip = ws.WriteText(@"src\clip.mov", "locked");
        var destination = Directory.CreateDirectory(ws.Path("dest")).FullName;

        OperationException ex;
        using (new FileStream(clip, FileMode.Open, FileAccess.Read, FileShare.Read)) // like a player holding the file
        {
            ex = await Assert.ThrowsAsync<OperationException>(() => ws.RunAsync(Op(ws, "action.move-to"), [clip],
                new Dictionary<string, object?> { ["destination"] = destination }));
        }

        Assert.Equal("Couldn't move \"clip.mov\": it is open in another program. Close it there and try again.", ex.Message);
        Assert.Equal("locked", File.ReadAllText(clip));
        Assert.Empty(Directory.EnumerateFileSystemEntries(destination)); // no partial, no reserved name left behind
    }

    [Fact]
    public async Task Across_drives_a_failed_delete_of_the_original_is_a_warning_not_a_failure()
    {
        using var ws = new TestWorkspace("moveto");
        var clip = ws.WriteText(@"src\clip.mov", "frames");
        var destination = Directory.CreateDirectory(ws.Path("dest")).FullName;

        TransferResult result;
        using (new FileStream(clip, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            result = await FileTransfer.MoveIntoAsync(InputFile.FromPath(clip), destination, ws.Output, _ => { }, CancellationToken.None,
                sameVolume: (_, _) => false);
        }

        Assert.Equal(Path.Combine(destination, "clip.mov"), result.Path);
        Assert.Equal("frames", File.ReadAllText(result.Path));
        Assert.True(File.Exists(clip));
        var warning = Assert.Single(result.Warnings);
        Assert.StartsWith($"Copied to {result.Path}, but the original couldn't be removed: ", warning, StringComparison.Ordinal);
        Assert.Contains("open in another program", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Across_drives_folders_move_and_a_folder_that_cannot_be_removed_stays_with_a_warning()
    {
        using var ws = new TestWorkspace("moveto");
        var destination = Directory.CreateDirectory(ws.Path("dest")).FullName;

        var moving = ws.Path("Shots");
        ws.WriteText(@"Shots\a.png", "a");
        ws.WriteText(@"Shots\sub\b.png", "b");
        var moved = await FileTransfer.MoveIntoAsync(InputFile.FromPath(moving), destination, ws.Output, _ => { }, CancellationToken.None, (_, _) => false);
        Assert.Empty(moved.Warnings);
        Assert.False(Directory.Exists(moving));
        Assert.Equal("b", File.ReadAllText(Path.Combine(moved.Path, "sub", "b.png")));

        var busy = ws.Path("Busy");
        var open = ws.WriteText(@"Busy\open.png", "open");
        TransferResult result;
        using (new FileStream(open, FileMode.Open, FileAccess.Read, FileShare.Read))
            result = await FileTransfer.MoveIntoAsync(InputFile.FromPath(busy), destination, ws.Output, _ => { }, CancellationToken.None, (_, _) => false);
        Assert.Equal("open", File.ReadAllText(Path.Combine(result.Path, "open.png")));
        Assert.StartsWith($"Copied to {result.Path}, but the original couldn't be removed", Assert.Single(result.Warnings), StringComparison.Ordinal);
        Assert.True(File.Exists(open));
    }

    [Fact]
    public async Task Across_drives_a_folder_with_links_is_copied_and_the_original_kept()
    {
        using var ws = new TestWorkspace("moveto");
        var folder = ws.Path("Project");
        ws.WriteText(@"Project\a.txt", "a");
        if (!TryCreateDirectoryLink(Path.Combine(folder, "loop"), folder))
            return;
        var destination = Directory.CreateDirectory(ws.Path("dest")).FullName;

        var result = await FileTransfer.MoveIntoAsync(InputFile.FromPath(folder), destination, ws.Output, _ => { }, CancellationToken.None, (_, _) => false);

        Assert.Equal("a", File.ReadAllText(Path.Combine(result.Path, "a.txt")));
        Assert.True(File.Exists(Path.Combine(folder, "a.txt")));
        Assert.Contains("wasn't removed", Assert.Single(result.Warnings), StringComparison.Ordinal);
    }
}

// ===== Explorer / terminal =====

public class ExplorerShellTests
{
    const string Wt = @"C:\Users\Me\AppData\Local\Microsoft\WindowsApps\wt.exe";
    const string Pwsh = @"C:\Program Files\PowerShell\7\pwsh.exe";

    [Fact]
    public void Windows_terminal_opens_ordinary_folders_and_powershell_is_the_fallback()
    {
        var launches = ExplorerShell.TerminalLaunches(@"D:\VJ Loops", Wt, Pwsh);
        Assert.Equal(2, launches.Count);
        Assert.Equal(Wt, launches[0].Exe);
        Assert.Equal(["-d", @"D:\VJ Loops"], launches[0].Arguments);
        Assert.Equal(Pwsh, launches[1].Exe);
        Assert.Equal(@"D:\VJ Loops", launches[1].WorkingDirectory);
    }

    [Fact]
    public void A_folder_with_a_semicolon_never_reaches_windows_terminals_command_parser()
    {
        var launch = Assert.Single(ExplorerShell.TerminalLaunches(@"D:\Shows; 2026", Wt, Pwsh));
        Assert.Equal(Pwsh, launch.Exe);
        Assert.Equal(@"D:\Shows; 2026", launch.WorkingDirectory);
        Assert.DoesNotContain(launch.Arguments, a => a.Contains(';', StringComparison.Ordinal));

        Assert.Equal(Pwsh, Assert.Single(ExplorerShell.TerminalLaunches(@"D:\Shows", null, Pwsh)).Exe);
    }

    [Fact]
    public void Rundll32_is_started_from_system32_by_full_path()
    {
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "rundll32.exe"), ExplorerShell.RundllPath);
        Assert.True(Path.IsPathFullyQualified(ExplorerShell.RundllPath));
        Assert.True(File.Exists(ExplorerShell.RundllPath));
    }
}

// ===== Start-up housekeeping =====

public class HousekeepingTests
{
    static readonly DateTime Now = DateTime.UtcNow;

    static string Folder(string root, string name, TimeSpan age, bool freshFileInside = false)
    {
        var path = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        var file = Path.Combine(path, "part.tmp");
        File.WriteAllText(file, "x");
        var when = Now - age;
        File.SetCreationTimeUtc(file, when);
        File.SetLastWriteTimeUtc(file, freshFileInside ? Now : when);
        Directory.SetCreationTimeUtc(path, when);
        Directory.SetLastWriteTimeUtc(path, when);
        return path;
    }

    static string Log(string directory, string name, TimeSpan age, int bytes = 10)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, Now - age);
        return path;
    }

    [Fact]
    public void Old_job_folders_go_and_recent_or_busy_ones_and_caches_stay()
    {
        using var ws = new TestWorkspace("housekeeping");
        var temp = ws.Path("TempRoot");
        Directory.CreateDirectory(temp);
        var old = Folder(temp, "0123456789abcdef0123456789abcdef", TimeSpan.FromDays(3));
        var recent = Folder(temp, "fedcba9876543210fedcba9876543210", TimeSpan.FromHours(2));
        var stillWriting = Folder(temp, "long-encode", TimeSpan.FromDays(2), freshFileInside: true);
        var cache = Folder(temp, "magick-opencl", TimeSpan.FromDays(30));
        var looseFile = Path.Combine(temp, "loose.txt");
        File.WriteAllText(looseFile, "x");
        File.SetLastWriteTimeUtc(looseFile, Now - TimeSpan.FromDays(9));

        var report = Housekeeping.Run(temp, ws.Path("no-logs"), Now, ws.Log);

        Assert.Equal(1, report.TempFolders);
        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(recent));
        Assert.True(Directory.Exists(stillWriting));
        Assert.True(Directory.Exists(cache));
        Assert.True(File.Exists(looseFile)); // only sub-folders are cleaned
    }

    [Fact]
    public void Logs_older_than_two_weeks_are_removed_from_the_data_dir_log_folder()
    {
        using var ws = new TestWorkspace("housekeeping");
        // TestWorkspace points CLEMENTINE_DATA_DIR at a temp folder: the default log folder follows it.
        var dataDir = Environment.GetEnvironmentVariable("CLEMENTINE_DATA_DIR");
        Assert.False(string.IsNullOrEmpty(dataDir));
        Assert.StartsWith(Path.GetFullPath(dataDir), Path.GetFullPath(AppPaths.LogDirectory), StringComparison.OrdinalIgnoreCase);
        var id = Guid.NewGuid().ToString("N")[..8];
        var ancient = Log(AppPaths.LogDirectory, $"housekeeping-{id}-old.log", TimeSpan.FromDays(20));
        var lastWeek = Log(AppPaths.LogDirectory, $"housekeeping-{id}-week.log", TimeSpan.FromDays(7));
        var notALog = Log(AppPaths.LogDirectory, $"housekeeping-{id}-notes.txt", TimeSpan.FromDays(60));

        // Unlimited budget: other tests' logs in the shared folder mustn't make this about size.
        var report = Housekeeping.Run(ws.Path("no-temp"), AppPaths.LogDirectory, Now, ws.Log, logBudgetBytes: long.MaxValue);

        Assert.True(report.LogFiles >= 1);
        Assert.False(File.Exists(ancient));
        Assert.True(File.Exists(lastWeek));
        Assert.True(File.Exists(notALog));
        File.Delete(lastWeek);
        File.Delete(notALog);
    }

    [Fact]
    public void Logs_are_capped_by_deleting_the_oldest_and_todays_log_is_never_removed()
    {
        using var ws = new TestWorkspace("housekeeping");
        var logs = ws.Path("logs");
        var today = Log(logs, "clementine-today.log", TimeSpan.Zero, bytes: 600);
        var day1 = Log(logs, "clementine-1.log", TimeSpan.FromDays(1), bytes: 300);
        var day2 = Log(logs, "clementine-2.log", TimeSpan.FromDays(2), bytes: 300);
        var day3 = Log(logs, "clementine-3.log", TimeSpan.FromDays(3), bytes: 50);
        var day4 = Log(logs, "clementine-4.log", TimeSpan.FromDays(4), bytes: 50);

        var report = Housekeeping.Run(ws.Path("no-temp"), logs, Now, ws.Log, currentLog: today, logBudgetBytes: 1000);

        // 600 (today) + 300 (yesterday) fit; the day before would make 1200, so it and everything older go.
        Assert.True(File.Exists(today));
        Assert.True(File.Exists(day1));
        Assert.False(File.Exists(day2));
        Assert.False(File.Exists(day3));
        Assert.False(File.Exists(day4));
        Assert.Equal(3, report.LogFiles);
        Assert.Equal(400, report.LogBytes);

        // Today's log alone over budget: kept anyway; everything else goes.
        Assert.Equal(1, Housekeeping.Run(ws.Path("no-temp"), logs, Now, ws.Log, currentLog: today, logBudgetBytes: 100).LogFiles);
        Assert.True(File.Exists(today));
        Assert.False(File.Exists(day1));
    }

    [Fact]
    public void A_file_in_use_is_left_for_next_time_and_missing_folders_are_fine()
    {
        using var ws = new TestWorkspace("housekeeping");
        var logs = ws.Path("logs");
        var busy = Log(logs, "clementine-busy.log", TimeSpan.FromDays(30));
        var temp = ws.Path("TempRoot");
        var busyFolder = Folder(temp, "busy-job", TimeSpan.FromDays(3));
        HousekeepingReport report;
        using (new FileStream(busy, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (new FileStream(Path.Combine(busyFolder, "part.tmp"), FileMode.Open, FileAccess.Read, FileShare.Read))
            report = Housekeeping.Run(temp, logs, Now, ws.Log, currentLog: Path.Combine(logs, "today.log"));

        Assert.Equal(new HousekeepingReport(0, 0, 0), report);
        Assert.True(File.Exists(busy));
        Assert.Equal(new HousekeepingReport(0, 0, 0), Housekeeping.Run(ws.Path("nope"), ws.Path("nope2"), Now, ws.Log));
    }
}

// ===== Main window: FFmpeg banner =====

public class FfmpegBannerTests
{
    static readonly Availability NeedsFfmpeg = Availability.Unavailable(SampleData.RequiresFfmpeg);

    [Fact]
    public void Banner_shows_only_when_ffmpeg_is_missing_and_most_groups_need_it()
    {
        Assert.True(MainWindow.ShouldShowFfmpegBanner(true, [NeedsFfmpeg, NeedsFfmpeg, Availability.Available]));
        Assert.False(MainWindow.ShouldShowFfmpegBanner(true, [NeedsFfmpeg, Availability.Available]));
        Assert.False(MainWindow.ShouldShowFfmpegBanner(true, [Availability.Unavailable("AVIF encoding isn't available in this build."), Availability.Unavailable("Needs LibreOffice")]));
        Assert.False(MainWindow.ShouldShowFfmpegBanner(false, [NeedsFfmpeg, NeedsFfmpeg]));
        Assert.False(MainWindow.ShouldShowFfmpegBanner(true, []));
    }

    [Fact]
    public void Main_window_shows_the_banner_for_video_but_not_for_images_and_hides_it_once_ffmpeg_is_chosen()
    {
        using var ws = new TestWorkspace("banner");
        var services = HostServices.Create([new SampleData.Provider(missingFfmpeg: true)], SettingsStore.InMemory(), ws.Log);
        var video = InputFile.FromPath(ws.WriteText("clip.mp4", "x"));
        var psd = InputFile.FromPath(ws.WriteText("art.psd", "x"));
        WpfHost.Run(() =>
        {
            var missing = true;
            var window = new MainWindow(services, () => { }, () => { }, ffmpegMissing: () => missing);
            try
            {
                Assert.False(window.IsFfmpegBannerVisible); // drop zone
                window.ShowFiles([video]);
                Assert.True(window.IsFfmpegBannerVisible);
                Assert.Contains(AccessibilityTests.Descendants(window).OfType<Button>(), b => Equals(b.Content, "Get FFmpeg"));
                Assert.Contains(AccessibilityTests.Descendants(window).OfType<TextBlock>(), t => t.Text == "FFmpeg isn't installed — video and audio need it.");
                window.SelectMode(WheelMode.Tools);
                Assert.True(window.IsFfmpegBannerVisible);

                window.ShowFiles([psd]);
                window.SelectMode(WheelMode.Convert);
                Assert.False(window.IsFfmpegBannerVisible);

                window.ShowFiles([video]);
                Assert.True(window.IsFfmpegBannerVisible);
                // Choosing a location (Settings › Tools, or the banner's Choose…) rebuilds the tiles.
                missing = false;
                services.Settings.Update(s => s.FfmpegPath = @"C:\Tools\ffmpeg\bin\ffmpeg.exe");
                Pump();
                Assert.False(window.IsFfmpegBannerVisible);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void No_banner_when_ffmpeg_is_there()
    {
        using var ws = new TestWorkspace("banner");
        var services = HostServices.Create([new SampleData.Provider(missingFfmpeg: true)], SettingsStore.InMemory(), ws.Log);
        WpfHost.Run(() =>
        {
            var window = new MainWindow(services, () => { }, () => { }, ffmpegMissing: () => false);
            try
            {
                window.ShowFiles([InputFile.FromPath(ws.WriteText("clip.mp4", "x"))]);
                Assert.False(window.IsFfmpegBannerVisible);
            }
            finally
            {
                window.Close();
            }
        });
    }

    internal static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
}

// ===== Settings › Wheel: hotkey problem; Help window =====

public class HotkeyProblemTests
{
    const string Taken = "Ctrl+Shift+Space is already used by another app — pick a different shortcut.";

    [Fact]
    public void Wheel_page_shows_why_the_hotkey_is_not_active_and_rereads_it_after_changes()
    {
        using var harness = new SettingsHarness();
        harness.Wheel.HotkeyProblem = Taken;
        WpfHost.Run(() =>
        {
            var window = harness.CreateWindow();
            try
            {
                Assert.Null(window.ShownHotkeyProblem); // General page
                window.ShowPage(SettingsPage.Wheel);
                Assert.Equal(Taken, window.ShownHotkeyProblem);

                // Recording: Backspace turns the hotkey off; the wheel then reports no problem.
                harness.Wheel.ProblemAfterApply = () => null;
                var recorder = Find<Button>(window, "Selection hotkey");
                recorder.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, recorder));
                using (var source = new HwndSource(new HwndSourceParameters("key source") { Width = 1, Height = 1 }))
                    recorder.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Back) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                Assert.Equal("", harness.Services.Settings.Current.SelectionHotkey);
                Assert.Null(window.ShownHotkeyProblem);

                // Any other change the wheel applies is re-read too.
                harness.Wheel.ProblemAfterApply = () => "Couldn't register Ctrl+Shift+Space (error 5).";
                var toggle = Find<CheckBox>(window, "Show the wheel while dragging");
                toggle.IsChecked = false;
                toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, toggle));
                Assert.Equal("Couldn't register Ctrl+Shift+Space (error 5).", window.ShownHotkeyProblem);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void The_default_selection_hotkey_avoids_altgr()
    {
        Assert.Equal("Ctrl+Shift+Space", new ClementineSettings().SelectionHotkey);
        Assert.True(KeyText.TryParseHotkey(new ClementineSettings().SelectionHotkey, out var modifiers, out var key));
        Assert.Equal(ModifierKeys.Control | ModifierKeys.Shift, modifiers);
        Assert.Equal(Key.Space, key);
    }

    [Fact]
    public void Help_window_follows_settings_changes_while_open()
    {
        var store = SettingsStore.InMemory();
        WpfHost.Run(() =>
        {
            var help = new HelpWindow(store);
            try
            {
                var keys = help.StepKeys();
                Assert.Equal("Shift", keys[0]);
                Assert.Equal("Ctrl+Shift+Space", keys[^1]);

                store.Update(s =>
                {
                    s.ConvertModifiers = TriggerModifiers.Control | TriggerModifiers.Windows;
                    s.SelectionHotkey = "";
                });
                var updated = help.StepKeys();
                Assert.Equal("Ctrl+Win", updated[0]);
                Assert.Equal(keys.Count - 1, updated.Count); // no hotkey step any more
            }
            finally
            {
                help.Close();
            }
            // Closed: no longer listening.
            store.Update(s => s.ConvertModifiers = TriggerModifiers.Alt);
            Assert.Equal("Ctrl+Win", help.StepKeys()[0]);
        });
    }

    static T Find<T>(Window window, string name) where T : Control =>
        AccessibilityTests.Descendants(window).OfType<T>().FirstOrDefault(c => AccessibilityTests.NameOf(c) == name)
        ?? throw new InvalidOperationException($"No {typeof(T).Name} named '{name}'.");
}
