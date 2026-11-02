using System.IO;
using System.Windows;
using System.Windows.Controls;
using TextElement = System.Windows.Documents.TextElement;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Clementine.App.Actions;
using Clementine.App.Branding;
using Clementine.App.Dialogs;
using Clementine.App.Launching;
using Clementine.App.Main;
using Clementine.App.Options;
using Clementine.App.Preferences;
using Clementine.App.Shelf;
using Clementine.App.Startup;
using Clementine.App.Theme;
using Clementine.App.Toasts;
using Clementine.App.Tray;
using Clementine.App.Updates;
using Clementine.Core;
using Clementine.Core.Hosting;
using Clementine.Wheel;

namespace Clementine.App.Snapshots;

/// <summary>
/// <c>--snapshot-ui &lt;dir&gt;</c>: renders every window, dialog, toast and the tray menu to PNGs (2× scale) for design
/// review, then asks the wheel to render its own previews. Uses in-memory settings and touches nothing outside
/// <paramref name="directory"/> and a temp folder of empty sample files. Windows are never shown on screen.
/// </summary>
internal static class SnapshotRunner
{
    public static int Run(Application app, string directory)
    {
        Directory.CreateDirectory(directory);
        var report = new List<string>();
        var log = new ReportLog(report);
        var renderer = new Renderer(app, directory, report);
        var samplesDir = Path.Combine(Path.GetTempPath(), "Clementine", "snapshot-samples");
        var queues = new List<JobQueue>();
        try
        {
            var samples = SampleData.CreateFiles(samplesDir);
            var settings = SettingsStore.InMemory();
            var services = ShellBootstrap.CreateServices(settings, log);
            services.Catalog.Add(GuardedProvider.Deduplicated(new ActionsOperationProvider(new NoShellActions()), services.Catalog, log));
            var video = InputFile.FromPath(samples.Videos[0]);
            var realVideoOps = services.Catalog.ForInputs([video], OperationCategory.Convert).Count;
            if (realVideoOps == 0)
            {
                services.Catalog.Add(new SampleData.Provider());
                report.Add("Engines have no video conversions yet: using sample operations for the main window.");
            }
            services.Launcher = new UiLauncher(services, toasts: null);
            queues.Add(services.Jobs);

            renderer.Step("icon-sheet", () => RenderIconSheet(renderer));
            renderer.Step("glyphs", () => RenderGlyphSheet(renderer));
            RenderToasts(renderer, services, samples, queues);
            RenderMainWindow(renderer, services, samples);
            RenderMainWindowWithoutFfmpeg(renderer, log, samples);
            RenderSettings(renderer, services);
            RenderOptions(renderer, services, samples);
            renderer.Step("shelf", () => RenderShelf(renderer, samples, log, empty: false));
            renderer.Step("shelf-empty", () => RenderShelf(renderer, samples, log, empty: true));
            renderer.Step("tray-menu", () => RenderTrayMenu(renderer, "tray-menu", null));
            renderer.Step("tray-menu-update", () => RenderTrayMenu(renderer, "tray-menu-update", "0.1.3"));
            renderer.Step("help", () => renderer.WindowContent("help", new HelpWindow(settings.Current), 640, 660));
            renderer.Step("text-viewer", () => renderer.WindowContent("text-viewer", new TextViewerWindow("Media info", "Festival Opener 4K.mov", SampleText.MediaInfo), 680, 500));
            renderer.Step("error-dialog", () =>
            {
                var dialog = new ErrorDialog("Clementine hit a problem but is still running. If it keeps happening, the details below help to fix it.",
                    "System.InvalidOperationException: The wheel window couldn't be created.\n   at Clementine.Wheel.WheelWindow..ctor()\n   at Clementine.Wheel.WheelHost.ShowForFiles(…)");
                dialog.ExpandDetails();
                renderer.WindowContent("error-dialog", dialog, 520, null);
            });

            renderer.Step("wheel-previews", () =>
            {
                var wheel = new WheelHost(services);
                services.Wheel = wheel;
                wheel.RenderPreviews(directory);
            });
        }
        catch (Exception ex)
        {
            report.Add($"FATAL: {ex}");
        }
        finally
        {
            foreach (var queue in queues)
                queue.CancelAll();
            renderer.Dispose();
            TryDelete(samplesDir);
        }

        report.Insert(0, $"Clementine snapshot {DateTime.Now:yyyy-MM-dd HH:mm:ss} → {directory}");
        File.WriteAllLines(Path.Combine(directory, "snapshot-report.txt"), report);
        return renderer.Failures == 0 ? 0 : 1;
    }

