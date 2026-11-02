using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Clementine.App.Interop;
using Clementine.App.Preferences;
using Clementine.App.Startup;
using Clementine.App.Toasts;
using Clementine.Core;
using Clementine.Core.Hosting;
using Clementine.TestSupport;
using Microsoft.Win32;

namespace Clementine.App.Tests;

/// <summary>IPC hardening: the pipe is reachable by every process of this user, so bad input must be harmless.</summary>
public class InstanceChannelRobustnessTests
{
    public static TheoryData<string> Garbage => new()
    {
        "",
        "not json at all",
        "null",
        "{}",
        """{"Args":null,"WorkingDirectory":"C:\\"}""",
        """{"Args":"--wheel","WorkingDirectory":"C:\\"}""",
        """{"Args":[1,2,3]}""",
        """{"Args":[["nested"]]}""",
        new string('[', 5000),
        """{"Args":["--wheel"],"WorkingDirectory":"C:\\"} trailing""",
    };

    [Theory]
    [MemberData(nameof(Garbage))]
    public void Malformed_messages_are_ignored_without_throwing(string payload) =>
        Assert.Null(InstanceChannel.Decode(Encoding.UTF8.GetBytes(payload)));

    [Fact]
    public void Invalid_utf8_and_huge_argument_lists_are_ignored()
    {
        Assert.Null(InstanceChannel.Decode(new byte[] { 0xFF, 0xFE, 0x00, 0x7B, 0xC3 }));
        var huge = JsonSerializer.SerializeToUtf8Bytes(new InstanceMessage(Enumerable.Repeat("x", 40_000).ToArray(), @"C:\"));
        Assert.Null(InstanceChannel.Decode(huge));
    }

    [Fact]
    public void Null_arguments_are_dropped_and_a_missing_directory_falls_back()
    {
        var message = InstanceChannel.Decode("""{"Args":[null,"--wheel","clip – ünïcødé \"1\".mov",null],"WorkingDirectory":null}"""u8);
        Assert.NotNull(message);
        Assert.Equal(["--wheel", "clip – ünïcødé \"1\".mov"], message.Args);
        Assert.Equal(Environment.CurrentDirectory, message.WorkingDirectory);
        Assert.Equal(ShellCommandKind.Wheel, message.ToCommand().Kind);
    }

    [Fact]
    public async Task The_server_survives_garbage_and_silent_clients_and_still_delivers_the_next_launch()
    {
        var pipe = $"Clementine.Test.{Guid.NewGuid():N}";
        var log = new RecordingLog();
        using var server = new InstanceChannel.Server(pipe, log, listeners: 2);
        var received = new TaskCompletionSource<InstanceMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.MessageReceived += (_, m) => received.TrySetResult(m);

        using (var raw = new NamedPipeClientStream(".", pipe, PipeDirection.Out, PipeOptions.CurrentUserOnly))
        {
            raw.Connect(5000);
            raw.Write("\0\0garbage{{{"u8);
        }
        using (var silent = new NamedPipeClientStream(".", pipe, PipeDirection.Out, PipeOptions.CurrentUserOnly))
            silent.Connect(5000);

        Assert.True(InstanceChannel.TrySend(pipe, new InstanceMessage(["--open", @"D:\a b\c.psd"], @"C:\"), TimeSpan.FromSeconds(5)));
        var message = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(["--open", @"D:\a b\c.psd"], message.Args);
    }

    [Fact]
    public async Task Launches_that_arrive_before_anyone_listens_are_delivered_in_order()
    {
        var pipe = $"Clementine.Test.{Guid.NewGuid():N}";
        using var server = new InstanceChannel.Server(pipe, new RecordingLog(), listeners: 1);
        for (var i = 1; i <= 3; i++)
            Assert.True(InstanceChannel.TrySend(pipe, new InstanceMessage(["--wheel", $@"C:\clip{i}.mp4"], @"C:\"), TimeSpan.FromSeconds(5)));
        await Task.Delay(300);

        var seen = new List<string>();
        server.MessageReceived += (_, m) =>
        {
            lock (seen)
                seen.Add(m.Args[1]);
        };
        Assert.Equal([@"C:\clip1.mp4", @"C:\clip2.mp4", @"C:\clip3.mp4"], seen);
    }

    [Fact]
    public void Forwarding_finds_the_listening_process_to_hand_it_the_foreground()
    {
        var pipe = $"Clementine.Test.{Guid.NewGuid():N}";
        using var server = new InstanceChannel.Server(pipe, new RecordingLog(), listeners: 1);
        Assert.True(InstanceChannel.TrySend(pipe, new InstanceMessage(["--settings"], @"C:\"), TimeSpan.FromSeconds(5), out var serverProcessId));
        // AllowSetForegroundWindow is called for this process id; here the "running instance" is the test host itself.
        Assert.Equal(Environment.ProcessId, serverProcessId);
    }

    [Fact]
    public async Task A_pipe_name_that_cannot_be_joined_backs_off_instead_of_spinning()
    {
        var pipe = $"Clementine.Test.{Guid.NewGuid():N}";
        // Someone else holds the name with a one-instance limit, so every listener's attempt fails at once.
        using var squatter = new NamedPipeServerStream(pipe, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
        var log = new RecordingLog();
        using (new InstanceChannel.Server(pipe, log, listeners: 4))
            await Task.Delay(1500);
        var warnings = log.Lines.Count(l => l.Contains("Instance pipe", StringComparison.Ordinal));
        Assert.InRange(warnings, 1, 12);
    }
}

/// <summary>Program.AcquireOrForward: the launch path every Explorer context-menu click and Send To takes.</summary>
public class ForwardingTests
{
    static readonly string[] WheelArgs = ["--wheel", @"C:\Clips\Opener – ünïcødé.mov"];

    [Fact]
    public async Task The_first_launch_becomes_the_primary_instance()
    {
        var (mutex, pipe) = Names();
        var result = await OnThread(() =>
        {
            using var instance = Program.AcquireOrForward(WheelArgs, CommandLine.Parse(WheelArgs), out var code, mutex, pipe, TimeSpan.FromSeconds(2));
            return (Primary: instance?.IsPrimary == true, code);
        });
        Assert.True(result.Primary);
        Assert.Equal(0, result.code);
    }

    [Fact]
    public async Task Quit_with_nothing_running_exits_quietly()
    {
        var (mutex, pipe) = Names();
        var result = await OnThread(() =>
        {
            var instance = Program.AcquireOrForward(["--quit"], CommandLine.Parse(["--quit"]), out var code, mutex, pipe, TimeSpan.FromSeconds(2));
            return (Instance: instance, code);
        });
        Assert.Null(result.Instance);
        Assert.Equal(0, result.code);
    }

    [Fact]
    public async Task A_later_launch_forwards_its_arguments_and_exits()
    {
        var (mutex, pipe) = Names();
        using var owner = new MutexOwner(mutex);
        using var server = new InstanceChannel.Server(pipe, new RecordingLog(), listeners: 2);
        var received = new TaskCompletionSource<InstanceMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.MessageReceived += (_, m) => received.TrySetResult(m);

        var result = await OnThread(() =>
        {
            var instance = Program.AcquireOrForward(WheelArgs, CommandLine.Parse(WheelArgs), out var code, mutex, pipe, TimeSpan.FromSeconds(5));
            return (Instance: instance, code);
        });
        Assert.Null(result.Instance);
        Assert.Equal(0, result.code);
        Assert.Equal(WheelArgs, (await received.Task.WaitAsync(TimeSpan.FromSeconds(10))).Args);
    }

    [Fact]
    public async Task A_launch_that_races_a_quitting_instance_takes_over()
    {
        var (mutex, pipe) = Names();
        var owner = new MutexOwner(mutex);
        _ = Task.Delay(700).ContinueWith(_ => owner.Dispose(), TaskScheduler.Default);
        var result = await OnThread(() =>
        {
            using var instance = Program.AcquireOrForward(WheelArgs, CommandLine.Parse(WheelArgs), out var code, mutex, pipe, TimeSpan.FromSeconds(8));
            return instance?.IsPrimary == true;
        });
        Assert.True(result);
    }

    [Fact]
    public async Task A_running_instance_that_never_answers_makes_the_launch_give_up()
    {
        var (mutex, pipe) = Names();
        using var owner = new MutexOwner(mutex);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var result = await OnThread(() =>
        {
            var instance = Program.AcquireOrForward(WheelArgs, CommandLine.Parse(WheelArgs), out var code, mutex, pipe, TimeSpan.FromSeconds(1.2));
            return (Instance: instance, code);
        });
        Assert.Null(result.Instance);
        Assert.Equal(1, result.code);
        Assert.InRange(clock.Elapsed.TotalSeconds, 1.0, 6);
    }

    static (string Mutex, string Pipe) Names()
    {
        var id = Guid.NewGuid().ToString("N");
        return ($"Clementine.Test.Mutex.{id}", $"Clementine.Test.Pipe.{id}");
    }

    static Task<T> OnThread<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                tcs.SetResult(func());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        thread.Start();
        return tcs.Task;
    }

    /// <summary>Holds a single-instance mutex on its own thread (mutex ownership is per thread), like a running app.</summary>
    sealed class MutexOwner : IDisposable
    {
        readonly ManualResetEventSlim _release = new();
        readonly Thread _thread;

        public MutexOwner(string name)
        {
            using var acquired = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                using var instance = SingleInstance.Acquire(name);
                acquired.Set();
                _release.Wait();
            });
            _thread.Start();
            Assert.True(acquired.Wait(TimeSpan.FromSeconds(10)));
        }

        public void Dispose()
        {
            _release.Set();
            _thread.Join(TimeSpan.FromSeconds(10));
        }
    }
}

public class WheelStragglerTests
{
    static readonly DateTimeOffset T0 = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_selection_still_streaming_in_after_the_max_delay_ends_up_in_one_wheel()
    {
        // 40 files from Explorer on a busy PC: one launch every 100 ms, longer than the 2.5 s maximum wait.
        var aggregator = new WheelRequestAggregator(TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(2.5));
        var shown = new List<IReadOnlyList<string>>();
        for (var ms = 0; ms <= 5000; ms += 20)
        {
            var now = T0.AddMilliseconds(ms);
            if (ms % 100 == 0 && ms / 100 < 40)
                aggregator.Add([$@"C:\Clips\clip{ms / 100:00}.mov"], WheelMode.Convert, now);
            if (aggregator.TryFlush(now) is { } batch)
                shown.Add(batch.Paths);
        }
        Assert.Equal(2, shown.Count);
        Assert.Equal(26, shown[0].Count); // the partial wheel at 2.5 s…
        Assert.Equal(40, shown[1].Count); // …is replaced by one with every file
        Assert.Equal(Enumerable.Range(0, 40).Select(i => $@"C:\Clips\clip{i:00}.mov"), shown[1]);
    }

    [Fact]
    public void A_new_request_after_a_quiet_gap_is_its_own_wheel()
    {
        var aggregator = new WheelRequestAggregator(TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(1));
        for (var i = 0; i < 6; i++)
            aggregator.Add([$@"C:\{i}.mov"], WheelMode.Convert, T0.AddMilliseconds(i * 200));
        Assert.Equal(6, aggregator.TryFlush(T0.AddMilliseconds(1000))!.Value.Paths.Count); // cut short at 1 s
        aggregator.Add([@"C:\6.mov"], WheelMode.Convert, T0.AddMilliseconds(1200));
        aggregator.Add([@"C:\7.mov"], WheelMode.Convert, T0.AddMilliseconds(1400));
        Assert.Null(aggregator.TryFlush(T0.AddMilliseconds(1700)));
        Assert.Equal(8, aggregator.TryFlush(T0.AddMilliseconds(1800))!.Value.Paths.Count); // the rest joins it

        // Seconds later: a different right-click, not a continuation.
        aggregator.Add([@"C:\other.mov"], WheelMode.Convert, T0.AddSeconds(5));
        Assert.Equal([@"C:\other.mov"], aggregator.TryFlush(T0.AddSeconds(6))!.Value.Paths);
    }

    [Fact]
    public void Stragglers_of_another_mode_do_not_join()
    {
        var aggregator = new WheelRequestAggregator(TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(1));
        for (var i = 0; i < 6; i++)
            aggregator.Add([$@"C:\{i}.mov"], WheelMode.Convert, T0.AddMilliseconds(i * 200));
        Assert.NotNull(aggregator.TryFlush(T0.AddMilliseconds(1000)));
        aggregator.Add([@"C:\x.mov"], WheelMode.Tools, T0.AddMilliseconds(1100));
        Assert.Equal([@"C:\x.mov"], aggregator.TryFlush(T0.AddMilliseconds(1500))!.Value.Paths);
    }
}

public class InstanceNameTests
{
    [Fact]
    public void A_redirected_profile_gets_its_own_instance_names()
    {
        Assert.Equal("", SingleInstance.ProfileScope(null));
        Assert.Equal("", SingleInstance.ProfileScope("  "));
        var a = SingleInstance.ProfileScope(@"C:\Temp\ClementineTests\data");
        Assert.Matches("^\\.[0-9A-F]{16}$", a);
        Assert.Equal(a, SingleInstance.ProfileScope(@"c:\temp\clementinetests\DATA\"));
        Assert.NotEqual(a, SingleInstance.ProfileScope(@"C:\Temp\ClementineTests\other"));
    }

    [Fact]
    public void Pipe_names_are_per_user_and_per_session()
    {
        Assert.Equal("Clementine.Pipe.S-1-5-21-1.2", SingleInstance.PipeNameFor("S-1-5-21-1", 2));
        Assert.NotEqual(SingleInstance.PipeNameFor("S-1-5-21-1", 1), SingleInstance.PipeNameFor("S-1-5-21-1", 2));
        Assert.NotEqual(SingleInstance.PipeNameFor("S-1-5-21-1", 1), SingleInstance.PipeNameFor("S-1-5-21-2", 1));
    }
}

public class ExplorerArgumentTests
{
    [Theory]
    // Explorer quotes %1; for a drive root "D:\" the C runtime turns the \" into a literal quote: D:"
    [InlineData("D:\"", @"D:\")]
    [InlineData("d:", @"d:\")]
    // A folder with a trailing backslash comes through the same way.
    [InlineData("C:\\VJ Loops\\Festival\"", @"C:\VJ Loops\Festival")]
    [InlineData("\"C:\\Clips\\Opener – ünïcødé.mov\"", @"C:\Clips\Opener – ünïcødé.mov")]
    public void Paths_from_explorer_resolve_to_what_was_clicked(string arg, string expected)
    {
        var command = CommandLine.Parse(["--wheel", arg], @"C:\Windows\System32");
        Assert.Equal([expected], command.Paths);
    }
}

public class AccessibilityTests
{
    [Fact]
    public void Icon_only_buttons_are_announced_by_their_tool_tip()
    {
        WpfHost.Run(() =>
        {
            var card = new ToastCard();
            card.Apply(new ToastState { Kind = ToastKind.Running, Title = "Converting", Progress = 0.3 });
            Assert.Equal("Cancel", NameOf(card.CloseButton));
            card.Apply(new ToastState { Kind = ToastKind.Succeeded, Title = "Converted", Outputs = [@"C:\a.mov"] });
            Assert.Equal("Dismiss", NameOf(card.CloseButton));
            Assert.Equal("Converted", AutomationProperties.GetName(card));
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(card));
        });
    }

    [Fact]
    public void Every_control_on_every_settings_page_has_an_accessible_name()
    {
        using var settings = new SettingsHarness();
        WpfHost.Run(() =>
        {
            var window = settings.CreateWindow();
            try
            {
                var unnamed = new List<string>();
                foreach (var page in Enum.GetValues<SettingsPage>())
                {
                    window.ShowPage(page);
                    foreach (var control in Descendants(window).OfType<Control>().Where(IsInteractive))
                    {
                        if (string.IsNullOrWhiteSpace(NameOf(control)))
                            unnamed.Add($"{page}: {control.GetType().Name}");
                    }
                }
                Assert.True(unnamed.Count == 0, "Unnamed controls: " + string.Join(", ", unnamed.Distinct()));
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// Text that falls back to the system text colour (black normally, white or yellow in high contrast) would vanish on
    /// the dark surfaces: every window must set its own foreground for its text to inherit.
    /// </summary>
    [Fact]
    public void No_text_falls_back_to_the_system_text_colour()
    {
        using var harness = new SettingsHarness();
        var shelfFile = harness.Workspace.Path("shelf.json");
        WpfHost.Run(() =>
        {
            var windows = new List<Window>
            {
                new Main.MainWindow(harness.Services, () => { }, () => { }),
                new Dialogs.HelpWindow(harness.Services.Settings.Current),
                new Dialogs.ErrorDialog("Something failed.", "details"),
                new Dialogs.TextViewerWindow("Media info", "clip.mov", "line 1\nline 2"),
                new Shelf.ShelfWindow(new Shelf.ShelfStore(shelfFile, harness.Workspace.Log), harness.Workspace.Log),
            };
            var settings = harness.CreateWindow();
            windows.Add(settings);
            var problems = new List<string>();
            try
            {
                foreach (var window in windows)
                {
                    var pages = window == settings ? Enum.GetValues<SettingsPage>() : [SettingsPage.General];
                    foreach (var page in pages)
                    {
                        if (window == settings)
                            settings.ShowPage(page);
                        foreach (var text in Descendants(window).OfType<TextBlock>())
                        {
                            if (DependencyPropertyHelper.GetValueSource(text, TextBlock.ForegroundProperty).BaseValueSource == BaseValueSource.Default)
                                problems.Add($"{window.GetType().Name}: \"{text.Text}\"");
                        }
                    }
                }
            }
            finally
            {
                foreach (var window in windows)
                    window.Close();
            }
            Assert.True(problems.Count == 0, "Text with the system colour: " + string.Join(", ", problems.Distinct().Take(10)));
        });
    }

    static bool IsInteractive(Control control) => control is ButtonBase or RangeBase or Selector or System.Windows.Controls.Primitives.TextBoxBase or PasswordBox;

    internal static string NameOf(UIElement element) => UIElementAutomationPeer.CreatePeerForElement(element)?.GetName() ?? "";

    internal static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var nested in Descendants(child))
                yield return nested;
        }
    }
}

public class SettingsLiveTests
{
    [Fact]
    public void Wheel_keys_and_the_wheel_switch_reach_the_running_wheel_at_once()
    {
        using var harness = new SettingsHarness();
        WpfHost.Run(() =>
        {
            var window = harness.CreateWindow();
            try
            {
                window.ShowPage(SettingsPage.Wheel);
                Click(Find<ToggleButton>(window, "Convert wheel Win"));
                Assert.Equal(TriggerModifiers.Shift | TriggerModifiers.Windows, harness.Services.Settings.Current.ConvertModifiers);
                Assert.True(harness.Wheel.Applied >= 1);

                Click(Find<ToggleButton>(window, "Actions wheel Alt"));
                Assert.Equal(TriggerModifiers.Shift, harness.Services.Settings.Current.ActionsModifiers);

                // Shift + Ctrl is already the Tools wheel: shown as a problem, not saved, not applied.
                var applied = harness.Wheel.Applied;
                Click(Find<ToggleButton>(window, "Actions wheel Ctrl"));
                Assert.Equal(TriggerModifiers.Shift, harness.Services.Settings.Current.ActionsModifiers);
                Assert.Equal(applied, harness.Wheel.Applied);

                Click(Find<CheckBox>(window, "Show the wheel while dragging"));
                Assert.False(harness.Services.Settings.Current.WheelEnabled);
                Assert.True(harness.Wheel.Applied > applied);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Launch_at_login_is_written_only_to_the_test_key_when_flipped()
    {
        using var harness = new SettingsHarness();
        WpfHost.Run(() =>
        {
            var window = harness.CreateWindow();
            try
            {
                window.ShowPage(SettingsPage.General);
                Assert.False(harness.Registration.IsLaunchAtLoginEnabled());
                Click(Find<CheckBox>(window, "Start with Windows"));
                Assert.True(harness.Registration.IsLaunchAtLoginEnabled());
                Assert.True(harness.Services.Settings.Current.LaunchAtLogin);
                Click(Find<CheckBox>(window, "Start with Windows"));
                Assert.False(harness.Registration.IsLaunchAtLoginEnabled());
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task The_heavy_jobs_slider_lets_queued_jobs_start_without_a_restart()
    {
        using var harness = new SettingsHarness(s => s.MaxConcurrentHeavyJobs = 1);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = 0;
        var op = new DelegateOperation(
            new OperationInfo { Id = "test.heavy", Category = OperationCategory.Convert, Group = "Heavy", Title = "Heavy", IconKey = "convert", Weight = OperationWeight.Heavy },
            _ => true,
            async (_, _, ct) =>
            {
                Interlocked.Increment(ref running);
                await release.Task.WaitAsync(ct);
                return new OperationResult();
            });
        var inputs = Enumerable.Range(1, 3).Select(i => InputFile.FromPath(harness.Workspace.WriteText($"clip{i}.mov", "x"))).ToList();
        var jobs = harness.Services.Jobs.Enqueue(op, inputs, null);
        try
        {
            await WaitUntil(() => Volatile.Read(ref running) == 1);
            await Task.Delay(300);
            Assert.Equal(1, Volatile.Read(ref running));

            WpfHost.Run(() =>
            {
                var window = harness.CreateWindow();
                try
                {
                    window.ShowPage(SettingsPage.Performance);
                    Find<Slider>(window, "Heavy jobs at once").Value = 3;
                }
                finally
                {
                    window.Close();
                }
            });
            Assert.Equal(3, harness.Services.Settings.Current.MaxConcurrentHeavyJobs);
            await WaitUntil(() => Volatile.Read(ref running) == 3);
        }
        finally
        {
            release.TrySetResult();
            foreach (var job in jobs)
                job.Cancel();
        }
    }

    [Fact]
    public void A_tool_path_override_is_used_without_a_restart()
    {
        using var harness = new SettingsHarness();
        var fake = harness.Workspace.WriteText(@"tools\ffmpeg.exe", "not really ffmpeg");
        _ = harness.Services.Tools.Ffmpeg; // cache the auto-detected location first
        harness.Services.Settings.Update(s => s.FfmpegPath = fake);
        Assert.Equal(fake, harness.Services.Tools.Ffmpeg, StringComparer.OrdinalIgnoreCase);
    }

    static T Find<T>(Window window, string name) where T : Control =>
        AccessibilityTests.Descendants(window).OfType<T>().FirstOrDefault(c => AccessibilityTests.NameOf(c) == name)
        ?? throw new InvalidOperationException($"No {typeof(T).Name} named '{name}'.");

    /// <summary>What a mouse click does to a toggle: flip it, then raise Click (the settings window listens to Click only).</summary>
    static void Click(ToggleButton toggle)
    {
        toggle.IsChecked = toggle.IsChecked != true;
        toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, toggle));
    }

    static async Task WaitUntil(Func<bool> condition)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(10))
                throw new TimeoutException("Condition not reached.");
            await Task.Delay(20);
        }
    }
}

public class TrayTests
{
    [Fact]
    public void The_tray_menu_is_built_on_first_use_and_shows_the_pause_state_set_before()
    {
        WpfHost.Run(() =>
        {
            var noop = () => { };
            using var tray = new Tray.TrayIcon(new Tray.TrayCommands(noop, noop, noop, noop, noop, noop));
            Assert.False(tray.HasMenu); // nothing WPF-heavy before the icon appears
            tray.SetPaused(true);
            Assert.False(tray.HasMenu);
            Assert.Equal(8, tray.MenuForTest.Items.Count);
            Assert.Equal("Resume wheel", tray.PauseItemHeader);
            tray.SetPaused(false);
            Assert.Equal("Pause wheel", tray.PauseItemHeader);
        });
    }
}

public class CrashGuardTests
{
    [Fact]
    public void Tray_exceptions_are_logged_instead_of_raising_the_winforms_crash_dialog()
    {
        var log = new RecordingLog();
        WpfHost.Run(() =>
        {
            CrashGuard.InstallWinForms(log);
            // What NotifyIcon's window procedure does with an exception thrown by a click handler.
            System.Windows.Forms.Application.OnThreadException(new InvalidOperationException("tray click failed"));
            foreach (var dialog in Application.Current.Windows.OfType<Dialogs.ErrorDialog>().ToList())
                dialog.Close();
        });
        Assert.Contains(log.Lines, l => l.Contains("tray click failed", StringComparison.Ordinal));
    }
}

public class ToastPlacementTests
{
    [Fact]
    public void Toasts_anchored_to_an_unplugged_monitor_move_to_a_connected_one()
    {
        var gone = new MonitorArea(new Int32Rect(-90000, -90000, 1900, 1040), new Int32Rect(-90000, -90000, 1920, 1080), 1.5);
        var now = ScreenGeometry.Refresh(gone);
        Assert.True(now.Bounds.Width >= 320 && now.Bounds.Height >= 200);
        Assert.True(now.Bounds.X > -90000);
        Assert.True(now.WorkArea.X >= now.Bounds.X && now.WorkArea.Y >= now.Bounds.Y);
        Assert.True(now.WorkArea.X + now.WorkArea.Width <= now.Bounds.X + now.Bounds.Width);
    }
}

public class NoticeTests
{
    static readonly string[] TestOnly = ["xunit", "Microsoft.", "System.", "NuGet.", "Newtonsoft.Json", "coverlet"];

    [Fact]
    public void Third_party_notices_ship_next_to_the_app_with_the_magick_notice()
    {
        var notices = Licences.NoticesFile();
        Assert.NotNull(notices);
        var text = File.ReadAllText(notices);
        foreach (var required in new[] { "FFmpeg", "LGPL", "GPL", "Magick.NET", "ImageMagick", "libheif", "PDFsharp", "MIT", "PdfPig", "PDFium",
                     "SkiaSharp", "Open XML SDK", "Markdig", "AngleSharp", "SharpCompress", "YamlDotNet", "ZXing.Net", "LibreOffice", "7-Zip", "Edge" })
            Assert.Contains(required, text, StringComparison.Ordinal);

        var magick = Path.Combine(AppContext.BaseDirectory, Licences.MagickNoticeRelativePath);
        Assert.True(File.Exists(magick), magick);
        Assert.Contains("ImageMagick", File.ReadAllText(magick), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_bundled_package_is_named_in_the_notices_and_has_a_known_licence()
    {
        var deps = Path.Combine(AppContext.BaseDirectory, "Clementine.App.Tests.deps.json");
        var text = File.ReadAllText(Licences.NoticesFile()!);
        var packages = Licences.BundledPackages(deps)
            .Where(p => !TestOnly.Any(t => p.Name.StartsWith(t, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        Assert.NotEmpty(packages);
        var missing = packages.Where(p => !Candidates(p.Name).Any(c => text.Contains(c, StringComparison.OrdinalIgnoreCase))).Select(p => p.Name).ToList();
        Assert.True(missing.Count == 0, "Not in THIRD-PARTY-NOTICES.txt: " + string.Join(", ", missing));

        var unknown = packages.Where(p => p.Licence.Contains("nuget.org", StringComparison.Ordinal)).Select(p => p.Name).ToList();
        Assert.True(unknown.Count == 0, "No licence for: " + string.Join(", ", unknown));
    }

    [Fact]
    public void About_rows_group_packages_into_families_and_list_the_external_programs()
    {
        var deps = Path.Combine(AppContext.BaseDirectory, "Clementine.App.Tests.deps.json");
        var rows = Licences.BundledComponents(deps).Where(r => !TestOnly.Any(t => r.Name.StartsWith(t, StringComparison.OrdinalIgnoreCase))).ToList();
        var magick = Assert.Single(rows, r => r.Name.StartsWith("Magick.NET", StringComparison.Ordinal));
        Assert.Contains("LGPL", magick.Note, StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(magick.Version));
        Assert.Single(rows, r => r.Name.StartsWith("SkiaSharp", StringComparison.Ordinal));
        Assert.All(rows, r => Assert.False(string.IsNullOrEmpty(r.Url), r.Name));
        // FFmpeg and 7-Zip ship in the tools folder; LibreOffice and Edge are used when present, never included.
        Assert.Equal(["FFmpeg", "7-Zip (7za.exe)"], Licences.BundledPrograms.Select(t => t.Name));
        Assert.All(Licences.BundledPrograms, t => Assert.False(t.IsExternal));
        Assert.Equal([Licences.BundledFfmpegVersion, Licences.BundledSevenZipVersion], Licences.BundledPrograms.Select(t => t.Version));
        Assert.Contains("FFmpeg-SOURCE.txt", Licences.BundledPrograms[0].Note, StringComparison.Ordinal);
        Assert.Equal(["LibreOffice", "Microsoft Edge"], Licences.ExternalPrograms.Select(t => t.Name));
        Assert.All(Licences.ExternalPrograms, t => Assert.True(t.IsExternal));
        Assert.StartsWith("Optional", Licences.ExternalPrograms[0].Note, StringComparison.Ordinal);
        Assert.DoesNotContain("never checks for updates", rows.Single(r => r.Name == "Velopack").Note, StringComparison.Ordinal);
    }

    /// <summary>"SkiaSharp.NativeAssets.Win32" → itself, "SkiaSharp.NativeAssets", "SkiaSharp".</summary>
    static IEnumerable<string> Candidates(string id)
    {
        for (var name = id; name.Length >= 6; name = name.Contains('.') ? name[..name.LastIndexOf('.')] : "")
            yield return name;
    }
}

/// <summary>A settings window over in-memory settings, a recording wheel, a test registry key and a temp Send To folder.</summary>
internal sealed class SettingsHarness : IDisposable
{
    const string TestRoot = @"Software\ClementineTests";
    readonly string _root = $@"{TestRoot}\SettingsWindow-{Guid.NewGuid():N}";

    public SettingsHarness(Action<ClementineSettings>? seed = null)
    {
        Workspace = new TestWorkspace("settings");
        var settings = new ClementineSettings();
        seed?.Invoke(settings);
        Services = HostServices.Create([], SettingsStore.InMemory(settings), Workspace.Log);
        Services.Wheel = Wheel;
        Registration = new ShellRegistration(@"C:\Program Files\Clementine\Clementine.exe", Registry.CurrentUser, _root + @"\Run", _root + @"\Classes");
        SendTo = new SendToShortcut(Workspace.Path("SendTo"), @"C:\Program Files\Clementine\Clementine.exe");
    }

    public TestWorkspace Workspace { get; }

    public HostServices Services { get; }

    public RecordingWheel Wheel { get; } = new();

    public ShellRegistration Registration { get; }

    public SendToShortcut SendTo { get; }

    /// <summary>The window, with an updater (default: an installed copy that hasn't checked yet, over a fake source).</summary>
    public SettingsWindow CreateWindow(Updates.Updater? updates = null, Action? restartToUpdate = null) =>
        new(Services, Registration, SendTo, tools: [], updates ?? DefaultUpdater, restartToUpdate);

    Updates.Updater DefaultUpdater => _defaultUpdater ??= new Updates.Updater(new FakeUpdateBackend(), () => true, Workspace.Log);

    Updates.Updater? _defaultUpdater;

    public void Dispose()
    {
        Services.Jobs.CancelAll();
        _defaultUpdater?.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false);
        using (var parent = Registry.CurrentUser.OpenSubKey(TestRoot, writable: true))
        {
            if (parent is not null && parent.SubKeyCount == 0 && parent.ValueCount == 0)
            {
                parent.Dispose();
                Registry.CurrentUser.DeleteSubKey(TestRoot, throwOnMissingSubKey: false);
            }
        }
        Workspace.Dispose();
    }

    public sealed class RecordingWheel : IWheelPresenter
    {
        public int Applied;

        /// <summary>What <see cref="HotkeyProblem"/> becomes on the next <see cref="ApplySettings"/> (null: unchanged).</summary>
        public Func<string?>? ProblemAfterApply { get; set; }

        public string? HotkeyProblem { get; set; }

        public void Start()
        {
        }

        public void Stop()
        {
        }

        public void ApplySettings()
        {
            Applied++;
            if (ProblemAfterApply is { } problem)
                HotkeyProblem = problem();
        }

        public void ShowForFiles(IReadOnlyList<InputFile> files, WheelMode mode, ScreenPoint? at = null)
        {
        }

        public void RenderPreviews(string outputDirectory)
        {
        }
    }
}
