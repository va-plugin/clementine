using System.Windows;
using System.Windows.Threading;
using Clementine.App.Dialogs;
using Clementine.Core;

namespace Clementine.App.Startup;

/// <summary>
/// Global exception handlers (Dispatcher, AppDomain, TaskScheduler): log everything and show a friendly dialog
/// instead of crashing. A failing job never reaches these — the job queue turns that into a failed toast.
/// </summary>
public static class CrashGuard
{
    static ILog _log = NullLog.Instance;
    static bool _installed;
    static DateTime _lastDialog = DateTime.MinValue;
    static int _dialogsOpen;

    public static void Install(Application app, ILog log)
    {
        _log = log;
        if (_installed)
            return;
        _installed = true;

        app.DispatcherUnhandledException += (_, e) =>
        {
            _log.Error("Unhandled UI exception", e.Exception);
            e.Handled = true;
            ShowFriendly(e.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            // The runtime is going down; all we can do is make sure it's in the log.
            _log.Error($"Fatal unhandled exception (terminating: {e.IsTerminating})", e.ExceptionObject as Exception);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            _log.Warn("Unobserved task exception", e.Exception);
            e.SetObserved();
        };
        InstallWinForms(log);
    }

    static bool _winFormsInstalled;

    /// <summary>
    /// The tray icon is a WinForms <c>NotifyIcon</c>: an exception in its click handlers is routed to WinForms'
    /// <c>Application.ThreadException</c>, never to the WPF dispatcher, and without a handler WinForms shows its own
    /// "Unhandled exception" dialog with a Quit button. Log it and show the friendly dialog instead.
    /// </summary>
    public static void InstallWinForms(ILog log)
    {
        _log = log;
        if (_winFormsInstalled)
            return;
        _winFormsInstalled = true;
        System.Windows.Forms.Application.ThreadException += (_, e) =>
        {
            _log.Error("Unhandled tray exception", e.Exception);
            ShowFriendly(e.Exception);
        };
    }

    /// <summary>Shows the error dialog (at most one at a time, and not more than once every few seconds).</summary>
    public static void ShowFriendly(Exception exception)
    {
        try
        {
            if (_dialogsOpen > 0 || DateTime.UtcNow - _lastDialog < TimeSpan.FromSeconds(3))
                return;
            _lastDialog = DateTime.UtcNow;
            var message = exception is OperationException
                ? exception.Message
                : "Clementine hit a problem but is still running. If it keeps happening, the details below help to fix it.";
            var dialog = new ErrorDialog(message, exception.ToString());
            _dialogsOpen++;
            dialog.Closed += (_, _) => _dialogsOpen--;
            dialog.Show();
        }
        catch (Exception dialogError)
        {
            _log.Error("Couldn't show the error dialog", dialogError);
        }
    }
}
