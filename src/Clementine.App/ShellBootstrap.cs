using System.Diagnostics;
using System.IO;
using System.Windows;
using Clementine.App.Branding;
using Clementine.App.Snapshots;
using Clementine.App.Startup;
using Clementine.App.Tray;
using Clementine.Core;
using Clementine.Core.Hosting;
using Clementine.Engines.Documents;
using Clementine.Engines.Files;
using Clementine.Engines.Images;
using Clementine.Engines.Media;

namespace Clementine.App;

/// <summary>
/// Composes the app at startup: parses the command line, enforces a single instance per user (later launches
/// forward their arguments over a named pipe and exit — see <see cref="Program"/>), builds the services and starts
/// the <see cref="AppShell"/>.
/// <para>
/// Start-up order for the primary instance, so the UI thread never waits on the engines: the instance pipe listens
/// immediately (launches that arrive meanwhile are held until the shell is ready), the engines and catalog are built
/// on a worker thread (Magick.NET initialisation, format probes…), and the tray icon appears in the meantime.
/// </para>
/// </summary>
public static class ShellBootstrap
{
    static readonly List<Action<AppShell>> PendingTrayCommands = [];
    static AppShell? _shell;

    public static IOperationProvider[] EngineProviders() =>
    [
        new MediaOperationProvider(),
        new ImageOperationProvider(),
        new DocumentOperationProvider(),
        new FilesOperationProvider(),
    ];

    /// <summary>
    /// Builds the service container with every engine (guarded, so one broken engine can't stop the app).
    /// The shell adds its actions provider and launcher afterwards. Thread-agnostic: the app calls it on a worker thread.
    /// </summary>
    public static HostServices CreateServices(SettingsStore settings, ILog log) =>
        HostServices.Create([new GuardedProvider(EngineProviders(), log)], settings, log);

    public static void Run(Application app, string[] args, SingleInstance? instance = null)
    {
        var startup = Stopwatch.StartNew();
        var command = CommandLine.Parse(args, Environment.CurrentDirectory);
        switch (command.Kind)
        {
            case ShellCommandKind.Snapshot:
                app.Shutdown(SnapshotRunner.Run(app, command.Directory ?? Path.Combine(Path.GetTempPath(), "ClementineSnapshots")));
                return;
            case ShellCommandKind.MakeIcon:
                app.Shutdown(MakeIcon(command.Directory ?? Environment.CurrentDirectory));
                return;
            case ShellCommandKind.Unregister:
                // Program.Main handles this before WPF starts; this covers a start through App's own Main.
                app.Shutdown(Preferences.UserIntegration.Unregister());
                return;
        }

        if (instance is null)
        {
            // Started through App's own Main rather than Program.Main: do the single-instance check here.
            instance = Program.AcquireOrForward(args, command, out var exitCode);
            if (instance is null)
            {
                app.Shutdown(exitCode);
                return;
            }
        }

        var log = new FileLog();
        CrashGuard.Install(app, log);
        log.Info($"Clementine starting ({string.Join(' ', args)}), {ProcessAgeMilliseconds()} ms after launch");
        InstanceChannel.Server? server = null;
        TrayIcon? tray = null;
        try
        {
            server = new InstanceChannel.Server(SingleInstance.DefaultPipeName, log);
            var settings = new SettingsStore();
            var engines = Task.Run(() =>
            {
                var clock = Stopwatch.StartNew();
                var services = CreateServices(settings, log);
                return (Services: services, Milliseconds: clock.ElapsedMilliseconds);
            });
            tray = AppShell.CreateTray(WhenReady);
            log.Info($"Tray icon ready ({ProcessAgeMilliseconds()} ms after launch, {startup.ElapsedMilliseconds} ms after WPF started); loading engines");
            _ = CompleteStartupAsync(app, instance, command, engines, server, tray, log, startup);
        }
        catch (Exception ex)
        {
            FailStartup(app, instance, server, tray, log, ex);
        }
    }

    static async Task CompleteStartupAsync(
        Application app, SingleInstance instance, ShellCommand command, Task<(HostServices Services, long Milliseconds)> engines,
        InstanceChannel.Server server, TrayIcon tray, ILog log, Stopwatch startup)
    {
        try
        {
            var (services, engineMs) = await engines.ConfigureAwait(false);
            await app.Dispatcher.InvokeAsync(() =>
            {
                _shell = AppShell.Start(app, instance, services, command, server, tray);
                app.Exit += (_, _) => _shell?.Dispose();
                foreach (var action in PendingTrayCommands)
                    action(_shell);
                PendingTrayCommands.Clear();
            });
            log.Info($"Shell ready in {startup.ElapsedMilliseconds} ms ({ProcessAgeMilliseconds()} ms after launch; engines loaded off the UI thread in {engineMs} ms)");
            _ = Housekeeping.RunInBackground(log);
        }
        catch (Exception ex)
        {
            await app.Dispatcher.InvokeAsync(() => FailStartup(app, instance, server, tray, log, ex));
        }
    }

    /// <summary>Runs a tray command now, or as soon as the shell is ready (a click while the engines are still loading).</summary>
    static void WhenReady(Action<AppShell> action)
    {
        if (_shell is { } shell)
            action(shell);
        else
            PendingTrayCommands.Add(action);
    }

    static void FailStartup(Application app, SingleInstance instance, InstanceChannel.Server? server, TrayIcon? tray, ILog log, Exception ex)
    {
        log.Error("Startup failed", ex);
        try
        {
            tray?.Dispose();
            server?.Dispose();
        }
        catch (Exception cleanup)
        {
            log.Warn("Cleaning up after a failed start", cleanup);
        }
        instance.Dispose();
        MessageBox.Show($"Clementine couldn't start: {ex.Message}", "Clementine", MessageBoxButton.OK, MessageBoxImage.Error);
        app.Shutdown(1);
    }

    static long ProcessAgeMilliseconds()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return (long)(DateTime.Now - process.StartTime).TotalMilliseconds;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return -1;
        }
    }

    static int MakeIcon(string directory)
    {
        try
        {
            foreach (var path in IconRenderer.Export(directory))
                Console.WriteLine(path);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
}