    // ===== Toasts =====

    static void RenderToasts(Renderer renderer, HostServices services, SampleData.Files samples, List<JobQueue> queues)
    {
        var clips = Enumerable.Range(1, 12).Select(i => Touch(samples.Directory, $"Clip {i:00}.mp4")).ToList();
        var states = new List<(string Name, ToastState State)>();

        // Finished scenarios run on the main queue so the main window lists them as recent jobs.
        var success = Run(services.Jobs, Fake("HAP Alpha · no audio", OperationCategory.Convert, "vj",
            (r, _, _) => Done(OperationResult.FromOutputs(Path.Combine(r.Input.DirectoryPath, r.Input.BaseName + " (HAP).mov")))), [samples.Videos[1]]);
        states.Add(("toast-success", ToastState.FromJobs(success)));

        var warning = Run(services.Jobs, Fake("DXV3 · with audio", OperationCategory.Convert, "vj",
            (r, _, _) => Done(new OperationResult
            {
                Outputs = [Path.Combine(r.Input.DirectoryPath, r.Input.BaseName + " (DXV).mov")],
                Warnings = ["This clip has no audio track, so the file is video only."],
            })), [samples.Videos[2]]);
        states.Add(("toast-success-warning", ToastState.FromJobs(warning)));

        var text = Run(services.Jobs, Fake("Media info", OperationCategory.Tool, "info",
            (_, _, _) => Done(OperationResult.FromText("Media info", SampleText.MediaInfo))), [samples.Videos[0]]);
        states.Add(("toast-text", ToastState.FromJobs(text)));

        var copied = Run(services.Jobs, Fake("Copy path", OperationCategory.Action, "copy",
            (r, _, _) => Done(OperationResult.FromText("Copied 3 paths", string.Join(Environment.NewLine, r.Inputs.Select(i => i.Path)))), combined: true),
            samples.Images);
        states.Add(("toast-text-copy-path", ToastState.FromJobs(copied)));

        var failure = Run(services.Jobs, Fake("Audio · MP3", OperationCategory.Convert, "audio",
            (_, _, _) => throw new OperationException("This video has no audio track.", "ffmpeg -i \"Crowd Cam 03.mp4\" -map 0:a:0 …\nStream map '0:a:0' matches no streams.\nTo ignore this, add a trailing '?' to the map.")),
            [samples.Videos[2]]);
        states.Add(("toast-failure", ToastState.FromJobs(failure)));

        var batchDone = Run(services.Jobs, Fake("PNG", OperationCategory.Convert, "image",
            (r, _, _) => Done(OperationResult.FromOutputs(Path.Combine(r.Input.DirectoryPath, r.Input.BaseName + ".png")))), [.. samples.Images, samples.Psd]);
        states.Add(("toast-batch-done", ToastState.FromJobs(batchDone)));

        var partial = Run(services.Jobs, Fake("MP4 · H.265", OperationCategory.Convert, "video",
            (r, _, _) => r.Input.Name == "Clip 04.mp4"
                ? throw new OperationException("FFmpeg couldn't read this file. It may be damaged.", "moov atom not found\nClip 04.mp4: Invalid data found when processing input")
                : Done(OperationResult.FromOutputs(Path.Combine(r.Input.DirectoryPath, r.Input.BaseName + " (H.265).mp4")))), clips.Take(5).ToList());
        states.Add(("toast-batch-partial", ToastState.FromJobs(partial)));

        // Long-running scenarios each get their own queue, so they never block the ones above.
        var hangQueue = NewQueue(services, queues);
        var running = Start(hangQueue, Fake("ProRes 4444 · alpha · with audio", OperationCategory.Convert, "film", Hang(0.42, "Encoding…")), [samples.Videos[0]]);
        var indeterminate = Start(NewQueue(services, queues), Fake("PNG", OperationCategory.Convert, "image", Hang(null, "Reading layers…")), [samples.Psd]);
        var batchQueue = NewQueue(services, queues);
        var batch = Start(batchQueue, Fake("MP4 · H.264", OperationCategory.Convert, "video",
            (r, p, ct) => string.CompareOrdinal(r.Input.Name, "Clip 06.mp4") < 0 ? Done(OperationResult.FromOutputs(r.Input.Path + ".out.mp4")) : Hang(0.3, "Encoding…")(r, p, ct)), clips);
        var mainRunning = Start(services.Jobs, Fake("ProRes 422 HQ · with audio", OperationCategory.Convert, "film", Hang(0.67, "Encoding…")), [samples.Videos[2]]);
        renderer.WaitUntil(() => running[0].Progress is not null && indeterminate[0].Status is not null && mainRunning[0].Progress is not null
                                  && batch.Count(j => j.IsFinished) >= 5 && batch.Count(j => j.State == JobState.Running && j.Progress is not null) >= 1,
            TimeSpan.FromSeconds(8));
        states.Insert(0, ("toast-running", ToastState.FromJobs(running)));
        states.Insert(1, ("toast-indeterminate", ToastState.FromJobs(indeterminate)));
        states.Insert(2, ("toast-batch-running", ToastState.FromJobs(batch)));
        states.Add(("toast-info-first-run", ToastState.Info("Clementine is running", "Hold Shift while dragging files to convert them. It lives in the tray.", "How it works")));
        states.Add(("toast-canceled", new ToastState { Kind = ToastKind.Canceled, Title = "Canceled", Subtitle = "Trim · Festival Opener 4K.mov", IconPath = samples.Videos[0], IconKey = "trim" }));
        states.Add(("toast-update-ready", ToastState.UpdateReady("0.1.3")));
        states.Add(("toast-updated", ToastState.Updated("0.1.3")));

        foreach (var (name, state) in states)
        {
            renderer.Step(name, () =>
            {
                var card = new ToastCard();
                card.Apply(state);
                renderer.Popover(name, card, ShellThemeWidth(card));
            });
        }

        renderer.Step("toast-stack", () =>
        {
            var stack = new StackPanel();
            foreach (var state in new[] { states.First(s => s.Name == "toast-batch-running").State, states.First(s => s.Name == "toast-success").State, states.First(s => s.Name == "toast-failure").State })
            {
                var card = new ToastCard();
                card.Apply(state);
                stack.Children.Add(card);
            }
            renderer.Popover("toast-stack", stack, 364);
        });
    }

