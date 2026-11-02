using Clementine.Core;

namespace Clementine.App.Updates;

/// <summary>A newer release offered by the update source. <see cref="Handle"/> is the source's own object (Velopack's UpdateInfo).</summary>
public sealed record UpdateOffer(string Version, object? Handle = null);

/// <summary>
/// The update system behind <see cref="Updater"/>: Velopack reading GitHub Releases in the app
/// (<see cref="VelopackUpdateBackend"/>), a fake in tests. Calls may block; the updater makes them on worker threads.
/// </summary>
public interface IUpdateBackend
{
    /// <summary>Whether this copy was installed by Velopack (Setup.exe or the portable zip) and can update itself.</summary>
    bool IsInstalled { get; }

    /// <summary>The newest stable release newer than this copy, or null when up to date. Throws when the feed can't be read.</summary>
    Task<UpdateOffer?> CheckAsync(CancellationToken cancellationToken);

    /// <summary>Downloads and verifies <paramref name="offer"/> into the local package store; progress is 0–100.</summary>
    Task DownloadAsync(UpdateOffer offer, Action<int> progress, CancellationToken cancellationToken);

    /// <summary>
    /// Starts the updater that waits for this process to exit, installs <paramref name="offer"/> and starts Clementine
    /// again with <paramref name="restartArgs"/>. The caller exits right after. Throws when it couldn't be started.
    /// </summary>
    void ApplyAfterExit(UpdateOffer offer, IReadOnlyList<string> restartArgs);
}

public enum UpdateStage
{
    /// <summary>This copy can't update itself (a development build, or no update source in this build).</summary>
    Unavailable,

    /// <summary>Nothing checked yet.</summary>
    Idle,

    Checking,
    UpToDate,
    Downloading,

    /// <summary>A newer version is downloaded and waits for a restart.</summary>
    Ready,

    /// <summary>The last check the user asked for failed (background failures are only logged).</summary>
    Failed,

    /// <summary>
    /// "Restart to update" handed the downloaded version to the installer, which waits for Clementine to exit. Final:
    /// nothing checks, downloads or changes the status any more.
    /// </summary>
    Restarting,
}

/// <summary>Where the updater is. Immutable; <see cref="Text"/> is what the About page shows.</summary>
public sealed record UpdateStatus(UpdateStage Stage, string? Version = null, int Percent = 0, string? Message = null)
{
    public static readonly UpdateStatus Idle = new(UpdateStage.Idle);

    public static readonly UpdateStatus NotInstalled = new(UpdateStage.Unavailable,
        Message: "This copy doesn't update itself. Copies installed with Clementine-Setup.exe or the portable zip do.");

    public static readonly UpdateStatus NoSource = new(UpdateStage.Unavailable, Message: "This build has no update source.");

    public const string CheckFailedText = "Couldn't reach GitHub — try again later";

    public string Text => Stage switch
    {
        UpdateStage.Unavailable => Message ?? NotInstalled.Message!,
        UpdateStage.Checking => "Checking for updates…",
        UpdateStage.UpToDate => "You're up to date",
        UpdateStage.Downloading => $"Downloading {Version}…",
        UpdateStage.Ready => $"Clementine {Version} is ready — restart to update",
        UpdateStage.Failed => Message ?? CheckFailedText,
        UpdateStage.Restarting => $"Restarting to install Clementine {Version}…",
        _ => "",
    };
}

/// <summary>
/// Keeps an installed Clementine up to date: checks the update source ~60 s after startup and then every 6 hours
/// (only while <see cref="ClementineSettings.CheckForUpdates"/> is on), downloads a newer release quietly in the
/// background and reports <see cref="UpdateStage.Ready"/>; the shell then offers "Restart to update". The user can
/// also check from Settings › About. Never throws, never blocks the caller: all work runs on worker threads, failures
/// (offline, rate-limited, no releases yet, a malformed feed) are logged and a background failure changes nothing
/// visible. One check runs at a time; a check requested meanwhile joins it. Once a restart starts handing the update to
/// the installer, no new check starts (a newer download would let Velopack delete the package being installed), and
/// once the installer waits the schedule stops and the status stays <see cref="UpdateStage.Restarting"/>.
/// </summary>
public sealed class Updater : IDisposable
{
    public static readonly TimeSpan DefaultFirstCheckDelay = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan DefaultCheckInterval = TimeSpan.FromHours(6);

    /// <summary>How long applying waits for a background check it interrupts.</summary>
    static readonly TimeSpan StopCheckTimeout = TimeSpan.FromSeconds(5);

