using Clementine.Core;

namespace Clementine.App.Preferences;

/// <summary>
/// Everything Clementine registers for the current user — the "launch at login" Run value, the Explorer verbs and the
/// Send To shortcut — removed or refreshed together. Used by the installer hooks (Velopack's uninstall/update
/// callbacks in <see cref="Program.Main"/>) and by <c>Clementine.exe --unregister</c>. Never shows UI.
/// </summary>
internal static class UserIntegration
{
    /// <summary>Removes every integration. Fast, never throws; returns what couldn't be removed (empty when all went).</summary>
    public static IReadOnlyList<string> RemoveAll(ShellRegistration registration, SendToShortcut sendTo)
    {
        var problems = new List<string>();
        Try(() => registration.SetLaunchAtLogin(false), "the launch-at-login entry", problems);
        Try(() => registration.SetContextMenu(false), "the Explorer menu entries", problems);
        Try(sendTo.Remove, "the Send To shortcut", problems);
        return problems;
    }

    /// <summary>
    /// Rewrites the integrations <paramref name="settings"/> has switched on so they run <see cref="ShellRegistration.ExePath"/>
    /// (after an update the exe may live somewhere new). Integrations that are off are left alone. Never throws.
    /// </summary>
    public static IReadOnlyList<string> Refresh(ClementineSettings settings, ShellRegistration registration, SendToShortcut sendTo)
    {
        var problems = new List<string>();
        if (settings.LaunchAtLogin)
            Try(() => registration.SetLaunchAtLogin(true), "the launch-at-login entry", problems);
        if (settings.ExplorerContextMenu)
            Try(() => registration.SetContextMenu(true), "the Explorer menu entries", problems);
        if (settings.SendToShortcut)
        {
            Try(() =>
            {
                sendTo.Remove();
                sendTo.Create();
            }, "the Send To shortcut", problems);
        }
        return problems;
    }

    // ===== Entry points (Program.Main) =====

    /// <summary>Velopack: just before the uninstaller removes the app.</summary>
    public static void BeforeUninstall() => Report("Uninstall", () => RemoveAll(CurrentRegistration(), CurrentSendTo()));

    /// <summary>Velopack: right after an update was installed (the old version is no longer running).</summary>
    public static void AfterUpdate() => Report("Update", () => Refresh(new SettingsStore().Current, CurrentRegistration(), CurrentSendTo()));

    /// <summary><c>--unregister</c>: removes everything and returns the process exit code (0, also when nothing was registered).</summary>
    public static int Unregister()
    {
        Report("--unregister", () => RemoveAll(CurrentRegistration(), CurrentSendTo()));
        return 0;
    }

    static ShellRegistration CurrentRegistration() => ShellRegistration.ForCurrentUser(ShellRegistration.CurrentExePath);

    static SendToShortcut CurrentSendTo() => new(SendToShortcut.DefaultDirectory, ShellRegistration.CurrentExePath);

    static void Report(string what, Func<IReadOnlyList<string>> run)
    {
        try
        {
            var problems = run();
            var log = new FileLog();
            if (problems.Count == 0)
                log.Info($"{what}: Windows integrations updated for {ShellRegistration.CurrentExePath}");
            foreach (var problem in problems)
                log.Warn($"{what}: {problem}");
        }
        catch (Exception ex)
        {
            // Installer hooks must never fail the install/uninstall; there is nothing useful to do here.
            System.Diagnostics.Debug.WriteLine($"{what} failed: {ex}");
        }
    }

    static void Try(Action action, string what, List<string> problems)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            problems.Add($"Couldn't update {what}: {ex.Message}");
        }
    }
}