    static double ShellThemeWidth(FrameworkElement card) => double.IsNaN(card.Width) ? 364 : card.Width;

    static JobQueue NewQueue(HostServices services, List<JobQueue> queues)
    {
        var queue = new JobQueue(services.Engine, services.Output, maxHeavyJobs: 4);
        queues.Add(queue);
        return queue;
    }

    static IReadOnlyList<Job> Start(JobQueue queue, IOperation operation, IReadOnlyList<string> paths) =>
        queue.Enqueue(operation, paths.Select(InputFile.FromPath).ToList());

    static IReadOnlyList<Job> Run(JobQueue queue, IOperation operation, IReadOnlyList<string> paths)
    {
        var jobs = Start(queue, operation, paths);
        Task.WhenAll(jobs.Select(j => j.Completion)).Wait(TimeSpan.FromSeconds(10));
        return jobs;
    }

    static Task<OperationResult> Done(OperationResult result) => Task.FromResult(result);

    static Func<OperationRequest, IProgress<JobProgress>, CancellationToken, Task<OperationResult>> Hang(double? fraction, string status) =>
        async (_, progress, cancellationToken) =>
        {
            progress.Report(new JobProgress(fraction, status));
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return new OperationResult();
        };

    static IOperation Fake(string title, OperationCategory category, string icon,
        Func<OperationRequest, IProgress<JobProgress>, CancellationToken, Task<OperationResult>> run, bool combined = false) =>
        new DelegateOperation(
            new OperationInfo
            {
                Id = "action.snapshot-sample.job-" + Guid.NewGuid().ToString("N")[..8],
                Category = category,
                Group = title.Split(' ')[0],
                Title = title,
                IconKey = icon,
            },
            _ => true,
            run,
            inputMode: combined ? InputMode.Combined : InputMode.PerFile);

