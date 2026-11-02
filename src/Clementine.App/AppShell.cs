using System.IO;
using System.Windows;
using System.Windows.Threading;
using Clementine.App.Actions;
using Clementine.App.Dialogs;
using Clementine.App.Interop;
using Clementine.App.Launching;
using Clementine.App.Main;
using Clementine.App.Preferences;
using Clementine.App.Shelf;
using Clementine.App.Startup;
using Clementine.App.Toasts;
using Clementine.App.Tray;
using Clementine.App.Updates;
using Clementine.Core;
using Clementine.Core.Hosting;
using Clementine.Wheel;

namespace Clementine.App;

/// <summary>
/// The running app: owns the services, wheel, tray icon, toasts, windows, the instance pipe and the updater, and
/// handles commands from the command line and from later launches. UI thread only.
/// </summary>
public sealed class AppShell : IDisposable
{
    readonly Application _app;
    readonly SingleInstance _instance;
    readonly ToastManager _toasts;
    readonly ShelfStore _shelf;
    readonly Dictionary<WheelMode, WheelRequestAggregator> _aggregators = [];
    readonly DispatcherTimer _aggregateTimer;
    readonly ShellRegistration _registration;
    readonly SendToShortcut _sendTo;
    readonly Updater _updater;
    InstanceChannel.Server? _server;
    TrayIcon? _tray;
    MainWindow? _main;
    SettingsWindow? _settingsWindow;
    ShelfWindow? _shelfWindow;
    HelpWindow? _help;
    bool _wheelPaused;
    bool _wheelStarted;
    bool _quitting;
    bool _disposed;
    string? _announcedUpdate;

    AppShell(Application app, SingleInstance instance, HostServices services)
    {
        _app = app;
        _instance = instance;
        Services = services;
        var dispatcher = app.Dispatcher;

        _toasts = new ToastManager(services, dispatcher);
        _shelf = new ShelfStore(ShelfStore.DefaultFile, services.Log);
        var shellActions = new DesktopShellActions(dispatcher, services.Log, PinToShelf);
        services.Catalog.Add(GuardedProvider.Deduplicated(new ActionsOperationProvider(shellActions), services.Catalog, services.Log));
        services.Launcher = new UiLauncher(services, _toasts);

        _registration = ShellRegistration.ForCurrentUser(ShellRegistration.CurrentExePath);
        _sendTo = new SendToShortcut(SendToShortcut.DefaultDirectory, ShellRegistration.CurrentExePath);

        _aggregateTimer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = TimeSpan.FromMilliseconds(80) };
        _aggregateTimer.Tick += (_, _) => FlushWheelRequests();

