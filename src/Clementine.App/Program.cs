using System.Diagnostics;
using Clementine.App.Preferences;
using Clementine.App.Startup;
using Velopack;

namespace Clementine.App;

/// <summary>
/// Entry point (the project's StartupObject). A launch that finds Clementine already running forwards its arguments
/// over the instance pipe and exits <em>before</em> WPF starts: Explorer starts one process per selected file for the
/// context-menu verb, and skipping WPF makes each of those a fraction of a full start (time and memory), so a whole
/// multi-selection lands inside the wheel's aggregation window.
/// </summary>
public static class Program
{
    /// <summary>How long a later launch keeps trying to reach the running instance (it may still be starting).</summary>
    internal static readonly TimeSpan ForwardTimeout = TimeSpan.FromSeconds(10);

    static readonly TimeSpan ForwardAttempt = TimeSpan.FromMilliseconds(500);

    [STAThread]
    public static int Main(string[] args)
    {
        // Installer hooks first: Velopack's setup/update/uninstall start Clementine with a --veloapp-* argument, run the
        // matching callback and exit the process inside Run() — no UI, no single-instance check. Any other launch of an
        // installed copy returns at once (Velopack also sets the AppUserModelID its shortcuts use). An update that was
        // downloaded but not applied is never installed behind the user's back at startup: the updater offers it again
        // ("Restart to update"), and only that applies it.
        if (VelopackHooks.Applies(args, AppContext.BaseDirectory))
        {
            VelopackApp.Build()
                .SetArgs(args)
                .SetAutoApplyOnStartup(false)
                .OnBeforeUninstallFastCallback(_ => UserIntegration.BeforeUninstall())
                .OnAfterUpdateFastCallback(_ => UserIntegration.AfterUpdate())
                .OnRestarted(version => VelopackHooks.RestartedAfterUpdate = version?.ToString())
                .Run();
        }

        var command = CommandLine.Parse(args, Environment.CurrentDirectory);
        if (command.Kind == ShellCommandKind.Unregister)
            return UserIntegration.Unregister();
        SingleInstance? instance = null;
        if (!command.IsStandalone)
        {
            instance = AcquireOrForward(args, command, out var exitCode);
            if (instance is null)
                return exitCode;
        }

        return RunApp(instance);
    }

    // Kept out of Main so a forwarding launch never loads WPF (the JIT resolves every type a method uses).
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static int RunApp(SingleInstance? instance)
    {
        var app = new App { PrimaryInstance = instance };
        app.InitializeComponent();
        return app.Run();
    }

    /// <summary>
    /// Returns the single-instance guard when this process is the primary instance (the caller keeps it for its
    /// lifetime, on this thread). Otherwise forwards <paramref name="args"/> to the running instance and returns null
    /// with the process exit code. The mutex is re-checked between attempts, so a launch that races a quitting
    /// instance takes over instead of failing.
    /// </summary>
    internal static SingleInstance? AcquireOrForward(string[] args, ShellCommand command, out int exitCode,
        string? mutexName = null, string? pipeName = null, TimeSpan? timeout = null)
    {
        var message = new InstanceMessage(args, Environment.CurrentDirectory);
        var deadline = Stopwatch.StartNew();
        var limit = timeout ?? ForwardTimeout;
        while (true)
        {
            var instance = SingleInstance.Acquire(mutexName);
            if (instance.IsPrimary)
            {
                if (command.Kind == ShellCommandKind.Quit)
                {
                    // Nothing is running, so there's nothing to quit.
                    instance.Dispose();
                    exitCode = 0;
                    return null;
                }
                exitCode = 0;
                return instance;
            }
            instance.Dispose();

            var attempt = limit - deadline.Elapsed;
            if (attempt > ForwardAttempt)
                attempt = ForwardAttempt;
            var started = deadline.Elapsed;
            if (attempt > TimeSpan.Zero && InstanceChannel.TrySend(pipeName ?? SingleInstance.DefaultPipeName, message, attempt))
            {
                exitCode = 0;
                return null;
            }
            if (deadline.Elapsed >= limit)
            {
                exitCode = 1;
                return null;
            }
            // A pipe that refuses at once (not a timeout) mustn't turn this into a busy loop.
            if (deadline.Elapsed - started < TimeSpan.FromMilliseconds(100))
                Thread.Sleep(100);
        }
    }
}
