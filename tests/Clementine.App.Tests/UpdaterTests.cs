using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Clementine.App.Preferences;
using Clementine.App.Startup;
using Clementine.App.Toasts;
using Clementine.App.Updates;
using Clementine.Core;
using Clementine.TestSupport;

namespace Clementine.App.Tests;

/// <summary>An update source that never goes online: scripted offers, failures and gates.</summary>
internal sealed class FakeUpdateBackend : IUpdateBackend
{
    int _checks;
    int _downloads;

    public bool IsInstalled { get; set; } = true;

    /// <summary>What the next checks return (null = up to date).</summary>
    public string? Offer { get; set; }

    public Exception? CheckFailure { get; set; }

    public Exception? DownloadFailure { get; set; }

    public Exception? ApplyFailure { get; set; }

    /// <summary>When set, checks wait for it (to test overlapping requests).</summary>
    public TaskCompletionSource? CheckGate { get; set; }

    public int Checks => Volatile.Read(ref _checks);

    public int Downloads => Volatile.Read(ref _downloads);

    public List<(string Version, IReadOnlyList<string> Args)> Applied { get; } = [];

    public async Task<UpdateOffer?> CheckAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _checks);
        if (CheckGate is { } gate)
            await gate.Task.WaitAsync(cancellationToken);
        if (CheckFailure is { } failure)
            throw failure;
        return Offer is null ? null : new UpdateOffer(Offer, "velopack-update-info");
    }

    public Task DownloadAsync(UpdateOffer offer, Action<int> progress, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _downloads);
        if (DownloadFailure is { } failure)
            throw failure;
        foreach (var percent in new[] { 0, 10, 10, 55, 100 })
            progress(percent);
        return Task.CompletedTask;
    }

    public void ApplyAfterExit(UpdateOffer offer, IReadOnlyList<string> restartArgs)
    {
        if (ApplyFailure is { } failure)
            throw failure;
        lock (Applied)
            Applied.Add((offer.Version, restartArgs));
    }
}

public class UpdaterTests
{
    readonly RecordingLog _log = new();
    readonly List<UpdateStatus> _seen = [];
    bool _automatic = true;

    Updater Create(IUpdateBackend? backend)
    {
        var updater = new Updater(backend, () => _automatic, _log);
        updater.StatusChanged += (_, status) =>
        {
            lock (_seen)
                _seen.Add(status);
        };
        return updater;
    }

    List<UpdateStatus> Seen()
    {
        lock (_seen)
            return [.. _seen];
    }

