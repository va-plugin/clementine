using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Threading;
using Clementine.Core;
using Clementine.Core.Hosting;
using Clementine.TestSupport;

namespace Clementine.Wheel.Tests;

/// <summary>Runs only with CLEMENTINE_VISIBLE=1: shows the (no-activate) drag wheel at the cursor for a moment.</summary>
public sealed class VisibleFactAttribute : FactAttribute
{
    public VisibleFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CLEMENTINE_VISIBLE") != "1")
            Skip = "Set CLEMENTINE_VISIBLE=1 to run; it briefly shows the wheel on screen (no mouse or keyboard input).";
    }
}

public class OpenLatencyTests
{
    /// <summary>
    /// From "files entered the catcher" to the wheel's first rendered frame, with the real catalog when the app is
    /// built: the warm open must stay well under 50 ms, and showing the wheel must never take the foreground.
    /// Timings go to %TEMP%\ClementineDev\review-wheel\latency.txt.
    /// </summary>
    [VisibleFact]
    public void Drag_wheel_opens_fast_and_never_takes_focus()
    {
        var report = new List<string>();
        Sta.Run(() =>
        {
            var log = new RecordingLog();
            var settings = SettingsStore.InMemory(new ClementineSettings { SelectionHotkey = "" });
            var baseServices = HostServices.Create([], settings, log);
            var catalog = RealCatalog.TryCreate(baseServices.Engine, out var reason) ?? Fakes.SampleCatalog();
            report.Add(catalog.All.Count > 100 ? $"real catalog: {catalog.All.Count} operations" : $"sample catalog ({reason})");
            var services = new HostServices
            {
                Settings = settings,
                Log = log,
                Tools = baseServices.Tools,
                Engine = baseServices.Engine,
                Output = baseServices.Output,
                Catalog = catalog,
                Jobs = baseServices.Jobs,
            };
            using var ws = new TestWorkspace("latency");
            var host = new WheelHost(services) { CatcherBlocker = _ => null };
            try
            {
                var clock = Stopwatch.StartNew();
                host.Start();
                report.Add($"Start(): {clock.Elapsed.TotalMilliseconds:F1} ms");
                Pump(TimeSpan.FromMilliseconds(600)); // let the idle-time warm-up run, as it does after app start

                var foreground = NativeMethods.GetForegroundWindow();
                var at = NativeMethods.CursorPosition();
                var timings = new List<double>();
                foreach (var name in new[] { "first.mp4", "second.mov", "poster.psd", "third.mp4", "loop.mov" })
                {
                    var path = ws.WriteText(name, "x");
                    var (sync, frame) = OpenAndWaitForFrame(host, [path], at);
                    timings.Add(frame);
                    var wheel = host.DragWheelWindow!;
                    report.Add($"{name}: open {sync:F1} ms, first frame {frame:F1} ms, {wheel.Session!.Model.Groups.Count} groups, " +
                        $"size {wheel.Width:F0} dip @ {VisualTreeHelper.GetDpi(wheel.View).PixelsPerDip:F2}x");
                    Assert.Equal(foreground, NativeMethods.GetForegroundWindow());
                    Assert.True((wheel.ExtendedStyle & NativeMethods.WS_EX_NOACTIVATE) != 0);
                    wheel.Dismiss();
                    Pump(TimeSpan.FromMilliseconds(80));
                }
                File.WriteAllLines(Path.Combine(Fakes.ScratchDirectory("latency"), "latency.txt"), report);
                // The first open after start-up still loads a little (the idle warm-up covers the rest); every later one is warm.
                Assert.True(timings[0] < 100, string.Join(Environment.NewLine, report));
                Assert.True(timings.Skip(1).Max() < 50, string.Join(Environment.NewLine, report));
            }
            finally
            {
                host.Stop();
            }
        });
        var target = Path.Combine(Path.GetTempPath(), "ClementineDev", "review-wheel");
        Directory.CreateDirectory(target);
        File.WriteAllLines(Path.Combine(target, "latency.txt"), report);
    }

    static (double Sync, double Frame) OpenAndWaitForFrame(WheelHost host, IReadOnlyList<string> paths, ScreenPoint at)
    {
        var clock = Stopwatch.StartNew();
        host.OnFilesEntered(paths, at);
        var sync = clock.Elapsed.TotalMilliseconds;
        var frame = new DispatcherFrame();
        var rendered = double.NaN;
        void OnRendering(object? sender, EventArgs e)
        {
            CompositionTarget.Rendering -= OnRendering;
            // Runs after this frame's render pass has been handed to the composition thread.
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Render, () =>
            {
                rendered = clock.Elapsed.TotalMilliseconds;
                frame.Continue = false;
            });
        }
        CompositionTarget.Rendering += OnRendering;
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timeout.Tick += (_, _) => frame.Continue = false;
        timeout.Start();
        Dispatcher.PushFrame(frame);
        timeout.Stop();
        return (sync, rendered);
    }

    static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