    readonly IUpdateBackend? _backend;
    readonly Func<bool> _automaticChecks;
    readonly ILog _log;
    readonly object _gate = new();
    readonly CancellationTokenSource _stop = new();
    UpdateStatus _status;
    UpdateStatus _settled; // last outcome worth keeping on screen: Idle, UpToDate or Ready
    UpdateOffer? _ready;
    Task<UpdateStatus>? _check;
    CancellationTokenSource? _checkCancel;
    bool _userWaiting;
    bool _applying; // from the moment a restart hands the update over; cleared only if the installer couldn't start
    Task? _loop;
    bool _disposed;

    /// <param name="backend">The update source, or null when this build has none (no repository configured).</param>
    /// <param name="automaticChecks">Whether scheduled checks may run (read before each one, so the setting applies live).</param>
    public Updater(IUpdateBackend? backend, Func<bool> automaticChecks, ILog log)
    {
        _backend = backend;
        _automaticChecks = automaticChecks;
        _log = log;
        var unavailable = backend is null ? UpdateStatus.NoSource : IsInstalledSafe(backend, log) ? null : UpdateStatus.NotInstalled;
        IsAvailable = unavailable is null;
        _status = _settled = unavailable ?? UpdateStatus.Idle;
    }

    /// <summary>Whether this copy can update itself at all (installed by Velopack, with an update source).</summary>
    public bool IsAvailable { get; }

    public UpdateStatus Status
    {
        get
        {
            lock (_gate)
                return _status;
        }
    }

    /// <summary>The downloaded release waiting for a restart, if any.</summary>
    public string? ReadyVersion
    {
        get
        {
            lock (_gate)
                return _ready?.Version;
        }
    }

    /// <summary>Raised on a worker thread whenever <see cref="Status"/> changes. UIs marshal it themselves.</summary>
    public event EventHandler<UpdateStatus>? StatusChanged;

    /// <summary>Starts the schedule: a first check after <paramref name="firstDelay"/>, then one every <paramref name="interval"/>.</summary>
    public void Start(TimeSpan? firstDelay = null, TimeSpan? interval = null)
    {
        lock (_gate)
        {
            if (!IsAvailable || _disposed || _loop is not null)
                return;
            var first = firstDelay ?? DefaultFirstCheckDelay;
            var every = interval ?? DefaultCheckInterval;
            _loop = Task.Run(() => ScheduleAsync(first, every, _stop.Token));
        }
    }

    /// <summary>
    /// A check the user asked for (Settings › About): runs even when automatic checks are off, and a failure is shown
    /// (<see cref="UpdateStage.Failed"/>). Completes with the resulting status; never throws.
    /// </summary>
    public Task<UpdateStatus> CheckNowAsync() => BeginCheck(userAsked: true);