    static async Task WaitFor(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The condition never became true.");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task No_update_means_up_to_date_and_nothing_is_downloaded()
    {
        var backend = new FakeUpdateBackend();
        using var updater = Create(backend);
        Assert.True(updater.IsAvailable);
        Assert.Equal(UpdateStage.Idle, updater.Status.Stage);

        var status = await updater.CheckNowAsync();

        Assert.Equal(UpdateStage.UpToDate, status.Stage);
        Assert.Equal("You're up to date", status.Text);
        Assert.Equal(1, backend.Checks);
        Assert.Equal(0, backend.Downloads);
        Assert.Null(updater.ReadyVersion);
        Assert.Equal([UpdateStage.Checking, UpdateStage.UpToDate], Seen().Select(s => s.Stage));
    }

    [Fact]
    public async Task An_update_is_downloaded_quietly_then_offered_for_a_restart()
    {
        var backend = new FakeUpdateBackend { Offer = "0.1.3" };
        using var updater = Create(backend);

        var status = await updater.CheckNowAsync();

        Assert.Equal(UpdateStage.Ready, status.Stage);
        Assert.Equal("0.1.3", status.Version);
        Assert.Equal("Clementine 0.1.3 is ready — restart to update", status.Text);
        Assert.Equal("0.1.3", updater.ReadyVersion);
        var seen = Seen();
        Assert.Equal(UpdateStage.Checking, seen[0].Stage);
        Assert.Equal("Downloading 0.1.3…", seen[1].Text);
        // Progress is reported once per new percentage, never backwards or twice.
        Assert.Equal([10, 55, 100], seen.Where(s => s.Stage == UpdateStage.Downloading).Skip(1).Select(s => s.Percent));
        Assert.Equal(UpdateStage.Ready, seen[^1].Stage);

        // Applying hands the release to the installer with the restart arguments, once nothing else runs.
        Assert.True(await updater.ApplyAfterExitAsync(VelopackHooks.RestartArgs));
        var applied = Assert.Single(backend.Applied);
        Assert.Equal("0.1.3", applied.Version);
        Assert.Equal(["--background"], applied.Args);
    }

    [Fact]
    public async Task A_ready_update_stays_ready_and_isnt_downloaded_again()
    {
        var backend = new FakeUpdateBackend { Offer = "0.1.3" };
        using var updater = Create(backend);
        await updater.CheckNowAsync();
        var before = Seen().Count;

        var again = await updater.CheckNowAsync();

        Assert.Equal(UpdateStage.Ready, again.Stage);
        Assert.Equal(1, backend.Downloads);
        // While an update waits, later checks are quiet: no "Checking…" replaces "ready" on screen or in the tray.
        Assert.Equal(before, Seen().Count);

        // An even newer release replaces it without ever leaving the ready state.
        backend.Offer = "0.1.4";
        var newer = await updater.CheckNowAsync();
        Assert.Equal("0.1.4", newer.Version);
        Assert.Equal(2, backend.Downloads);
        Assert.All(Seen().Skip(before), s => Assert.Equal(UpdateStage.Ready, s.Stage));
    }

    [Fact]
    public async Task Once_a_restart_hands_the_update_over_nothing_checks_or_downloads_again()
    {
        var backend = new FakeUpdateBackend { Offer = "0.1.3" };
        using var updater = Create(backend);
        Assert.Equal(UpdateStage.Ready, (await updater.CheckNowAsync()).Stage);
        updater.Start(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10));
        await WaitFor(() => backend.Checks >= 2); // the schedule is running

        Assert.True(await updater.ApplyAfterExitAsync(VelopackHooks.RestartArgs));

        Assert.Equal(new UpdateStatus(UpdateStage.Restarting, "0.1.3"), updater.Status);
        Assert.Equal("Restarting to install Clementine 0.1.3…", updater.Status.Text);
        Assert.Equal(UpdateStage.Restarting, Seen()[^1].Stage);
        // An even newer release appears while the quit drain runs: neither the schedule nor "Check for updates" in a
        // Settings window that is still open looks for it, so nothing can replace the package being installed.
        backend.Offer = "0.1.4";
        await Task.Delay(100);
        var checks = backend.Checks;
        Assert.Equal(UpdateStage.Restarting, (await updater.CheckNowAsync()).Stage);
        await Task.Delay(200);
        Assert.Equal(checks, backend.Checks);
        Assert.Equal(1, backend.Downloads);
        Assert.Equal("0.1.3", updater.ReadyVersion);
        Assert.Equal(UpdateStage.Restarting, updater.Status.Stage);
        // A second "Restart to update" (the tray and the card at once) doesn't start the installer twice.
        Assert.False(await updater.ApplyAfterExitAsync(VelopackHooks.RestartArgs));
        Assert.Equal("0.1.3", Assert.Single(backend.Applied).Version);
    }

    [Fact]
    public async Task A_restart_whose_installer_couldnt_start_leaves_the_updater_working()
    {
        var backend = new FakeUpdateBackend { Offer = "0.1.3", ApplyFailure = new FileNotFoundException("Update.exe is missing.") };
        using var updater = Create(backend);
        await updater.CheckNowAsync();

        Assert.False(await updater.ApplyAfterExitAsync(VelopackHooks.RestartArgs));
        Assert.Equal(new UpdateStatus(UpdateStage.Ready, "0.1.3"), updater.Status);

        backend.Offer = "0.1.4";
        Assert.Equal("0.1.4", (await updater.CheckNowAsync()).Version);
        Assert.Equal(2, backend.Checks);
        backend.ApplyFailure = null;
        Assert.True(await updater.ApplyAfterExitAsync(VelopackHooks.RestartArgs));
        Assert.Equal("0.1.4", Assert.Single(backend.Applied).Version);
        Assert.Equal(new UpdateStatus(UpdateStage.Restarting, "0.1.4"), updater.Status);
    }