    // ===== Main window =====

    static void RenderMainWindow(Renderer renderer, HostServices services, SampleData.Files samples)
    {
        MainWindow? main = null;
        renderer.Step("main-empty", () =>
        {
            main = new MainWindow(services, () => { }, () => { });
            renderer.WindowContent("main-empty", main, 1000, 650, detachOnly: true);
        });
        if (main is null)
            return;
        var videos = samples.Videos.Select(InputFile.FromPath).ToList();
        renderer.Step("main-video-convert", () =>
        {
            main.ShowFiles(videos);
            main.SelectMode(WheelMode.Convert);
            renderer.Reuse("main-video-convert", 1000, 650);
        });
        renderer.Step("main-video-tools", () =>
        {
            main.SelectMode(WheelMode.Tools);
            renderer.Reuse("main-video-tools", 1000, 650);
        });
        renderer.Step("main-video-actions", () =>
        {
            main.SelectMode(WheelMode.Actions);
            renderer.Reuse("main-video-actions", 1000, 650);
        });
        renderer.Step("main-psd-convert", () =>
        {
            main.ShowFiles([InputFile.FromPath(samples.Psd)]);
            main.SelectMode(WheelMode.Convert);
            renderer.Reuse("main-psd-convert", 1000, 650);
        });
        renderer.Step("main-mixed-none", () =>
        {
            main.ShowFiles([InputFile.FromPath(samples.Videos[0]), InputFile.FromPath(samples.Pdf), InputFile.FromPath(samples.Audio)]);
            main.SelectMode(WheelMode.Convert);
            renderer.Reuse("main-mixed-none", 1000, 650);
        });
        renderer.Release();
    }

    /// <summary>
    /// The "FFmpeg isn't installed" banner. FFmpeg may well be installed on this machine, so its absence is faked: sample
    /// operations marked unavailable the way the media engine marks them, and a window told FFmpeg is missing.
    /// </summary>
    static void RenderMainWindowWithoutFfmpeg(Renderer renderer, ILog log, SampleData.Files samples)
    {
        var services = HostServices.Create([new SampleData.Provider(missingFfmpeg: true)], SettingsStore.InMemory(), log);
        services.Launcher = new UiLauncher(services, toasts: null);
        MainWindow? main = null;
        renderer.Step("main-no-ffmpeg-video", () =>
        {
            main = new MainWindow(services, () => { }, () => { }, ffmpegMissing: () => true);
            main.ShowFiles(samples.Videos.Select(InputFile.FromPath).ToList());
            main.SelectMode(WheelMode.Convert);
            renderer.WindowContent("main-no-ffmpeg-video", main, 1000, 650, detachOnly: true);
            if (!main.IsFfmpegBannerVisible)
                throw new InvalidOperationException("The FFmpeg banner should show for videos when FFmpeg is missing.");
        });
        if (main is null)
            return;
        renderer.Step("main-no-ffmpeg-psd", () =>
        {
            // Images don't need FFmpeg: no banner even though it is missing.
            main.ShowFiles([InputFile.FromPath(samples.Psd)]);
            main.SelectMode(WheelMode.Convert);
            renderer.Reuse("main-no-ffmpeg-psd", 1000, 650);
            if (main.IsFfmpegBannerVisible)
                throw new InvalidOperationException("The FFmpeg banner shouldn't show for a PSD.");
        });
        renderer.Release();
    }

    // ===== Settings =====