    /// <summary>
    /// Hands the downloaded update to the installer, which waits for Clementine to exit, installs it and starts it again
    /// with <paramref name="restartArgs"/>. A running background check is stopped first, and no new one starts. True
    /// when the installer is waiting (the caller must exit now; the status is then <see cref="UpdateStage.Restarting"/>
    /// for good); false when nothing is ready, a restart is already under way, or the installer couldn't be started
    /// (logged; checks then carry on as before).
    /// </summary>
    public async Task<bool> ApplyAfterExitAsync(IReadOnlyList<string> restartArgs)
    {
        UpdateOffer? ready;
        Task? check;
        lock (_gate)
        {
            ready = _ready;
            check = _check;
            if (ready is null || _backend is null || _disposed || _applying)
                return false;
            // No new check from here on (scheduled, or "Check for updates" in a Settings window that is still open
            // while the quit drain runs): one that found an even newer release would download it, and Velopack cleans
            // out older packages after a download, perhaps the one the installer is about to apply.
            _applying = true;
            _checkCancel?.Cancel();
        }
        if (check is { IsCompleted: false })
        {
            try
            {
                await check.WaitAsync(StopCheckTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _log.Warn("Update: a background check didn't stop in time; applying anyway");
            }
            // It may have finished downloading an even newer release before it stopped.
            lock (_gate)
                ready = _ready ?? ready;
        }
        try
        {
            await Task.Run(() => _backend.ApplyAfterExit(ready, restartArgs)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn($"Update: couldn't start installing {ready.Version}", ex);
            lock (_gate)
                _applying = false;
            return false;
        }
        _log.Info($"Update: installing {ready.Version} once Clementine exits");
        // The installer is waiting for this process: stop the schedule for good and say so (the About page then turns
        // its buttons off, the tray drops "Restart to update").
        _stop.Cancel();
        Publish(new UpdateStatus(UpdateStage.Restarting, ready.Version));
        return true;
    }

    async Task ScheduleAsync(TimeSpan firstDelay, TimeSpan interval, CancellationToken cancellationToken)
    {
        var delay = firstDelay;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            delay = interval;
            bool automatic;
            try
            {
                automatic = _automaticChecks();
            }
            catch (Exception ex)
            {
                _log.Warn("Update: couldn't read the update setting", ex);
                automatic = false;
            }
            if (!automatic)
                continue;
            await BeginCheck(userAsked: false).ConfigureAwait(false);
        }
    }

    Task<UpdateStatus> BeginCheck(bool userAsked)
    {
        lock (_gate)
        {
            if (!IsAvailable || _disposed || _applying)
                return Task.FromResult(_status);
            if (_check is { IsCompleted: false } running)
            {
                _userWaiting |= userAsked;
                return running;
            }
            _userWaiting = userAsked;
            _checkCancel?.Dispose();
            _checkCancel = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            var token = _checkCancel.Token;
            _check = Task.Run(() => CheckAsync(token));
            return _check;
        }
    }

    async Task<UpdateStatus> CheckAsync(CancellationToken cancellationToken)
    {
        var backend = _backend!;
        // With an update already downloaded, "Ready" stays on screen (and in the tray) while a later check looks for,
        // and maybe downloads, an even newer release.
        var quiet = ReadyVersion is not null;
        try
        {
            if (!quiet)
                Publish(new UpdateStatus(UpdateStage.Checking));

            UpdateOffer? offer;
            try
            {
                offer = await backend.CheckAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _log.Warn("Update check failed (offline, rate-limited, no release feed yet or an unreadable feed)", ex);
                return Fail(UpdateStatus.CheckFailedText);
            }
            // A backend that ignored the cancellation (a restart stopped this check) must not start a download now.
            cancellationToken.ThrowIfCancellationRequested();

            if (offer is null || string.IsNullOrWhiteSpace(offer.Version))
            {
                _log.Info("Update check: up to date");
                return Settle(ReadyVersion is null ? new UpdateStatus(UpdateStage.UpToDate) : _settled);
            }
            if (string.Equals(offer.Version, ReadyVersion, StringComparison.OrdinalIgnoreCase))
                return Settle(_settled);

            _log.Info($"Update check: {offer.Version} is available; downloading");
            if (!quiet)
                Publish(new UpdateStatus(UpdateStage.Downloading, offer.Version));
            var lastPercent = 0;
            try
            {
                await backend.DownloadAsync(offer, percent =>
                {
                    percent = Math.Clamp(percent, 0, 100);
                    if (quiet || percent == lastPercent || cancellationToken.IsCancellationRequested)
                        return;
                    lastPercent = percent;
                    Publish(new UpdateStatus(UpdateStage.Downloading, offer.Version, percent));
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _log.Warn($"Update: downloading {offer.Version} failed", ex);
                return Fail($"Couldn't download {offer.Version} — try again later");
            }

            lock (_gate)
                _ready = offer;
            _log.Info($"Update: {offer.Version} is downloaded and ready");
            return Settle(new UpdateStatus(UpdateStage.Ready, offer.Version));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Settle(_settled);
        }
        catch (Exception ex)
        {
            // A bug in the update path must never surface anywhere but the log.
            _log.Warn("Update check failed unexpectedly", ex);
            return Fail(UpdateStatus.CheckFailedText);
        }
    }

    /// <summary>A failure is shown only when the user is waiting for this check; otherwise the last outcome stays.</summary>
    UpdateStatus Fail(string message)
    {
        bool shown;
        lock (_gate)
            shown = _userWaiting && _ready is null;
        return Publish(shown ? new UpdateStatus(UpdateStage.Failed, Message: message) : _settled);
    }

    UpdateStatus Settle(UpdateStatus status)
    {
        lock (_gate)
            _settled = status;
        return Publish(status);
    }

    UpdateStatus Publish(UpdateStatus status)
    {
        lock (_gate)
        {
            // Restarting is final: a late result from a check that ignored its cancellation can't replace it.
            if (_disposed || _status == status || _status.Stage == UpdateStage.Restarting)
                return status;
            _status = status;
        }
        try
        {
            StatusChanged?.Invoke(this, status);
        }
        catch (Exception ex)
        {
            _log.Warn("An update status listener failed", ex);
        }
        return status;
    }

    static bool IsInstalledSafe(IUpdateBackend backend, ILog log)
    {
        try
        {
            return backend.IsInstalled;
        }
        catch (Exception ex)
        {
            log.Warn("Update: couldn't tell whether this copy is installed; not checking for updates", ex);
            return false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        _stop.Cancel();
    }
}