    [Fact]
    public async Task Offline_is_shown_only_when_the_user_asked_and_always_logged()
    {
        var backend = new FakeUpdateBackend { CheckFailure = new HttpRequestException("No such host is known. (api.github.com:443)") };
        using var updater = Create(backend);

        var asked = await updater.CheckNowAsync();
        Assert.Equal(UpdateStage.Failed, asked.Stage);
        Assert.Equal("Couldn't reach GitHub — try again later", asked.Text);
        Assert.Contains(_log.Lines, l => l.StartsWith("[WARN] Update check failed", StringComparison.Ordinal) && l.Contains("No such host", StringComparison.Ordinal));

        // In the background the same failure changes nothing visible: the last real outcome stays.
        backend.CheckFailure = null;
        Assert.Equal(UpdateStage.UpToDate, (await updater.CheckNowAsync()).Stage);
        backend.CheckFailure = new HttpRequestException("Response status code does not indicate success: 403 (rate limit exceeded).");
        lock (_seen)
            _seen.Clear();
        updater.Start(TimeSpan.Zero, TimeSpan.FromHours(1));
        await WaitFor(() => backend.Checks >= 3 && _log.Lines.Count(l => l.Contains("403", StringComparison.Ordinal)) == 1);
        await WaitFor(() => updater.Status.Stage == UpdateStage.UpToDate);
        Assert.DoesNotContain(Seen(), s => s.Stage == UpdateStage.Failed);
    }

    [Theory]
    [InlineData("malformed feed")]
    [InlineData("no releases yet")]
    public async Task Broken_feeds_fail_quietly_in_the_background(string problem)
    {
        var backend = new FakeUpdateBackend
        {
            CheckFailure = problem == "malformed feed"
                ? new System.Text.Json.JsonException("'<' is an invalid start of a value.")
                : new InvalidOperationException("No assets found in release 'v0.1.1' for channel 'win'."),
        };
        using var updater = Create(backend);
        updater.Start(TimeSpan.Zero, TimeSpan.FromHours(1));

        await WaitFor(() => _log.Lines.Any(l => l.StartsWith("[WARN] Update check failed", StringComparison.Ordinal)));
        await WaitFor(() => updater.Status.Stage == UpdateStage.Idle);
        Assert.DoesNotContain(Seen(), s => s.Stage == UpdateStage.Failed);
    }

    [Fact]
    public async Task A_failed_download_is_reported_to_the_user_who_asked()
    {
        var backend = new FakeUpdateBackend { Offer = "0.1.3", DownloadFailure = new IOException("The network name is no longer available.") };
        using var updater = Create(backend);

        var status = await updater.CheckNowAsync();

        Assert.Equal(UpdateStage.Failed, status.Stage);
        Assert.Equal("Couldn't download 0.1.3 — try again later", status.Text);
        Assert.Null(updater.ReadyVersion);
        Assert.False(await updater.ApplyAfterExitAsync(VelopackHooks.RestartArgs));
        Assert.Empty(backend.Applied);
    }

    [Fact]
    public async Task Turning_automatic_checks_off_stops_the_schedule_but_not_a_check_the_user_asks_for()
    {
        _automatic = false;
        var backend = new FakeUpdateBackend { Offer = "0.1.3" };
        using var updater = Create(backend);

        updater.Start(TimeSpan.Zero, TimeSpan.FromMilliseconds(20));
        await Task.Delay(400);
        Assert.Equal(0, backend.Checks);
        Assert.Equal(UpdateStage.Idle, updater.Status.Stage);

        Assert.Equal(UpdateStage.Ready, (await updater.CheckNowAsync()).Stage);
        Assert.Equal(1, backend.Checks);

        // The setting is read before every scheduled check, so switching it on applies without a restart.
        _automatic = true;
        await WaitFor(() => backend.Checks >= 3);
    }

    [Fact]
    public async Task The_schedule_checks_after_the_first_delay_then_on_every_interval_until_disposed()
    {
        var backend = new FakeUpdateBackend();
        var updater = Create(backend);
        updater.Start(TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(50));
        await Task.Delay(150);
        Assert.Equal(0, backend.Checks);

        await WaitFor(() => backend.Checks >= 3);
        updater.Dispose();
        await Task.Delay(200);
        var checks = backend.Checks;
        await Task.Delay(300);
        Assert.Equal(checks, backend.Checks);
        Assert.Equal(TimeSpan.FromSeconds(60), Updater.DefaultFirstCheckDelay);
        Assert.Equal(TimeSpan.FromHours(6), Updater.DefaultCheckInterval);
    }

