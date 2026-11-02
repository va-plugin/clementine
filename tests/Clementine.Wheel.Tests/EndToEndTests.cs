using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Clementine.Core;
using Clementine.Core.Hosting;
using Clementine.TestSupport;

namespace Clementine.Wheel.Tests;

/// <summary>Runs only with CLEMENTINE_E2E=1: these tests drive the real mouse and keyboard with SendInput.</summary>
public sealed class E2EFactAttribute : FactAttribute
{
    public E2EFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CLEMENTINE_E2E") != "1")
            Skip = "Set CLEMENTINE_E2E=1 to run; it moves the real mouse and holds Shift.";
    }
}

public class EndToEndTests
{
    /// <summary>
    /// A real OLE drag: an in-process source window starts DoDragDrop with a FileDrop; SendInput holds Shift and
    /// drags; the hook arms the catcher, the wheel opens, and dropping on the first segment launches its operation
    /// with LaunchSource.Wheel and a Copy effect.
    /// </summary>
    [E2EFact]
    public void Shift_drag_of_a_file_opens_the_wheel_and_the_drop_launches_the_operation()
    {
        using var ws = new TestWorkspace("e2e");
        var file = ws.WriteText("clip.mp4", "not really a video");
        NativeMethods.GetCursorPos(out var originalCursor);
        var launched = new TaskCompletionSource<(string Id, LaunchSource Source)>(TaskCreationOptions.RunContinuationsAsynchronously);
        DragDropEffects dropResult = DragDropEffects.None;

        Sta.Run(() =>
        {
            var catalog = Fakes.Catalog(Fakes.Op(Fakes.Info("video.e2e.first", "First", order: 1)));
            var services = HostServices.Create([], SettingsStore.InMemory(), new RecordingLog());
            var e2eServices = new HostServices
            {
                Settings = services.Settings,
                Log = services.Log,
                Tools = services.Tools,
                Engine = services.Engine,
                Output = services.Output,
                Catalog = catalog,
                Jobs = services.Jobs,
            };
            e2eServices.Launcher = new RecordingLauncher(launched);
            var host = new WheelHost(e2eServices);

            var source = new Window
            {
                Width = 240,
                Height = 240,
                Left = 200,
                Top = 200,
                Topmost = true,
                WindowStyle = WindowStyle.ToolWindow,
                Content = new Border { Background = Brushes.SteelBlue },
            };
            var dragging = false;
            source.MouseMove += (_, e) =>
            {
                if (dragging || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
                    return;
                dragging = true;
                dropResult = DragDrop.DoDragDrop(source, new DataObject(DataFormats.FileDrop, new[] { file }), DragDropEffects.Copy | DragDropEffects.Move);
            };

            try
            {
                host.Start();
                source.Show();
                var start = new ScreenPoint((int)(source.Left + 120), (int)(source.Top + 120));
                var script = Task.Run(async () =>
                {
                    await Task.Delay(300);
                    Input.MoveTo(start);
                    Input.Key(NativeMethods.VK_SHIFT, down: true);
                    Input.Button(down: true);
                    for (var i = 1; i <= 12; i++)
                    {
                        Input.MoveTo(new ScreenPoint(start.X + i * 6, start.Y + i * 3));
                        await Task.Delay(25);
                    }
                    await Task.Delay(500); // catcher → wheel
                    var wheel = await source.Dispatcher.InvokeAsync(() => host.DragWheelWindow);
                    var target = await source.Dispatcher.InvokeAsync(() =>
                    {
                        var session = wheel!.Session!;
                        var offset = session.Layout.SegmentCentroid(0);
                        var scale = VisualTreeHelper.GetDpi(wheel.View).DpiScaleX;
                        return new ScreenPoint(wheel.CentreOnScreen.X + (int)(offset.X * scale), wheel.CentreOnScreen.Y + (int)(offset.Y * scale));
                    });
                    for (var i = 1; i <= 10; i++)
                    {
                        Input.MoveTo(target with { X = target.X - (10 - i) * 4 });
                        await Task.Delay(30);
                    }
                    await Task.Delay(200);
                    Input.Button(down: false);
                    Input.Key(NativeMethods.VK_SHIFT, down: false);
                });

                var frame = new DispatcherFrame();
                launched.Task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
                var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
                timeout.Tick += (_, _) => frame.Continue = false;
                timeout.Start();
                Dispatcher.PushFrame(frame);
                timeout.Stop();
                script.Wait(TimeSpan.FromSeconds(5));
            }
            finally
            {
                Input.Button(down: false);
                Input.Key(NativeMethods.VK_SHIFT, down: false);
                host.Stop();
                source.Close();
            }
        });

        Input.MoveTo(new ScreenPoint(originalCursor.X, originalCursor.Y));
        Assert.True(launched.Task.IsCompleted, "The drop never reached the launcher.");
        Assert.Equal(("video.e2e.first", LaunchSource.Wheel), launched.Task.Result);
        Assert.Equal(DragDropEffects.Copy, dropResult);
    }

    sealed class RecordingLauncher(TaskCompletionSource<(string, LaunchSource)> launched) : IOperationLauncher
    {
        public Task<IReadOnlyList<Job>> LaunchAsync(IOperation operation, IReadOnlyList<InputFile> inputs, LaunchSource source, ScreenPoint? anchor = null)
        {
            launched.TrySetResult((operation.Info.Id, source));
            return Task.FromResult<IReadOnlyList<Job>>([]);
        }
    }

    /// <summary>Minimal SendInput wrapper (absolute moves across the virtual desktop).</summary>
    static class Input
    {
        const uint INPUT_MOUSE = 0;
        const uint INPUT_KEYBOARD = 1;
        const uint MOUSEEVENTF_MOVE = 0x0001;
        const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        const uint MOUSEEVENTF_LEFTUP = 0x0004;
        const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
        const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
        const uint KEYEVENTF_KEYUP = 0x0002;

        public static void MoveTo(ScreenPoint point)
        {
            var left = GetSystemMetrics(76);
            var top = GetSystemMetrics(77);
            var width = Math.Max(1, GetSystemMetrics(78) - 1);
            var height = Math.Max(1, GetSystemMetrics(79) - 1);
            var mouse = new MOUSEINPUT
            {
                dx = (int)Math.Round((point.X - left) * 65535.0 / width),
                dy = (int)Math.Round((point.Y - top) * 65535.0 / height),
                dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
            };
            Send(new INPUT { type = INPUT_MOUSE, u = new InputUnion { mi = mouse } });
        }

        public static void Button(bool down) =>
            Send(new INPUT { type = INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP } } });

        public static void Key(int vk, bool down) =>
            Send(new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = (ushort)vk, dwFlags = down ? 0 : KEYEVENTF_KEYUP } } });

        static void Send(INPUT input) => SendInput(1, [input], Marshal.SizeOf<INPUT>());

        [StructLayout(LayoutKind.Sequential)]
        struct INPUT
        {
            public uint type;
            public InputUnion u;
        }

        [StructLayout(LayoutKind.Explicit)]
        struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        static extern int GetSystemMetrics(int index);
    }
}
