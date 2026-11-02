using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Clementine.Core;
using Clementine.Core.Hosting;
using Clementine.TestSupport;

namespace Clementine.Wheel.Tests;

/// <summary>
/// Exercises the Win32 plumbing for real. Every hook and hotkey installed here is removed before the test ends,
/// and no window is ever shown.
/// </summary>
public class NativeIntegrationTests
{
    [Fact]
    public void Mouse_hook_runs_on_its_own_thread_and_is_removed_on_stop()
    {
        Sta.Run(() =>
        {
            var monitor = new DragMonitor(Dispatcher.CurrentDispatcher, new RecordingLog());
            try
            {
                monitor.Configure(ModifierBindings.Default);
                Assert.True(monitor.Start());
                Assert.True(monitor.IsRunning);
                Assert.True(monitor.Start(), "Starting twice is harmless.");
            }
            finally
            {
                monitor.Dispose();
            }
            Assert.False(monitor.IsRunning);
        });
    }

    [Fact]
    public void Hook_thread_runs_its_timers_while_installed()
    {
        Sta.Run(() =>
        {
            var monitor = new DragMonitor(Dispatcher.CurrentDispatcher, new RecordingLog(), TimeSpan.FromMilliseconds(40));
            try
            {
                Assert.True(monitor.Start());
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (monitor.HealthChecks < 3 && DateTime.UtcNow < deadline)
                    Thread.Sleep(20);
                Assert.True(monitor.HealthChecks >= 3, $"Only {monitor.HealthChecks} health checks ran.");
            }
            finally
            {
                monitor.Dispose();
            }
            var after = monitor.HealthChecks;
            Thread.Sleep(150);
            Assert.Equal(after, monitor.HealthChecks);
        });
    }

    [Fact]
    public void Hotkey_registers_replaces_and_unregisters()
    {
        Sta.Run(() =>
        {
            using var hotkey = new HotkeyManager(new RecordingLog());
            Assert.True(HotkeyGesture.TryParse("Ctrl+Alt+Shift+F22", out var first));
            Assert.True(HotkeyGesture.TryParse("Ctrl+Alt+Shift+F23", out var second));
            Assert.True(hotkey.Register(first));
            Assert.True(hotkey.Register(second));
            Assert.Equal(second, hotkey.Registered);
            hotkey.Unregister();
            Assert.Null(hotkey.Registered);
        });
    }

    [Fact]
    public void Host_follows_settings_and_cleans_up_on_stop()
    {
        Sta.Run(() =>
        {
            var settings = SettingsStore.InMemory(new ClementineSettings { SelectionHotkey = "Ctrl+Alt+Shift+F21" });
            var services = HostServices.Create([], settings, new RecordingLog());
            var host = new WheelHost(services);
            try
            {
                host.Start();
                Assert.True(host.IsMonitoring);
                Assert.Equal("Ctrl+Alt+Shift+F21", host.RegisteredHotkey?.ToString());

                settings.Update(s => s.WheelEnabled = false);
                host.ApplySettings();
                Assert.False(host.IsMonitoring);
                Assert.Null(host.RegisteredHotkey);

                settings.Update(s =>
                {
                    s.WheelEnabled = true;
                    s.SelectionHotkey = "";
                });
                host.ApplySettings();
                Assert.True(host.IsMonitoring);
                Assert.Null(host.RegisteredHotkey);

                settings.Update(s =>
                {
                    s.ConvertModifiers = TriggerModifiers.None;
                    s.ToolsModifiers = TriggerModifiers.None;
                    s.ActionsModifiers = TriggerModifiers.None;
                });
                host.ApplySettings();
                Assert.False(host.IsMonitoring, "No bindings: no hook needed.");
            }
            finally
            {
                host.Stop();
            }
            Assert.False(host.IsMonitoring);
            Assert.Null(host.RegisteredHotkey);
        });
    }

    [Fact]
    public void Popup_windows_never_activate_or_show_in_the_taskbar()
    {
        Sta.Run(() =>
        {
            var log = new RecordingLog();
            var drag = new WheelWindow(WheelInteraction.Drag, log);
            var click = new WheelWindow(WheelInteraction.Click, log);
            var catcher = new CatcherWindow(log);
            try
            {
                foreach (var window in new PopupWindow[] { drag, click, catcher })
                {
                    Assert.NotEqual(IntPtr.Zero, window.Handle);
                    Assert.True((window.ExtendedStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0);
                    Assert.True((window.ExtendedStyle & NativeMethods.WS_EX_APPWINDOW) == 0);
                    Assert.True(window.AllowsTransparency);
                    Assert.True(window.Topmost);
                    Assert.False(window.ShowInTaskbar);
                    Assert.False(window.IsVisible);
                }
                Assert.True((drag.ExtendedStyle & NativeMethods.WS_EX_NOACTIVATE) != 0);
                Assert.True((catcher.ExtendedStyle & NativeMethods.WS_EX_NOACTIVATE) != 0);
                Assert.True((click.ExtendedStyle & NativeMethods.WS_EX_NOACTIVATE) == 0, "Click mode needs the keyboard.");
                Assert.True(drag.AllowDrop);
                Assert.True(catcher.AllowDrop);
                Assert.False(drag.ShowActivated);
            }
            finally
            {
                drag.Close();
                click.Close();
                catcher.Close();
            }
        });
    }

    [Fact]
    public void Monitor_lookup_reports_a_work_area_and_scale()
    {
        var (work, scale) = PopupWindow.MonitorAt(NativeMethods.CursorPosition());
        Assert.True(work.Width > 100 && work.Height > 100);
        Assert.InRange(scale, 1, 4);
    }

    [Fact]
    public void Explorer_selection_reads_safely()
    {
        Sta.Run(() =>
        {
            var log = new RecordingLog();
            var window = new Window();
            var own = new WindowInteropHelper(window).EnsureHandle();
            Assert.Empty(ExplorerSelection.GetSelectedPaths(own, log));
            Assert.Empty(ExplorerSelection.GetSelectedPaths(IntPtr.Zero, log));

            // Read-only probes of the real shell, when present: they must not throw or log problems.
            var desktop = NativeMethods.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Progman", null);
            if (desktop != IntPtr.Zero)
                Assert.All(ExplorerSelection.GetSelectedPaths(desktop, log), p => Assert.True(Path.IsPathRooted(p)));
            var explorer = NativeMethods.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "CabinetWClass", null);
            if (explorer != IntPtr.Zero)
                Assert.All(ExplorerSelection.GetSelectedPaths(explorer, log), p => Assert.True(Path.IsPathRooted(p)));
            Assert.DoesNotContain(log.Lines, l => l.StartsWith("[WARN]") || l.StartsWith("[ERROR]"));
            window.Close();
        });
    }

    [Fact]
    public void Show_for_files_with_nothing_selected_does_nothing()
    {
        Sta.Run(() =>
        {
            var log = new RecordingLog();
            var host = new WheelHost(HostServices.Create([], SettingsStore.InMemory(), log));
            host.ShowForFiles([], WheelMode.Convert);
            Assert.Contains(log.Lines, l => l.Contains("nothing to show"));
            host.Stop();
        });
    }
}