    [Fact]
    public async Task Overlapping_requests_share_one_check()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new FakeUpdateBackend { CheckGate = gate };
        using var updater = Create(backend);

        var first = updater.CheckNowAsync();
        var second = updater.CheckNowAsync();
        await WaitFor(() => backend.Checks == 1);
        Assert.Equal(UpdateStage.Checking, updater.Status.Stage);
        gate.SetResult();

        Assert.Equal(UpdateStage.UpToDate, (await first).Stage);
        Assert.Equal(UpdateStage.UpToDate, (await second).Stage);
        Assert.Equal(1, backend.Checks);
    }

    [Fact]
    public async Task A_copy_that_isnt_installed_never_checks()
    {
        var backend = new FakeUpdateBackend { IsInstalled = false, Offer = "0.1.3" };
        using var updater = Create(backend);
        Assert.False(updater.IsAvailable);
        Assert.Equal(UpdateStatus.NotInstalled, updater.Status);

        updater.Start(TimeSpan.Zero, TimeSpan.FromMilliseconds(10));
        Assert.Equal(UpdateStage.Unavailable, (await updater.CheckNowAsync()).Stage);
        await Task.Delay(200);
        Assert.Equal(0, backend.Checks);
        Assert.False(await updater.ApplyAfterExitAsync(VelopackHooks.RestartArgs));
        Assert.Empty(Seen());
    }

    [Fact]
    public async Task A_build_without_an_update_repository_never_checks()
    {
        using var updater = Create(null);
        Assert.False(updater.IsAvailable);
        Assert.Equal(UpdateStatus.NoSource, updater.Status);
        Assert.Equal("This build has no update source.", updater.Status.Text);
        updater.Start(TimeSpan.Zero, TimeSpan.FromMilliseconds(10));
        Assert.Equal(UpdateStatus.NoSource, await updater.CheckNowAsync());
    }

    [Fact]
    public async Task A_backend_that_throws_while_installing_or_reporting_never_escapes()
    {
        var backend = new FakeUpdateBackend { Offer = "0.1.3", ApplyFailure = new FileNotFoundException("Update.exe is missing.") };
        using var updater = Create(backend);
        updater.StatusChanged += (_, _) => throw new InvalidOperationException("a listener broke");

        Assert.Equal(UpdateStage.Ready, (await updater.CheckNowAsync()).Stage);
        Assert.False(await updater.ApplyAfterExitAsync(VelopackHooks.RestartArgs));
        Assert.Contains(_log.Lines, l => l.Contains("couldn't start installing 0.1.3", StringComparison.Ordinal));
        Assert.Contains(_log.Lines, l => l.Contains("a listener broke", StringComparison.Ordinal));
    }

    [Fact]
    public void Throwing_while_asking_whether_installed_counts_as_not_installed()
    {
        using var updater = new Updater(new ExplodingBackend(), () => true, _log);
        Assert.False(updater.IsAvailable);
        Assert.Contains(_log.Lines, l => l.Contains("couldn't tell whether this copy is installed", StringComparison.Ordinal));
    }

    sealed class ExplodingBackend : IUpdateBackend
    {
        public bool IsInstalled => throw new UnauthorizedAccessException("sq.version is locked");

        public Task<UpdateOffer?> CheckAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DownloadAsync(UpdateOffer offer, Action<int> progress, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void ApplyAfterExit(UpdateOffer offer, IReadOnlyList<string> restartArgs) => throw new NotSupportedException();
    }
}

public class UpdateSetupTests
{
    static string RepoRoot => typeof(UpdateSetupTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "RepoRoot").Value!;