    static void RenderSettings(Renderer renderer, HostServices services)
    {
        var testRoot = $@"Software\ClementineTests\Snapshot-{Guid.NewGuid():N}";
        var registration = new ShellRegistration(ShellRegistration.CurrentExePath, Microsoft.Win32.Registry.CurrentUser, testRoot + @"\Run", testRoot + @"\Classes");
        var sendTo = new SendToShortcut(Path.Combine(Path.GetTempPath(), "Clementine", "snapshot-sendto"), ShellRegistration.CurrentExePath);
        IReadOnlyList<ToolInfo> tools = [];
        renderer.Step("settings-tools-detect", () => tools = renderer.Wait(ToolStatus.DetectAsync(services.Tools), TimeSpan.FromSeconds(15)) ?? []);

        // An installed copy that has just downloaded 0.1.3 (a sample source: snapshots never go online).
        using var updates = new Updater(new SampleData.UpdateSource("0.1.3"), () => true, services.Log);
        renderer.Step("settings-update-sample", () =>
        {
            var status = renderer.Wait(updates.CheckNowAsync(), TimeSpan.FromSeconds(10));
            if (status?.Stage != UpdateStage.Ready)
                throw new InvalidOperationException($"The sample update should be ready, not {status?.Stage}.");
        });

        SettingsWindow? window = null;
        renderer.Step("settings-general", () =>
        {
            window = new SettingsWindow(services, registration, sendTo, tools, updates, restartToUpdate: () => { });
            renderer.WindowContent("settings-general", window, 900, 640, detachOnly: true);
        });
        if (window is null)
            return;
        foreach (var page in Enum.GetValues<SettingsPage>().Skip(1))
        {
            var name = "settings-" + page.ToString().ToLowerInvariant();
            renderer.Step(name, () =>
            {
                window.ShowPage(page);
                renderer.Reuse(name, 900, page == SettingsPage.About ? AboutHeight : 640);
            });
        }
        // The About page is longer than a window can be (Windows caps it at the screen size): its end as well.
        renderer.Step("settings-about-end", () =>
        {
            window.ScrollPageToEnd();
            renderer.Reuse("settings-about-end", 900, AboutHeight);
        });
        renderer.Release();

        // What a release build shows: FFmpeg and 7-Zip built in, LibreOffice not installed (sample data, nothing runs).
        renderer.Step("settings-tools-bundled", () =>
        {
            var bundled = new SettingsWindow(services, registration, sendTo, SampleData.BundledTools(), updates);
            bundled.ShowPage(SettingsPage.Tools);
            renderer.WindowContent("settings-tools-bundled", bundled, 900, 640);
        });

        // This development build: it can't update itself, so the About page says so and offers no check.
        renderer.Step("settings-about-dev-build", () =>
        {
            using var real = UpdateSetup.CreateUpdater(services.Settings, services.Log);
            var about = new SettingsWindow(services, registration, sendTo, tools, real, restartToUpdate: () => { });
            about.ShowPage(SettingsPage.About);
            renderer.WindowContent("settings-about-dev-build", about, 900, AboutHeight);
        });
    }

    const double AboutHeight = 1060;

    // ===== Options dialogs =====

    static void RenderOptions(Renderer renderer, HostServices services, SampleData.Files samples)
    {
        var video = new[] { InputFile.FromPath(samples.Videos[0]) };
        var pdf = new[] { InputFile.FromPath(samples.Pdf) };
        var images = samples.Images.Select(InputFile.FromPath).ToList();

        void Dialog(string name, OptionSchema schema, IReadOnlyList<InputFile> inputs, string? icon, IOptionsPreview? preview = null, OptionValues? values = null, Action<OptionsDialog>? tweak = null)
        {
            renderer.Step(name, () =>
            {
                var initial = OptionMerging.Prefill(schema, null, values);
                var dialog = new OptionsDialog(schema, inputs, initial, icon, preview);
                tweak?.Invoke(dialog);
                renderer.WindowContent(name, dialog, dialog.Width, null, popover: true);
            });
        }

        Dialog("options-trim", SampleData.TrimSchema, video, "trim");
        Dialog("options-compress", SampleData.CompressSchema, video, "compress");
        Dialog("options-compress-size", SampleData.CompressSchema, video, "compress", values: new OptionValues { ["mode"] = "size", ["size"] = 25.0 });
        Dialog("options-pdf-ranges", SampleData.PdfRangesSchema, pdf, "split");
        Dialog("options-password", SampleData.PasswordSchema, pdf, "lock");
        Dialog("options-every-field", SampleData.EveryFieldSchema, images, "frames", values: new OptionValues { ["loop"] = true, ["folder"] = @"D:\VJ\Renders" });
        Dialog("options-validation", SampleData.TrimSchema, video, "trim", tweak: d => d.TypeInto("end", "1:7x"));
        Dialog("options-validation-number", SampleData.CompressSchema, video, "compress", tweak: d => d.TypeInto("quality", "150"));

        var rename = new RenameOperation();
        Dialog("options-rename", rename.Options, images, "rename", rename);
        var copyTo = new CopyToOperation();
        Dialog("options-copy-to", copyTo.Options, images, "copy");

        // Real schemas from the engines, when they exist: a few per engine family (video., pdf., archive. …).
        var real = services.Catalog.All
            .Where(o => o.Options is not null && !o.Info.Id.StartsWith("action.", StringComparison.Ordinal))
            .DistinctBy(o => o.Options!.Title)
            .GroupBy(o => o.Info.Id.Split('.')[0])
            .SelectMany(g => g.Take(3))
            .Take(24)
            .ToList();
        foreach (var op in real)
        {
            var inputs = new[] { SampleInputFor(op, samples) };
            Dialog($"options-real-{op.Info.Id}", op.Options!, inputs.Where(i => i is not null).Select(i => i!).ToList(), op.Info.IconKey);
        }
    }

