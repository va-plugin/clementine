using System.Security.Principal;

namespace Clementine.App.Startup;

/// <summary>
/// Per-user single-instance guard: a named mutex <c>Clementine.SingleInstance.&lt;SID&gt;</c>. The first process owns it
/// for its whole lifetime; later launches forward their arguments over <see cref="InstanceChannel"/> and exit.
/// <para>
/// The mutex name has no <c>Global\</c> prefix, so it lives in the session's namespace: the same account signed in
/// twice (console + Remote Desktop, fast user switching) runs one instance per session. Pipe names are machine-wide,
/// so <see cref="DefaultPipeName"/> carries the session id too — otherwise a launch in one session could hand its
/// files to the instance on the other session's desktop.
/// </para>
/// </summary>
public sealed class SingleInstance : IDisposable
{
    readonly Mutex _mutex;
    bool _owned;

    SingleInstance(Mutex mutex, bool owned)
    {
        _mutex = mutex;
        _owned = owned;
    }

    /// <summary>True when this process is the first (primary) instance.</summary>
    public bool IsPrimary => _owned;

    /// <summary>"Clementine.SingleInstance.S-1-5-21-…" for the current user.</summary>
    public static string DefaultMutexName => $"Clementine.SingleInstance.{UserKey}{ProfileScope(DataDirectoryOverride)}";

    /// <summary>Pipe the primary instance of this user's current session listens on.</summary>
    public static string DefaultPipeName => PipeNameFor(UserKey + ProfileScope(DataDirectoryOverride), CurrentSessionId);

    /// <summary>
    /// A process started with <c>CLEMENTINE_DATA_DIR</c> (tests, a portable or development profile) uses other
    /// settings, so it is a separate instance with its own mutex and pipe: it must neither forward its launch to the
    /// everyday instance nor receive the everyday instance's launches. Empty for the normal profile.
    /// </summary>
    internal static string ProfileScope(string? dataDirectoryOverride)
    {
        if (string.IsNullOrWhiteSpace(dataDirectoryOverride))
            return "";
        string normalized;
        try
        {
            normalized = System.IO.Path.GetFullPath(dataDirectoryOverride.Trim()).TrimEnd('\\', '/').ToUpperInvariant();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.PathTooLongException)
        {
            normalized = dataDirectoryOverride.Trim().ToUpperInvariant();
        }
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized));
        return "." + Convert.ToHexString(hash, 0, 8);
    }

    static string? DataDirectoryOverride => Environment.GetEnvironmentVariable("CLEMENTINE_DATA_DIR");

    /// <summary>"Clementine.Pipe.&lt;SID&gt;.&lt;session&gt;".</summary>
    public static string PipeNameFor(string userKey, int sessionId) =>
        $"Clementine.Pipe.{userKey}.{sessionId.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    static int CurrentSessionId
    {
        get
        {
            try
            {
                using var process = System.Diagnostics.Process.GetCurrentProcess();
                return process.SessionId;
            }
            catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or System.ComponentModel.Win32Exception)
            {
                return 0;
            }
        }
    }

    static string UserKey
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return identity.User?.Value ?? Environment.UserName;
            }
            catch (System.Security.SecurityException)
            {
                return Environment.UserName;
            }
        }
    }

    public static SingleInstance Acquire(string? mutexName = null)
    {
        var mutex = new Mutex(initiallyOwned: false, mutexName ?? DefaultMutexName);
        bool owned;
        try
        {
            owned = mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            // A previous instance crashed while holding it; we own it now.
            owned = true;
        }
        return new SingleInstance(mutex, owned);
    }

    public void Dispose()
    {
        if (_owned)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Released from a different thread at shutdown; the OS releases it when the process exits anyway.
            }
            _owned = false;
        }
        _mutex.Dispose();
    }
}