    [Theory]
    [InlineData("https://github.com/va-plugin/clementine", "https://github.com/va-plugin/clementine")]
    [InlineData("  https://github.com/va-plugin/clementine/ ", "https://github.com/va-plugin/clementine")]
    [InlineData("https://github.com/Owner/My.Repo.git", "https://github.com/Owner/My.Repo")]
    [InlineData("https://GitHub.com/a_b/c-d", "https://github.com/a_b/c-d")]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("http://github.com/va-plugin/clementine", null)]
    [InlineData("https://gitlab.com/va-plugin/clementine", null)]
    [InlineData("https://github.com/va-plugin", null)]
    [InlineData("https://github.com/va-plugin/clementine/releases", null)]
    [InlineData("https://github.com/va-plugin/clementine?tab=readme", null)]
    [InlineData("github.com/va-plugin/clementine", null)]
    public void Only_github_repository_urls_are_update_sources(string? value, string? expected) =>
        Assert.Equal(expected, UpdateSetup.NormalizeRepository(value));

    [Fact]
    public void The_app_reads_the_repository_from_directory_build_props()
    {
        var props = File.ReadAllText(Path.Combine(RepoRoot, "Directory.Build.props"));
        var configured = Regex.Match(props, "<ClementineUpdateRepo>(?<url>[^<]*)</ClementineUpdateRepo>").Groups["url"].Value;
        Assert.Equal(UpdateSetup.NormalizeRepository(configured), UpdateSetup.RepositoryUrl);
        Assert.Equal(UpdateSetup.RepositoryUrl ?? UpdateSetup.DefaultProjectUrl, UpdateSetup.ProjectUrl);
        Assert.EndsWith("/releases/tag/v0.1.3", UpdateSetup.ReleaseUrl("0.1.3"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_development_build_isnt_a_velopack_install_so_velopack_is_never_asked()
    {
        var log = new RecordingLog();
        // The test host runs from the build output: no Update.exe above it.
        var backend = VelopackUpdateBackend.ForGitHub("https://github.com/va-plugin/clementine", log, AppContext.BaseDirectory);
        Assert.False(backend.IsInstalled);
        using var updater = new Updater(backend, () => true, log);
        Assert.Equal(UpdateStatus.NotInstalled, updater.Status);
        Assert.Empty(log.Lines);
    }

    [Fact]
    public void The_bundled_versions_named_in_the_app_match_what_publish_ps1_bundles()
    {
        var publish = File.ReadAllText(Path.Combine(RepoRoot, "publish.ps1"));
        var ffmpeg = Regex.Match(publish, @"/ffmpeg-(?<v>\d+(?:\.\d+)+)-[A-Za-z_-]+\.7z'").Groups["v"].Value;
        var sevenZip = Regex.Match(publish, @"/7z(?<v>\d{4})-extra\.7z'").Groups["v"].Value;
        Assert.Equal(Licences.BundledFfmpegVersion, ffmpeg);
        Assert.Equal(Licences.BundledSevenZipVersion.Replace(".", "", StringComparison.Ordinal), sevenZip);
        var notices = File.ReadAllText(Licences.NoticesFile()!);
        Assert.Contains($"FFmpeg {Licences.BundledFfmpegVersion}", notices, StringComparison.Ordinal);
        Assert.Contains($"7-Zip {Licences.BundledSevenZipVersion}", notices, StringComparison.Ordinal);
        Assert.Contains("FFmpeg-SOURCE.txt", notices, StringComparison.Ordinal);
    }

    [Fact]
    public void The_release_workflow_attaches_what_publish_ps1_writes_and_publishes_only_a_complete_draft()
    {
        var workflow = File.ReadAllText(Path.Combine(RepoRoot, ".github", "workflows", "release.yml"));
        var publish = File.ReadAllText(Path.Combine(RepoRoot, "publish.ps1"));

        // publish.ps1 tells the workflow where the installer copies are, so the workflow never repeats the folder layout.
        Assert.Contains("\"product_dir=$product`n\"", publish, StringComparison.Ordinal);
        Assert.Contains("id: publish", workflow, StringComparison.Ordinal);
        Assert.Contains("PRODUCT_DIR: ${{ steps.publish.outputs.product_dir }}", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("'Product'", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("Clementine Product", workflow, StringComparison.Ordinal);
        foreach (var name in new[] { "Clementine-Setup.exe", "Clementine-Portable.zip" })
        {
            Assert.Contains($"Join-Path $product '{name}'", publish, StringComparison.Ordinal);
            Assert.Contains($"'{name}'", workflow, StringComparison.Ordinal);
        }

        // vpk leaves the release a draft (invisible to installed copies); the last step publishes it, after the
        // installer copies and the FFmpeg and 7-Zip source are attached and checked.
        Assert.DoesNotContain("'--publish'", workflow, StringComparison.Ordinal);
        var upload = workflow.IndexOf("'vpk', 'upload', 'github'", StringComparison.Ordinal);
        var attach = workflow.IndexOf("gh release upload", StringComparison.Ordinal);
        var check = workflow.IndexOf("name: Check the draft has every file", StringComparison.Ordinal);
        var undraft = workflow.IndexOf("gh release edit $tag --draft=false", StringComparison.Ordinal);
        Assert.True(upload > 0 && attach > upload && check > attach && undraft > check,
            "Expected vpk upload, then gh release upload, then the file check, then gh release edit --draft=false.");
        var checkStep = workflow[check..undraft];
        Assert.Contains("clementine-sources", checkStep, StringComparison.Ordinal);
        Assert.Contains("$missing.Count -gt 0", checkStep, StringComparison.Ordinal);
        Assert.Contains("throw", checkStep, StringComparison.Ordinal);
    }

    [Fact]
    public void The_licence_texts_name_every_7zip_licence_and_offer_the_ffmpeg_library_source()
    {
        // 7z2501-extra's License.txt: 7za.exe is GNU LGPL, with BSD 3-clause (zstd decoder) and BSD 2-clause (XXH64) code.
        var sevenZip = Licences.BundledPrograms.Single(p => p.Name.StartsWith("7-Zip", StringComparison.Ordinal));
        Assert.Equal("LGPL-2.1 · BSD-3-Clause · BSD-2-Clause", sevenZip.Licence);
        var notices = File.ReadAllText(Licences.NoticesFile()!);
        Assert.Contains("with BSD-3-Clause parts (zstd decoder) and BSD-2-Clause parts (XXH64)", Flat(notices), StringComparison.Ordinal);
        Assert.Contains("BSD 2-Clause License (Markdig, parts of 7-Zip", notices, StringComparison.Ordinal);

        // The FFmpeg DLLs compile in GPL libraries whose source isn't attached to the releases: a three-year written offer
        // covers them wherever the source is described (the notices, licenses\FFmpeg-SOURCE.txt and the README).
        var publish = File.ReadAllText(Path.Combine(RepoRoot, "publish.ps1"));
        var readme = File.ReadAllText(Path.Combine(RepoRoot, "README.md"));
        foreach (var text in new[] { notices, publish, readme })
        {
            Assert.Contains("for at least three years after the last clementine release that includes this ffmpeg build",
                Flat(text).ToLowerInvariant(), StringComparison.Ordinal);
            Assert.Contains("\"FFmpeg source request\"", text, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("we will provide", Flat(notices), StringComparison.Ordinal);
    }

    /// <summary>The text with every run of white space (line breaks and indents included) as one space.</summary>
    static string Flat(string text) => Regex.Replace(text, @"\s+", " ");
}

public class UpdateUiTests
{
    [Fact]
    public void The_update_card_and_tray_item_name_the_version()
    {
        var ready = ToastState.UpdateReady("0.1.3");
        Assert.Equal("Clementine 0.1.3 is ready", ready.Title);
        Assert.Equal("Restart to update", ready.ActionLabel);
        Assert.Equal(ToastKind.Info, ready.Kind);
        Assert.Equal("Updated to Clementine 0.1.3", ToastState.Updated("0.1.3").Title);

        WpfHost.Run(() =>
        {
            var restarts = 0;
            var noop = () => { };
            using var tray = new Tray.TrayIcon(new Tray.TrayCommands(noop, noop, noop, noop, noop, noop, RestartToUpdate: () => restarts++));
            tray.SetUpdateReady("0.1.3"); // before the menu exists
            var menu = tray.MenuForTest;
            Assert.Equal("Restart to update (0.1.3)", tray.VisibleUpdateItemHeader);
            ((MenuItem)menu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(1, restarts);
            tray.SetUpdateReady(null);
            Assert.Null(tray.VisibleUpdateItemHeader);
            tray.SetUpdateReady("0.1.4");
            Assert.Equal("Restart to update (0.1.4)", tray.VisibleUpdateItemHeader);
        });
    }

    [Fact]
    public async Task Settings_offer_the_automatic_check_and_about_shows_the_status_with_a_check_button()
    {
        using var harness = new SettingsHarness();
        var backend = new FakeUpdateBackend { Offer = "0.1.3" };
        using var updates = new Updater(backend, () => harness.Services.Settings.Current.CheckForUpdates, harness.Workspace.Log);
        var restarts = 0;
        SettingsWindow window = null!;
        WpfHost.Run(() =>
        {
            window = harness.CreateWindow(updates, () => restarts++);
            window.ShowPage(SettingsPage.General);
            var toggle = FindRowToggle(window, "Check for updates automatically");
            Assert.True(toggle.IsChecked);
            toggle.IsChecked = false;
            toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        });
        Assert.False(harness.Services.Settings.Current.CheckForUpdates);

        WpfHost.Run(() =>
        {
            window.ShowPage(SettingsPage.About);
            Assert.Equal("Automatic checks are off (Settings › General). Check whenever you like.", window.ShownUpdateStatus);
            var check = Buttons(window).Single(b => Equals(b.Content, "Check for updates"));
            Assert.True(check.IsEnabled);
            check.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        });
        // Wait for the whole Ready state (status and button), not just the text: on a slow machine the check's own
        // continuation can repaint the card once more after the status first reads "ready".
        await WaitForUi(() => window.ShownUpdateStatus == "Clementine 0.1.3 is ready — restart to update"
            && Buttons(window).Single(b => Equals(b.Content, "Restart to update")).Visibility == Visibility.Visible);
        WpfHost.Run(() =>
        {
            var restart = Buttons(window).Single(b => Equals(b.Content, "Restart to update"));
            Assert.Equal(Visibility.Visible, restart.Visibility);
            restart.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Equal(1, restarts);
            window.Close();
        });
    }

    [Fact]
    public async Task Once_the_restart_has_begun_about_offers_neither_a_check_nor_another_restart()
    {
        using var harness = new SettingsHarness();
        var backend = new FakeUpdateBackend { Offer = "0.1.3" };
        using var updates = new Updater(backend, () => true, harness.Workspace.Log);
        Assert.Equal(UpdateStage.Ready, (await updates.CheckNowAsync()).Stage);
        SettingsWindow window = null!;
        WpfHost.Run(() =>
        {
            window = harness.CreateWindow(updates, () => { });
            window.ShowPage(SettingsPage.About);
            Assert.True(Buttons(window).Single(b => Equals(b.Content, "Check for updates")).IsEnabled);
            Assert.Equal(Visibility.Visible, Buttons(window).Single(b => Equals(b.Content, "Restart to update")).Visibility);
        });

        // The window stays open while the quit drain runs.
        Assert.True(await updates.ApplyAfterExitAsync(VelopackHooks.RestartArgs));
        await WaitForUi(() => window.ShownUpdateStatus == "Restarting to install Clementine 0.1.3…");
        WpfHost.Run(() =>
        {
            try
            {
                Assert.False(Buttons(window).Single(b => Equals(b.Content, "Check for updates")).IsEnabled);
                Assert.Equal(Visibility.Collapsed, Buttons(window).Single(b => Equals(b.Content, "Restart to update")).Visibility);
                // Coming back to the page shows the same.
                window.ShowPage(SettingsPage.General);
                window.ShowPage(SettingsPage.About);
                Assert.Equal("Restarting to install Clementine 0.1.3…", window.ShownUpdateStatus);
                Assert.False(Buttons(window).Single(b => Equals(b.Content, "Check for updates")).IsEnabled);
            }
            finally
            {
                window.Close();
            }
        });
        Assert.Equal(1, backend.Checks);
    }

    [Fact]
    public void About_says_why_a_copy_that_isnt_installed_doesnt_check()
    {
        using var harness = new SettingsHarness();
        using var updates = new Updater(new FakeUpdateBackend { IsInstalled = false }, () => true, harness.Workspace.Log);
        WpfHost.Run(() =>
        {
            var window = harness.CreateWindow(updates);
            try
            {
                window.ShowPage(SettingsPage.About);
                Assert.Equal(UpdateStatus.NotInstalled.Text, window.ShownUpdateStatus);
                Assert.False(Buttons(window).Single(b => Equals(b.Content, "Check for updates")).IsEnabled);
                Assert.Contains(Texts(window), t => t.Contains("Clementine is free and open source under the MIT licence", StringComparison.Ordinal));
                Assert.Contains(Texts(window), t => t.Contains("A Visual Artform product", StringComparison.Ordinal));
            }
            finally
            {
                window.Close();
            }
        });
    }

    static async Task WaitForUi(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!WpfHost.Run(condition))
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The window never showed the expected state.");
            await Task.Delay(20);
        }
    }

    static CheckBox FindRowToggle(DependencyObject root, string title) =>
        Descendants(root).OfType<Grid>()
            .Where(g => Descendants(g).OfType<TextBlock>().Any(t => t.Text == title))
            .SelectMany(g => g.Children.OfType<CheckBox>())
            .Single();

    static IEnumerable<Button> Buttons(DependencyObject root) => Descendants(root).OfType<Button>();

    static IEnumerable<string> Texts(DependencyObject root) =>
        Descendants(root).OfType<TextBlock>().Select(t => new System.Windows.Documents.TextRange(t.ContentStart, t.ContentEnd).Text);

    static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var deeper in Descendants(child))
                yield return deeper;
        }
    }
}

public class BundledToolTests
{
    static ToolInfo Tool(string key, string name, string? path, string? version, bool bundled = false, bool optional = false, string? missing = null) =>
        new(key, name, "purpose", path, version, "https://example.com", CanOverride: true, IsBundled: bundled, IsOptional: optional, MissingNote: missing);

    [Fact]
    public void Bundled_installed_optional_and_missing_tools_read_differently()
    {
        var app = @"C:\Users\Me\AppData\Local\ClementineApp\current";
        var bundled = Tool("ffmpeg", "FFmpeg", app + @"\tools\ffmpeg\bin\ffmpeg.exe", "8.1.2", bundled: true);
        Assert.Equal(ToolState.BuiltIn, ToolStatus.StateOf(bundled));
        Assert.Equal("Built in · FFmpeg 8.1.2", ToolStatus.StatusLine(bundled));
        Assert.Equal("Built in", ToolStatus.StatusLine(bundled with { Version = null }));

        var installed = Tool("ffmpeg", "FFmpeg", @"C:\ffmpeg\bin\ffmpeg.exe", "7.1");
        Assert.Equal(ToolState.Installed, ToolStatus.StateOf(installed));
        Assert.Equal(@"C:\ffmpeg\bin\ffmpeg.exe", ToolStatus.StatusLine(installed));

        var libreOffice = Tool("libreoffice", "LibreOffice", null, null, optional: true, missing: ToolStatus.LibreOfficeMissingNote);
        Assert.Equal(ToolState.NotInstalledOptional, ToolStatus.StateOf(libreOffice));
        Assert.Equal("Optional — for PowerPoint and older Office files", ToolStatus.StatusLine(libreOffice));

        var missing = Tool("ffmpeg", "FFmpeg", null, null);
        Assert.Equal(ToolState.Missing, ToolStatus.StateOf(missing));
        Assert.Equal("Not found on this PC", ToolStatus.StatusLine(missing));
    }

    [Theory]
    [InlineData(@"C:\App\tools\ffmpeg\bin\ffmpeg.exe", @"C:\App\tools", true)]
    [InlineData(@"c:\app\TOOLS\7zip\7za.exe", @"C:\App\tools\", true)]
    [InlineData(@"C:\App\tools2\ffmpeg.exe", @"C:\App\tools", false)]
    [InlineData(@"C:\App\tools\..\ffmpeg.exe", @"C:\App\tools", false)]
    [InlineData(@"C:\ffmpeg\bin\ffmpeg.exe", @"C:\App\tools", false)]
    [InlineData(null, @"C:\App\tools", false)]
    public void Bundled_means_inside_the_apps_tools_folder(string? path, string tools, bool expected) =>
        Assert.Equal(expected, ToolStatus.IsUnder(path, tools));

    [Fact]
    public async Task Detection_marks_only_tools_from_the_app_folder_as_bundled()
    {
        // The test host has no tools folder: whatever is found here is installed on the PC, not bundled.
        var tools = await ToolStatus.DetectAsync(new ToolLocator(() => new ClementineSettings()));
        Assert.All(tools, t => Assert.False(t.IsBundled, t.Name));
        Assert.True(tools.Single(t => t.Key == ToolStatus.LibreOfficeKey).IsOptional);
        Assert.Equal(ToolStatus.BundledToolsDirectory(), Path.Combine(AppContext.BaseDirectory, "tools"));
    }
}