        _updater = UpdateSetup.CreateUpdater(services.Settings, services.Log);
        _updater.StatusChanged += (_, status) => dispatcher.BeginInvoke(() => OnUpdateStatus(status));
    }

    public HostServices Services { get; }

    /// <summary>
    /// Builds and starts the app for the primary instance. <paramref name="server"/> (already listening, holding any
    /// launches that arrived while the engines loaded) and <paramref name="tray"/> are handed over and owned from now on.
    /// </summary>
    public static AppShell Start(Application app, SingleInstance instance, HostServices services, ShellCommand command,
        InstanceChannel.Server? server = null, TrayIcon? tray = null)
    {
        var shell = new AppShell(app, instance, services);
        app.SessionEnding += shell.OnSessionEnding;
        shell._tray = tray ?? CreateTray(action => action(shell));
        shell.CreateWheel();
        shell.StartServer(server);
        shell.Handle(command);
        // Installing the drag hook and warming up the wheel windows takes the UI thread for a while on a cold start;
        // do it right after whatever the command showed (main window, settings) has rendered. The first-run card
        // comes after that: its window is the first WPF window of the process and takes a second or more to appear.
        app.Dispatcher.BeginInvoke(DispatcherPriority.Background, shell.StartWheel);
        app.Dispatcher.BeginInvoke(DispatcherPriority.Background, shell.FirstRunHint);
        app.Dispatcher.BeginInvoke(DispatcherPriority.Background, shell.StartUpdates);
        return shell;
    }

    /// <summary>
    /// The tray icon. Built before the engines finish loading, so its commands go through <paramref name="run"/>,
    /// which runs them on the shell once it exists.
    /// </summary>
    public static TrayIcon CreateTray(Action<Action<AppShell>> run) => new(new TrayCommands(
        OpenMain: () => run(shell => shell.ShowMain()),
        OpenJobs: () => run(shell =>
        {
            shell.ShowMain();
            shell._main?.FocusJobs();
        }),
        TogglePause: () => run(shell => shell.ToggleWheelPause()),
        OpenSettings: () => run(shell => shell.ShowSettings()),
        OpenHelp: () => run(shell => shell.ShowHelp()),
        Quit: () => run(shell => shell.Quit(confirm: true)),
        RestartToUpdate: () => run(shell => shell.RestartToUpdate())));

    /// <summary>Handles a command from this process's command line or from another launch.</summary>
    public void Handle(ShellCommand command)
    {
        Services.Log.Info($"Command: {command.Kind} ({command.Paths.Count} path(s))");
        switch (command.Kind)
        {
            case ShellCommandKind.Background:
                break;
            case ShellCommandKind.Wheel:
                QueueWheel(command.Paths, command.Mode);
                break;
            case ShellCommandKind.OpenFiles:
                ShowMain(ToInputs(command.Paths));
                break;
            case ShellCommandKind.Settings:
                ShowSettings();
                break;
            case ShellCommandKind.Quit:
                Quit(confirm: false);
                break;
            default:
                ShowMain();
                break;
        }
    }

    // ===== Startup pieces =====

    void CreateWheel()
    {
        try
        {
            // Cheap: the expensive part (hook, hotkey, window warm-up) is Start(), run in StartWheel.
            Services.Wheel = new WheelHost(Services);
        }
        catch (Exception ex)
        {
            Services.Log.Error("The wheel couldn't be created; drag-and-drop from the main window still works", ex);
        }
    }

    void StartWheel()
    {
        if (_disposed || _quitting || _wheelPaused || _wheelStarted)
            return;
        try
        {
            // The wheel reads WheelEnabled itself (and again on ApplySettings), so always start it.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Services.Wheel?.Start();
            _wheelStarted = true;
            Services.Log.Info($"Wheel started in {clock.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            Services.Log.Error("The wheel failed to start; drag-and-drop from the main window still works", ex);
        }
    }

    void StartServer(InstanceChannel.Server? server)
    {
        _server = server ?? new InstanceChannel.Server(SingleInstance.DefaultPipeName, Services.Log);
        // Subscribing delivers launches that arrived during start-up first, in order.
        _server.MessageReceived += (_, message) => _app.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                Handle(message.ToCommand());
            }
            catch (Exception ex)
            {
                Services.Log.Error("Forwarded command failed", ex);
            }
        });
    }

    void FirstRunHint()
    {
        var settings = Services.Settings.Current;
        if (_disposed || settings.FirstRunCompleted)
            return;
        var keys = KeyText.Describe(settings.ConvertModifiers);
        _toasts.ShowInfo(
            ToastState.Info("Clementine is running", $"Hold {keys} while dragging files to convert them. It lives in the tray.", "How it works"),
            ShowHelp);
        try
        {
            Services.Settings.Update(s => s.FirstRunCompleted = true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Services.Log.Warn("Couldn't save first-run flag", ex);
        }
    }

    // ===== Updates =====

    /// <summary>
    /// Starts the update schedule (installed copies with an update source only; the updater decides) and, right after
    /// Velopack restarted Clementine into a new version, says so.
    /// </summary>
    void StartUpdates()
    {
        if (_disposed)
            return;
        if (VelopackHooks.RestartedAfterUpdate is { Length: > 0 } version)
        {
            VelopackHooks.RestartedAfterUpdate = null;
            Services.Log.Info($"Update: now running {version}");
            _toasts.ShowInfo(ToastState.Updated(version), () => ExplorerShell.OpenUrl(UpdateSetup.ReleaseUrl(version), Services.Log));
        }
        Services.Log.Info(_updater.IsAvailable
            ? $"Update checks: {(Services.Settings.Current.CheckForUpdates ? "on" : "off in Settings")}"
            : $"Update checks: none ({_updater.Status.Text})");
        _updater.Start(UpdateSetup.FirstCheckDelay);
    }

    /// <summary>The tray item and the one-time card for a downloaded update (UI thread).</summary>
    void OnUpdateStatus(UpdateStatus status)
    {
        if (_disposed)
            return;
        var ready = status.Stage == UpdateStage.Ready ? status.Version : null;
        _tray?.SetUpdateReady(ready);
        if (ready is null || _quitting || string.Equals(ready, _announcedUpdate, StringComparison.OrdinalIgnoreCase))
            return;
        _announcedUpdate = ready;
        _toasts.ShowInfo(ToastState.UpdateReady(ready), RestartToUpdate, UpdateCardTimeout);
    }

    /// <summary>The update card stays longer than other messages; the tray item stays until Clementine restarts.</summary>
    static readonly TimeSpan UpdateCardTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Restart to update: like Quit (running jobs are cancelled after asking, and given the quit drain to clean up),
    /// then Velopack installs the downloaded version and starts Clementine again in the tray.
    /// </summary>
    public void RestartToUpdate()
    {
        if (_quitting || _disposed || _updater.ReadyVersion is not { } version)
            return;
        var running = Services.Jobs.Snapshot().Count(j => !j.IsFinished);
        if (running > 0)
        {
            var answer = MessageBox.Show(
                $"{running} {(running == 1 ? "job is" : "jobs are")} still running. Cancel {(running == 1 ? "it" : "them")} and restart to update to Clementine {version}?",
                "Restart to update", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes || _quitting || _disposed)
                return;
        }
        _ = RestartToUpdateAsync(version);
    }

    async Task RestartToUpdateAsync(string version)
    {
        // No other quit or restart while the installer is being started; nothing has stopped yet if that fails.
        _quitting = true;
        Services.Log.Info($"Update: restarting to install {version}");
        if (!await _updater.ApplyAfterExitAsync(VelopackHooks.RestartArgs))
        {
            _quitting = false;
            _toasts.ShowInfo(ToastState.Info("Couldn't start the update", $"Try again later, or download Clementine {version} from GitHub."));
            return;
        }
        // The installer waits (up to a minute) for this process to exit; the drain takes a few seconds at most.
        await QuitAsync(Services.Jobs.Snapshot().Count(j => !j.IsFinished));
    }

    // ===== Windows =====

    public void ShowMain(IReadOnlyList<InputFile>? files = null)
    {
        if (_main is null)
        {
            _main = new MainWindow(Services, () => ShowSettings(), ShowHelp);
            _main.Closed += (_, _) => _main = null;
        }
        if (files is { Count: > 0 })
            _main.ShowFiles(files);
        if (!_main.IsVisible)
            _main.Show();
        if (_main.WindowState == WindowState.Minimized)
            _main.WindowState = WindowState.Normal;
        WindowEffects.ForceActivate(_main);
    }

    public void ShowSettings(SettingsPage page = SettingsPage.General)
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(Services, _registration, _sendTo, updates: _updater, restartToUpdate: RestartToUpdate);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        _settingsWindow.ShowPage(page);
        if (!_settingsWindow.IsVisible)
            _settingsWindow.Show();
        WindowEffects.ForceActivate(_settingsWindow);
    }

    void ShowHelp()
    {
        if (_help is null)
        {
            _help = new HelpWindow(Services.Settings);
            _help.Closed += (_, _) => _help = null;
            _help.Show();
        }
        WindowEffects.ForceActivate(_help);
    }

    void PinToShelf(IReadOnlyList<string> paths)
    {
        _shelf.Add(paths);
        _shelfWindow ??= new ShelfWindow(_shelf, Services.Log);
        _shelfWindow.ShowShelf();
    }

    // ===== Wheel requests =====

    void QueueWheel(IReadOnlyList<string> paths, WheelMode mode)
    {
        if (!_aggregators.TryGetValue(mode, out var aggregator))
            _aggregators[mode] = aggregator = new WheelRequestAggregator();
        aggregator.Add(paths, mode, DateTimeOffset.UtcNow);
        _aggregateTimer.Start();
    }

    void FlushWheelRequests()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var aggregator in _aggregators.Values)
        {
            if (aggregator.TryFlush(now) is { } batch)
                ShowWheelFor(batch.Paths, batch.Mode);
        }
        if (_aggregators.Values.All(a => !a.HasPending))
            _aggregateTimer.Stop();
    }

    void ShowWheelFor(IReadOnlyList<string> paths, WheelMode mode)
    {
        var files = ToInputs(paths);
        if (files.Count == 0)
        {
            ShowMain();
            return;
        }
        try
        {
            if (Services.Wheel is null)
                throw new InvalidOperationException("The wheel isn't available.");
            StartWheel(); // no-op once started; the click-mode wheel relies on the hook to close on outside clicks
            Services.Wheel.ShowForFiles(files, mode, ScreenGeometry.CursorPosition());
        }
        catch (Exception ex)
        {
            Services.Log.Error("Couldn't show the wheel; opening the main window instead", ex);
            ShowMain(files);
            _main?.SelectMode(mode);
        }
    }

    List<InputFile> ToInputs(IEnumerable<string> paths)
    {
        var files = new List<InputFile>();
        foreach (var path in paths)
        {
            try
            {
                files.Add(InputFile.FromPath(path));
            }
            catch (Exception ex) when (ex is FileNotFoundException or IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
            {
                Services.Log.Warn($"Ignoring '{path}'", ex);
            }
        }
        return files;
    }

    void ToggleWheelPause()
    {
        if (_quitting)
            return;
        _wheelPaused = !_wheelPaused;
        try
        {
            if (_wheelPaused)
                Services.Wheel?.Stop();
            else
                Services.Wheel?.Start();
            _wheelStarted = !_wheelPaused;
        }
        catch (Exception ex)
        {
            Services.Log.Warn("Pausing/resuming the wheel failed", ex);
        }
        _tray?.SetPaused(_wheelPaused);
    }

    // ===== Exit =====

    public void Quit(bool confirm)
    {
        if (_quitting || _disposed)
            return;
        var running = Services.Jobs.Snapshot().Count(j => !j.IsFinished);
        if (confirm && running > 0)
        {
            var answer = MessageBox.Show(
                $"{running} {(running == 1 ? "job is" : "jobs are")} still running. Quit and cancel {(running == 1 ? "it" : "them")}?",
                "Quit Clementine", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes || _quitting || _disposed)
                return;
        }
        _ = QuitAsync(running);
    }

    /// <summary>
    /// Cancels the jobs and waits (a few seconds at most, UI still responsive) for them to remove their partial outputs
    /// and temp folders, then exits. New launches meanwhile wait for the single-instance lock and start afresh.
    /// </summary>
    async Task QuitAsync(int running)
    {
        _quitting = true;
        StopTakingCommands();
        if (running > 0)
            Services.Log.Info($"Quit: cancelling {running} job(s)");
        if (!await JobDrain.CancelAndWaitAsync(Services.Jobs, JobDrain.QuitGrace))
            Services.Log.Warn($"Quit: jobs were still stopping after {JobDrain.QuitGrace.TotalSeconds:0} s; exiting anyway");
        Dispose();
        _app.Shutdown();
    }

    /// <summary>Signing out or shutting down: cancel the jobs and give them a short, bounded moment to clean up.</summary>
    void OnSessionEnding(object? sender, SessionEndingCancelEventArgs e)
    {
        _quitting = true;
        var running = Services.Jobs.Snapshot().Count(j => !j.IsFinished);
        Services.Log.Info($"Windows session ending ({e.ReasonSessionEnding}); cancelling {running} job(s)");
        if (running > 0 && !JobDrain.CancelAndWait(Services.Jobs, JobDrain.SessionEndGrace))
            Services.Log.Warn("Session ending: jobs were still stopping; exiting anyway");
        // Not cancelled, so WPF shuts down next and Application.Exit disposes the shell.
    }

    void StopTakingCommands()
    {
        _aggregateTimer.Stop();
        TryRun(() => Services.Wheel?.Stop(), "stopping the wheel");
        var server = _server;
        _server = null;
        TryRun(() => server?.Dispose(), "closing the instance pipe");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _app.SessionEnding -= OnSessionEnding;
        _aggregateTimer.Stop();
        TryRun(_updater.Dispose, "stopping update checks");
        TryRun(() => Services.Wheel?.Stop(), "stopping the wheel");
        TryRun(() => _tray?.Dispose(), "removing the tray icon");
        TryRun(() => _server?.Dispose(), "closing the instance pipe");
        TryRun(_toasts.DismissAll, "closing toasts");
        foreach (var window in _app.Windows.OfType<Window>().ToList())
            TryRun(window.Close, "closing a window");
        TryRun(_instance.Dispose, "releasing the single-instance lock");
    }

    void TryRun(Action action, string what)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Services.Log.Warn($"Shutdown: {what} failed", ex);
        }
    }
}
