using System.IO;
using Clementine.App.Startup;
using Clementine.Core;
using Velopack;
using Velopack.Sources;

namespace Clementine.App.Updates;

/// <summary>
/// <see cref="IUpdateBackend"/> on Velopack's <see cref="UpdateManager"/>. The manager is only created for a Velopack
/// install (Update.exe in the folder above the exe, the same test <see cref="VelopackHooks"/> uses), so development
/// builds and copied folders never touch Velopack. Stable releases only, no token: GitHub allows 60 anonymous API
/// requests an hour per IP address, far more than one check every 6 hours needs.
/// </summary>
internal sealed class VelopackUpdateBackend : IUpdateBackend
{
    readonly Lazy<UpdateManager?> _manager;

    VelopackUpdateBackend(Func<IUpdateSource> source, string exeDirectory, ILog log)
    {
        _manager = new Lazy<UpdateManager?>(() =>
        {
            if (!VelopackHooks.IsVelopackInstall(exeDirectory))
                return null;
            try
            {
                return new UpdateManager(source(), new UpdateOptions { AllowVersionDowngrade = false });
            }
            catch (Exception ex)
            {
                log.Warn("Update: Velopack couldn't be set up", ex);
                return null;
            }
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Updates from the GitHub repository's Releases (e.g. https://github.com/va-plugin/clementine).</summary>
    public static VelopackUpdateBackend ForGitHub(string repositoryUrl, ILog log, string? exeDirectory = null) =>
        new(() => new GithubSource(repositoryUrl, accessToken: null, prerelease: false), exeDirectory ?? AppContext.BaseDirectory, log);

    /// <summary>Updates from a local folder holding a Velopack feed (vpk pack output) — end-to-end tests of DEBUG builds only.</summary>
    public static VelopackUpdateBackend ForLocalFeed(string directory, ILog log, string? exeDirectory = null) =>
        new(() => new SimpleFileSource(new DirectoryInfo(directory)), exeDirectory ?? AppContext.BaseDirectory, log);

    public bool IsInstalled => _manager.Value?.IsInstalled == true;

    UpdateManager Manager => _manager.Value ?? throw new InvalidOperationException("This copy of Clementine isn't installed, so it can't update itself.");

    public async Task<UpdateOffer?> CheckAsync(CancellationToken cancellationToken)
    {
        // CheckForUpdatesAsync takes no token; WaitAsync lets a cancelled check return at once.
        var info = await Manager.CheckForUpdatesAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        if (info?.TargetFullRelease is not { } target || info.IsDowngrade)
            return null;
        return new UpdateOffer(target.Version.ToString(), info);
    }

    public Task DownloadAsync(UpdateOffer offer, Action<int> progress, CancellationToken cancellationToken) =>
        Manager.DownloadUpdatesAsync(Info(offer), progress, cancellationToken);

    public void ApplyAfterExit(UpdateOffer offer, IReadOnlyList<string> restartArgs) =>
        // Not silent: Velopack's own small progress window shows while the files are swapped.
        Manager.WaitExitThenApplyUpdates(Info(offer).TargetFullRelease, silent: false, restart: true, [.. restartArgs]);

    static UpdateInfo Info(UpdateOffer offer) =>
        offer.Handle as UpdateInfo ?? throw new ArgumentException("This offer didn't come from Velopack.", nameof(offer));
}