    static InputFile? SampleInputFor(IOperation op, SampleData.Files samples)
    {
        foreach (var path in samples.Videos.Concat([samples.Psd, samples.Pdf, samples.Audio]).Concat(samples.Images).Concat(samples.Others).Append(samples.Folder))
        {
            var input = InputFile.FromPath(path);
            if (SafeAccepts(op, input))
                return input;
        }
        return InputFile.FromPath(samples.Videos[0]);
    }

    static bool SafeAccepts(IOperation op, InputFile file)
    {
        try
        {
            return op.Accepts(file);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ===== Shelf, tray, icon =====

    static void RenderShelf(Renderer renderer, SampleData.Files samples, ILog log, bool empty)
    {
        var store = new ShelfStore(null, log);
        if (!empty)
            store.AddUnchecked([samples.Videos[0], samples.Videos[1], samples.Psd, samples.Pdf, samples.Folder]);
        renderer.WindowContent(empty ? "shelf-empty" : "shelf", new ShelfWindow(store, log), 356, null, popover: true);
    }

    static void RenderTrayMenu(Renderer renderer, string name, string? updateVersion)
    {
        var (menu, _) = TrayIcon.BuildMenu(new TrayCommands(() => { }, () => { }, () => { }, () => { }, () => { }, () => { }, RestartToUpdate: () => { }), updateVersion);
        var items = menu.Items.Cast<object>().ToList();
        menu.Items.Clear();
        var panel = new StackPanel();
        foreach (var item in items)
            panel.Children.Add((UIElement)item);
        var border = new Border
        {
            Background = ShellTheme.Brush("Shell.Popup"),
            BorderBrush = ShellTheme.Brush("Clementine.Stroke"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(4),
            Width = 240,
            Effect = (System.Windows.Media.Effects.Effect)Application.Current.FindResource("Shell.SoftShadow"),
            Child = panel,
        };
        renderer.Popover(name, border, 240);
    }

    static void RenderIconSheet(Renderer renderer)
    {
        int[] sizes = [16, 20, 24, 32, 48, 64, 128, 256];
        const int gap = 24;
        var width = sizes.Sum() + gap * (sizes.Length + 1);
        var height = (256 + gap * 2) * 2;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1F, 0x1D, 0x1B)), null, new Rect(0, 0, width, height / 2.0));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3)), null, new Rect(0, height / 2.0, width, height / 2.0));
            var x = gap;
            foreach (var size in sizes)
            {
                var image = IconRenderer.Render(size);
                dc.DrawImage(image, new Rect(x, gap + 256 - size, size, size));
                dc.DrawImage(image, new Rect(x, height / 2.0 + gap + 256 - size, size, size));
                x += size + gap;
            }
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        renderer.Save("icon-sheet", bitmap);
    }

    static void RenderGlyphSheet(Renderer renderer)
    {
        var panel = new WrapPanel { Width = 760, Margin = new Thickness(16) };
        foreach (var glyph in Glyphs.All.Distinct())
        {
            panel.Children.Add(new Border
            {
                Width = 56,
                Height = 56,
                Margin = new Thickness(4),
                CornerRadius = new CornerRadius(8),
                Background = ShellTheme.Brush("Shell.Card"),
                Child = new TextBlock { Text = glyph, Style = (Style)Application.Current.FindResource("Shell.Glyph"), FontSize = 22 },
            });
        }
        renderer.Element("glyphs", panel, 792, null, ShellTheme.WindowGradient);
    }

    static string Touch(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        if (!File.Exists(path))
            File.WriteAllBytes(path, []);
        return path;
    }

    static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Shell actions that do nothing (snapshot mode must not touch the desktop).</summary>
    sealed class NoShellActions : IShellActions
    {
        public void SetClipboardText(string text)
        {
        }

        public void Reveal(IReadOnlyList<string> paths)
        {
        }

        public void OpenTerminal(string directory)
        {
        }

        public void OpenWith(string file)
        {
        }

        public void PinToShelf(IReadOnlyList<string> paths)
        {
        }
    }

    sealed class ReportLog(List<string> report) : ILog
    {
        public void Info(string message)
        {
        }

        public void Warn(string message, Exception? exception = null) => Add("warn", message, exception);

        public void Error(string message, Exception? exception = null) => Add("error", message, exception);

        void Add(string level, string message, Exception? exception)
        {
            lock (report)
                report.Add($"[{level}] {message}{(exception is null ? "" : " — " + exception.Message)}");
        }
    }

    static class SampleText
    {
        public const string MediaInfo = """
            Container     QuickTime / MOV
            Duration      00:00:12.480
            Size          184.2 MB · 118 Mb/s
            Video         ProRes 4444 (ap4h) · 3840×2160 · 25 fps · yuva444p12le · alpha
            Colour        BT.709 · limited range
            Audio         PCM 24-bit · 48 kHz · stereo
            Encoder       Lavf61.7.100
            """;
    }

    /// <summary>Hosts elements in one off-screen window and saves them as 2× PNGs.</summary>
    sealed class Renderer : IDisposable
    {
        /// <summary>A desktop-like backdrop for floating cards (dithered, so the PNGs only show the app's own banding).</summary>
        static readonly DitheredGradient Wallpaper = CreateWallpaper();

        readonly Application _app;
        readonly string _directory;
        readonly List<string> _report;
        readonly Window _host;
        Border? _reused;

        public Renderer(Application app, string directory, List<string> report)
        {
            _app = app;
            _directory = directory;
            _report = report;
            _host = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowInTaskbar = false,
                ShowActivated = false,
                SizeToContent = SizeToContent.WidthAndHeight,
                Left = -32000,
                Top = -32000,
                Title = "Clementine snapshot",
            };
            _host.Show();
        }

        public int Failures { get; private set; }

        public void Step(string name, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Failures++;
                _report.Add($"FAILED {name}: {ex}");
            }
        }

        /// <summary>Renders a window's content (the window itself is never shown).</summary>
        public void WindowContent(string name, Window window, double width, double? height, bool popover = false, bool detachOnly = false)
        {
            var content = (FrameworkElement)window.Content;
            window.Content = null;
            var gradient = popover ? Wallpaper : WindowBackdrop.GetGradient(window);
            var root = Root(content, width, height, gradient is null ? window.Background : null, popover, gradient);
            if (detachOnly)
                _reused = root;
            RenderRoot(name, root);
            if (!detachOnly)
                Detach(root);
        }

        /// <summary>Re-renders the last detached window content (after changing its state).</summary>
        public void Reuse(string name, double width, double height)
        {
            if (_reused is null)
                throw new InvalidOperationException("Nothing to reuse.");
            _reused.Width = width;
            _reused.Height = height;
            RenderRoot(name, _reused);
        }

        public void Release()
        {
            if (_reused is not null)
                Detach(_reused);
            _reused = null;
        }

        /// <summary>A floating card (toast, popover) on a desktop-like wallpaper.</summary>
        public void Popover(string name, FrameworkElement element, double width)
        {
            var padded = new Border { Padding = new Thickness(28), Child = element };
            var root = Root(padded, width + 56, null, null, popover: true, Wallpaper);
            RenderRoot(name, root);
            padded.Child = null;
            Detach(root);
        }

        public void Element(string name, FrameworkElement element, double width, double? height, DitheredGradient background)
        {
            var root = Root(element, width, height, null, popover: false, background);
            RenderRoot(name, root);
            Detach(root);
        }

        public void Save(string name, BitmapSource bitmap)
        {
            var path = Path.Combine(_directory, name + ".png");
            IconRenderer.SavePng(bitmap, path);
            _report.Add($"ok  {name}.png  {bitmap.PixelWidth}×{bitmap.PixelHeight}");
        }

        public T? Wait<T>(Task<T> task, TimeSpan timeout)
        {
            var frame = new DispatcherFrame();
            task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
            var timer = new DispatcherTimer(DispatcherPriority.Normal, _app.Dispatcher) { Interval = timeout };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                frame.Continue = false;
            };
            timer.Start();
            if (!task.IsCompleted)
                Dispatcher.PushFrame(frame);
            timer.Stop();
            return task.IsCompletedSuccessfully ? task.Result : default;
        }

        public void WaitUntil(Func<bool> condition, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (!condition() && DateTime.UtcNow < deadline)
            {
                Pump();
                Thread.Sleep(30);
            }
            Pump();
        }

        /// <param name="gradient">When set, the root is painted with this gradient, dithered at the PNG's own pixel size.</param>
        Border Root(FrameworkElement content, double width, double? height, Brush? background, bool popover, DitheredGradient? gradient = null)
        {
            var root = new Border
            {
                Background = gradient is null ? background : new SolidColorBrush(gradient.Fallback),
                Width = width,
                Child = content,
                UseLayoutRounding = true,
                SnapsToDevicePixels = true,
                Tag = gradient,
            };
            if (height is { } h)
                root.Height = h;
            TextElement.SetFontFamily(root, (FontFamily)_app.FindResource("Clementine.Font"));
            TextElement.SetFontSize(root, 13);
            TextElement.SetForeground(root, ShellTheme.Brush("Clementine.Text"));
            TextOptions.SetTextFormattingMode(root, TextFormattingMode.Ideal);
            if (popover)
                root.Padding = new Thickness(0);
            return root;
        }

        void RenderRoot(string name, Border root)
        {
            _host.Content = root;
            Pump();
            root.UpdateLayout();
            // Let template animations (toggle switches, fades) reach their final frame before capturing.
            WaitUntil(() => false, TimeSpan.FromMilliseconds(260));
            var width = Math.Ceiling(root.ActualWidth);
            var height = Math.Ceiling(root.ActualHeight);
            if (width < 1 || height < 1)
                throw new InvalidOperationException($"{name} has no size.");
            // Like WindowBackdrop does on screen: one dithered bitmap pixel per output pixel (the PNGs are 2×).
            if (root.Tag is DitheredGradient gradient)
            {
                root.Background = gradient.CreateBrush(root.ActualWidth, root.ActualHeight, 2, 2);
                root.UpdateLayout();
            }

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var brush = new VisualBrush(root)
                {
                    Stretch = Stretch.None,
                    AlignmentX = AlignmentX.Left,
                    AlignmentY = AlignmentY.Top,
                    Viewbox = new Rect(0, 0, width, height),
                    ViewboxUnits = BrushMappingMode.Absolute,
                };
                dc.DrawRectangle(brush, null, new Rect(0, 0, width, height));
            }
            var bitmap = new RenderTargetBitmap((int)(width * 2), (int)(height * 2), 192, 192, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            Save(name, bitmap);
        }

        void Detach(Border root)
        {
            if (ReferenceEquals(_host.Content, root))
                _host.Content = null;
            root.Child = null;
        }

        void Pump()
        {
            _app.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            _app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        }

        static DitheredGradient CreateWallpaper() => new(new Point(0, 0), new Point(1, 1),
        [
            (0, Color.FromRgb(0x24, 0x33, 0x47)),
            (0.55, Color.FromRgb(0x3B, 0x2C, 0x45)),
            (1, Color.FromRgb(0x1A, 0x1B, 0x22)),
        ]);

        public void Dispose()
        {
            _host.Content = null;
            _host.Close();
        }
    }
}
