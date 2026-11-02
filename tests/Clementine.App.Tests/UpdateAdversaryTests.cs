using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Clementine.App.Startup;
using Clementine.App.Updates;
using Clementine.Core;
using Clementine.TestSupport;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;
using Velopack.Sources;

namespace Clementine.App.Tests;

/// <summary>
/// Adversarial checks of the update path: the updater never blocks or throws into its caller, survives hostile or
/// broken backends, and the real Velopack GitHub source (driven through a recording downloader, never online) sends
/// nothing but anonymous GitHub requests, offers only newer stable releases and rejects a tampered package.
/// </summary>
public class UpdateAdversaryTests
{
    const string Repo = "https://github.com/va-plugin/clementine";

    readonly RecordingLog _log = new();

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

    // ===== The updater against hostile backends =====

    /// <summary>A backend that does its blocking work synchronously, before returning a task, and records the thread.</summary>
    sealed class BlockingBackend : IUpdateBackend
    {
        public int CheckThread;
        public int IsInstalledCalls;
        public int CheckCalls;

        public bool IsInstalled
        {
            get
            {
                Interlocked.Increment(ref IsInstalledCalls);
                return true;
            }
        }

        public Task<UpdateOffer?> CheckAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref CheckCalls);
            CheckThread = Environment.CurrentManagedThreadId;
            Thread.Sleep(1500); // like a synchronous DNS lookup or a slow disk
            return Task.FromResult<UpdateOffer?>(null);
        }

        public Task DownloadAsync(UpdateOffer offer, Action<int> progress, CancellationToken cancellationToken) => Task.CompletedTask;

        public void ApplyAfterExit(UpdateOffer offer, IReadOnlyList<string> restartArgs)
        {
        }
    }

    [Fact]
    public async Task Creating_starting_and_checking_never_block_the_caller_even_when_the_backend_blocks()
    {
        var backend = new BlockingBackend();
        var clock = Stopwatch.StartNew();
        using var updater = new Updater(backend, () => true, _log);
        updater.Start(TimeSpan.Zero, TimeSpan.FromHours(1));
        var check = updater.CheckNowAsync();
        var returnedAfter = clock.Elapsed;

        Assert.True(returnedAfter < TimeSpan.FromMilliseconds(700), $"The caller was blocked for {returnedAfter.TotalMilliseconds:0} ms.");
        Assert.Equal(1, backend.IsInstalledCalls);
        Assert.Equal(UpdateStage.UpToDate, (await check).Stage);
        Assert.NotEqual(Environment.CurrentManagedThreadId, backend.CheckThread);
        // The scheduled check and the user's check overlapped: one request, not two.
        Assert.Equal(1, backend.CheckCalls);
    }

    /// <summary>A backend whose every method throws synchronously (no task at all).</summary>
    sealed class SynchronouslyThrowingBackend : IUpdateBackend
    {
        public bool IsInstalled => true;

        public Task<UpdateOffer?> CheckAsync(CancellationToken cancellationToken) =>
            throw new HttpRequestException("Response status code does not indicate success: 403 (rate limit exceeded).", null, HttpStatusCode.Forbidden);

        public Task DownloadAsync(UpdateOffer offer, Action<int> progress, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void ApplyAfterExit(UpdateOffer offer, IReadOnlyList<string> restartArgs) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_backend_that_throws_synchronously_is_contained_and_only_a_user_check_shows_it()
    {
        var seen = new List<UpdateStatus>();
        using var updater = new Updater(new SynchronouslyThrowingBackend(), () => true, _log);
        updater.StatusChanged += (_, s) =>
        {
            lock (seen)
                seen.Add(s);
        };

        updater.Start(TimeSpan.Zero, TimeSpan.FromHours(1));
        await WaitFor(() => _log.Lines.Any(l => l.Contains("403", StringComparison.Ordinal)));
        await WaitFor(() => updater.Status.Stage == UpdateStage.Idle);
        lock (seen)
            Assert.DoesNotContain(seen, s => s.Stage == UpdateStage.Failed);

        var asked = await updater.CheckNowAsync();
        Assert.Equal(UpdateStage.Failed, asked.Stage);
        Assert.Equal(UpdateStatus.CheckFailedText, asked.Text);
        Assert.False(await updater.ApplyAfterExitAsync(VelopackHooks.RestartArgs));
    }

    [Fact]
    public async Task Hammering_check_from_many_threads_makes_one_request()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new FakeUpdateBackend { CheckGate = gate };
        using var updater = new Updater(backend, () => true, _log);
        updater.Start(TimeSpan.Zero, TimeSpan.FromMilliseconds(5));

        var calls = Enumerable.Range(0, 64).Select(_ => Task.Run(updater.CheckNowAsync)).ToList();
        await WaitFor(() => backend.Checks >= 1);
        await Task.Delay(200);
        Assert.Equal(1, backend.Checks);
        gate.SetResult();
        var results = await Task.WhenAll(calls);
        Assert.All(results, r => Assert.Equal(UpdateStage.UpToDate, r.Stage));
    }

    [Fact]
    public async Task A_user_who_joins_a_failing_background_check_is_told()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new FakeUpdateBackend { CheckGate = gate, CheckFailure = new HttpRequestException("No such host is known.") };
        using var updater = new Updater(backend, () => true, _log);
        updater.Start(TimeSpan.Zero, TimeSpan.FromHours(1));
        await WaitFor(() => backend.Checks == 1);

        var joined = updater.CheckNowAsync();
        gate.SetResult();

        Assert.Equal(UpdateStage.Failed, (await joined).Stage);
        Assert.Equal(1, backend.Checks);
    }

    [Fact]
    public async Task An_offer_without_a_version_is_up_to_date_and_progress_is_clamped()
    {
        var backend = new FakeUpdateBackend { Offer = " " };
        using var updater = new Updater(backend, () => true, _log);
        Assert.Equal(UpdateStage.UpToDate, (await updater.CheckNowAsync()).Stage);
        Assert.Equal(0, backend.Downloads);

        var wild = new ProgressBackend([-20, 150, 40, 999]);
        using var second = new Updater(wild, () => true, _log);
        var percents = new List<int>();
        second.StatusChanged += (_, s) =>
        {
            if (s.Stage == UpdateStage.Downloading)
                lock (percents)
                    percents.Add(s.Percent);
        };
        Assert.Equal(UpdateStage.Ready, (await second.CheckNowAsync()).Stage);
        lock (percents)
            Assert.All(percents, p => Assert.InRange(p, 0, 100));
    }

    sealed class ProgressBackend(int[] percents) : IUpdateBackend
    {
        public bool IsInstalled => true;

        public Task<UpdateOffer?> CheckAsync(CancellationToken cancellationToken) => Task.FromResult<UpdateOffer?>(new UpdateOffer("0.1.3"));

        public Task DownloadAsync(UpdateOffer offer, Action<int> progress, CancellationToken cancellationToken)
        {
            foreach (var p in percents)
                progress(p);
            return Task.CompletedTask;
        }

        public void ApplyAfterExit(UpdateOffer offer, IReadOnlyList<string> restartArgs)
        {
        }
    }

    [Fact]
    public async Task A_setting_that_cant_be_read_skips_scheduled_checks()
    {
        var backend = new FakeUpdateBackend();
        using var updater = new Updater(backend, () => throw new IOException("settings.json is locked"), _log);
        updater.Start(TimeSpan.Zero, TimeSpan.FromMilliseconds(20));
        await WaitFor(() => _log.Lines.Count(l => l.Contains("couldn't read the update setting", StringComparison.Ordinal)) >= 2);
        Assert.Equal(0, backend.Checks);
    }

    /// <summary>Scripted: the first check offers <see cref="First"/>; later checks hang (optionally ignoring cancellation).</summary>
    sealed class HangingSecondCheckBackend(bool honourCancellation) : IUpdateBackend
    {
        public const string First = "0.1.3";
        readonly TaskCompletionSource _never = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int _checks;

        public List<string> Applied { get; } = [];

        public volatile bool SecondCheckStarted;

        public bool IsInstalled => true;

        public async Task<UpdateOffer?> CheckAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _checks) == 1)
                return new UpdateOffer(First);
            SecondCheckStarted = true;
            if (honourCancellation)
                await _never.Task.WaitAsync(cancellationToken);
            else
                await _never.Task.WaitAsync(TimeSpan.FromSeconds(30));
            return new UpdateOffer("0.1.4");
        }

        public Task DownloadAsync(UpdateOffer offer, Action<int> progress, CancellationToken cancellationToken) => Task.CompletedTask;

        public void ApplyAfterExit(UpdateOffer offer, IReadOnlyList<string> restartArgs)
        {
            lock (Applied)
                Applied.Add(offer.Version);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Restarting_stops_a_running_check_and_installs_the_ready_version(bool honourCancellation)
    {
        var backend = new HangingSecondCheckBackend(honourCancellation);
        using var updater = new Updater(backend, () => true, _log);
        Assert.Equal(UpdateStage.Ready, (await updater.CheckNowAsync()).Stage);
        var second = updater.CheckNowAsync();
        await WaitFor(() => backend.SecondCheckStarted);

        var clock = Stopwatch.StartNew();
        Assert.True(await updater.ApplyAfterExitAsync(VelopackHooks.RestartArgs));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(8), $"Applying waited {clock.Elapsed.TotalSeconds:0.0} s.");
        lock (backend.Applied)
            Assert.Equal([HangingSecondCheckBackend.First], backend.Applied);
        Assert.Equal(HangingSecondCheckBackend.First, updater.ReadyVersion);
        if (honourCancellation)
            Assert.Equal(UpdateStage.Ready, (await second.WaitAsync(TimeSpan.FromSeconds(5))).Stage);
        Assert.DoesNotContain(_log.Lines, l => l.StartsWith("[ERROR]", StringComparison.Ordinal));
    }

    /// <summary>The first check offers 0.1.3; the second ignores cancellation and offers 0.1.4 once released.</summary>
    sealed class LateNewerReleaseBackend : IUpdateBackend
    {
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool SecondCheckStarted;
        int _checks;
        int _downloads;

        public int Downloads => Volatile.Read(ref _downloads);

        public List<string> Applied { get; } = [];

        public bool IsInstalled => true;

        public async Task<UpdateOffer?> CheckAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _checks) == 1)
                return new UpdateOffer("0.1.3");
            SecondCheckStarted = true;
            await Release.Task; // deliberately deaf to the cancellation
            return new UpdateOffer("0.1.4");
        }

        public Task DownloadAsync(UpdateOffer offer, Action<int> progress, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _downloads);
            return Task.CompletedTask;
        }

        public void ApplyAfterExit(UpdateOffer offer, IReadOnlyList<string> restartArgs)
        {
            lock (Applied)
                Applied.Add(offer.Version);
        }
    }

    [Fact]
    public async Task A_check_that_finds_a_newer_release_after_the_restart_began_never_downloads_it()
    {
        var backend = new LateNewerReleaseBackend();
        var updater = new Updater(backend, () => true, _log);
        try
        {
            Assert.Equal(UpdateStage.Ready, (await updater.CheckNowAsync()).Stage);
            var late = updater.CheckNowAsync();
            await WaitFor(() => backend.SecondCheckStarted);

            // Applying gives up waiting for the deaf check after a few seconds and installs what is ready.
            Assert.True(await updater.ApplyAfterExitAsync(VelopackHooks.RestartArgs));
            backend.Release.SetResult();
            await late.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, backend.Downloads);
            Assert.Equal("0.1.3", updater.ReadyVersion);
            lock (backend.Applied)
                Assert.Equal(["0.1.3"], backend.Applied);
            Assert.Equal(new UpdateStatus(UpdateStage.Restarting, "0.1.3"), updater.Status);
            Assert.DoesNotContain(_log.Lines, l => l.Contains("0.1.4", StringComparison.Ordinal));
        }
        finally
        {
            updater.Dispose();
        }
    }

    [Fact]
    public async Task Nothing_checks_reports_or_installs_after_dispose()
    {
        var backend = new FakeUpdateBackend { Offer = "0.1.3" };
        var updater = new Updater(backend, () => true, _log);
        Assert.Equal(UpdateStage.Ready, (await updater.CheckNowAsync()).Stage);
        var events = 0;
        updater.StatusChanged += (_, _) => Interlocked.Increment(ref events);
        updater.Start(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(10));
        updater.Dispose();
        updater.Dispose();
        var checks = backend.Checks;

        await updater.CheckNowAsync();
        Assert.False(await updater.ApplyAfterExitAsync(VelopackHooks.RestartArgs));
        await Task.Delay(150);
        Assert.Equal(checks, backend.Checks);
        Assert.Empty(backend.Applied);
        Assert.Equal(0, Volatile.Read(ref events));
    }

    // ===== Where updates come from =====

    [Theory]
    [InlineData("https://github.com.evil.example/va-plugin/clementine", null)]
    [InlineData("https://evil.example/github.com/va-plugin/clementine", null)]
    [InlineData("https://api.github.com/repos/va-plugin/clementine", null)]
    [InlineData("https://github.com/va-plugin/clementine#readme", null)]
    [InlineData("file://github.com/va-plugin/clementine", null)]
    [InlineData("https://github.com/va plugin/clementine", null)]
    [InlineData("https://github.com/va-plugin/clementine.git/", "https://github.com/va-plugin/clementine")]
    // Credentials in the configured URL are never carried into the update source.
    [InlineData("https://user:secret@github.com/va-plugin/clementine", "https://github.com/va-plugin/clementine")]
    public void Only_a_plain_github_repository_becomes_the_update_source(string value, string? expected) =>
        Assert.Equal(expected, UpdateSetup.NormalizeRepository(value));

    [Fact]
    public void The_update_source_and_its_links_point_at_the_configured_repository()
    {
        Assert.Equal(Repo, UpdateSetup.RepositoryUrl);
        Assert.Equal(Repo + "/releases/tag/v0.2.0-beta.1", UpdateSetup.ReleaseUrl("0.2.0-beta.1"));
        Assert.Equal(["--background"], VelopackHooks.RestartArgs);
    }

    // ===== The real Velopack GitHub source, offline =====

    sealed class NullVelopackLog : IVelopackLogger
    {
        public void Log(VelopackLogLevel logLevel, string? message, Exception? exception)
        {
        }
    }

    /// <summary>Stands in for GitHub: records every request and serves scripted JSON and package bytes.</summary>
    sealed class RecordingDownloader : IFileDownloader
    {
        public List<(string Url, IDictionary<string, string> Headers)> Requests { get; } = [];

        public Dictionary<string, byte[]> Responses { get; } = new(StringComparer.Ordinal);

        public Exception? Failure { get; set; }

        byte[] Serve(string url, IDictionary<string, string>? headers)
        {
            lock (Requests)
                Requests.Add((url, headers is null ? new Dictionary<string, string>() : new Dictionary<string, string>(headers)));
            if (Failure is { } failure)
                throw failure;
            foreach (var (prefix, body) in Responses)
            {
                if (url.StartsWith(prefix, StringComparison.Ordinal))
                    return body;
            }
            throw new HttpRequestException($"Response status code does not indicate success: 404 (Not Found) for {url}", null, HttpStatusCode.NotFound);
        }

        public Task DownloadFile(string url, string targetFile, Action<int> progress, IDictionary<string, string>? headers = null, double timeout = 30, CancellationToken cancelToken = default)
        {
            var body = Serve(url, headers);
            File.WriteAllBytes(targetFile, body);
            progress(100);
            return Task.CompletedTask;
        }

        public Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30) => Task.FromResult(Serve(url, headers));

        public Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30) =>
            Task.FromResult(Encoding.UTF8.GetString(Serve(url, headers)));
    }

    const string ReleasesApi = "https://api.github.com/repos/va-plugin/clementine/releases";

    static string ReleaseJson(string tag, bool prerelease, bool withFeed)
    {
        var assets = new List<Dictionary<string, string>>();
        if (withFeed)
            assets.Add(Asset(tag, "releases.win.json", "application/json"));
        assets.Add(Asset(tag, $"ClementineApp-{tag}-full.nupkg", "application/octet-stream"));
        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["name"] = $"Clementine {tag}",
            ["prerelease"] = prerelease,
            ["published_at"] = "2026-10-30T12:00:00Z",
            ["assets"] = assets,
        });
    }

    static Dictionary<string, string> Asset(string tag, string name, string contentType) => new()
    {
        ["name"] = name,
        ["url"] = $"https://api.github.com/repos/va-plugin/clementine/releases/assets/{Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(tag + name)))[..8]}",
        ["browser_download_url"] = $"https://github.com/va-plugin/clementine/releases/download/v{tag}/{name}",
        ["content_type"] = contentType,
    };

    static string FeedJson(string version, byte[] package) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["Assets"] = new[]
        {
            new Dictionary<string, object>
            {
                ["PackageId"] = "ClementineApp",
                ["Version"] = version,
                ["Type"] = "Full",
                ["FileName"] = $"ClementineApp-{version}-full.nupkg",
                ["SHA1"] = Convert.ToHexString(SHA1.HashData(package)),
                ["SHA256"] = Convert.ToHexString(SHA256.HashData(package)),
                ["Size"] = package.Length,
            },
        },
    });

    /// <summary>An UpdateManager set up exactly like <see cref="VelopackUpdateBackend.ForGitHub"/>, for an installed 0.1.1.</summary>
    static UpdateManager Manager(RecordingDownloader downloader, string packagesDir, string installed = "0.1.1") =>
        new(new GithubSource(Repo, accessToken: null, prerelease: false, downloader), new UpdateOptions { AllowVersionDowngrade = false },
            new TestVelopackLocator("ClementineApp", installed, packagesDir, new NullVelopackLog()));

    /// <summary>The same calls <see cref="VelopackUpdateBackend"/> makes, on a manager the test controls.</summary>
    sealed class ManagerBackend(UpdateManager manager) : IUpdateBackend
    {
        public bool IsInstalled => manager.IsInstalled;

        public async Task<UpdateOffer?> CheckAsync(CancellationToken cancellationToken)
        {
            var info = await manager.CheckForUpdatesAsync().WaitAsync(cancellationToken);
            if (info?.TargetFullRelease is not { } target || info.IsDowngrade)
                return null;
            return new UpdateOffer(target.Version.ToString(), info);
        }

        public Task DownloadAsync(UpdateOffer offer, Action<int> progress, CancellationToken cancellationToken) =>
            manager.DownloadUpdatesAsync((UpdateInfo)offer.Handle!, progress, cancellationToken);

        public void ApplyAfterExit(UpdateOffer offer, IReadOnlyList<string> restartArgs) => throw new NotSupportedException("never in tests");
    }

    static void Serve(RecordingDownloader downloader, string releasesJson) =>
        downloader.Responses[ReleasesApi] = Encoding.UTF8.GetBytes(releasesJson);

    [Fact]
    public async Task The_github_check_is_anonymous_and_goes_only_to_github()
    {
        using var workspace = new TestWorkspace("upd-privacy");
        var package = Encoding.UTF8.GetBytes("not really a nupkg");
        var downloader = new RecordingDownloader();
        Serve(downloader, "[" + ReleaseJson("0.1.3", prerelease: false, withFeed: true) + "]");
        downloader.Responses["https://github.com/va-plugin/clementine/releases/download/v0.1.3/releases.win.json"] = Encoding.UTF8.GetBytes(FeedJson("0.1.3", package));

        var info = await Manager(downloader, workspace.Path("packages")).CheckForUpdatesAsync();

        Assert.NotNull(info);
        Assert.Equal("0.1.3", info.TargetFullRelease.Version.ToString());
        Assert.NotEmpty(downloader.Requests);
        Assert.StartsWith(ReleasesApi + "?", downloader.Requests[0].Url, StringComparison.Ordinal);
        // Velopack keeps a random "staging" id on disk for staged rollouts; it must never travel to GitHub.
        var stagingIds = Directory.EnumerateFiles(workspace.Root, ".betaId", SearchOption.AllDirectories).Select(f => File.ReadAllText(f).Trim()).Where(s => s.Length > 0).ToList();
        foreach (var (url, headers) in downloader.Requests)
        {
            var uri = new Uri(url);
            Assert.Equal(Uri.UriSchemeHttps, uri.Scheme);
            Assert.Contains(uri.Host, new[] { "api.github.com", "github.com" });
            Assert.DoesNotContain("localVersion", url, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("stagingId", url, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotMatch(@"[?&](id|arch|os|rid)=", url);
            Assert.DoesNotContain(headers.Keys, k => k.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || k.Equals("PRIVATE-TOKEN", StringComparison.OrdinalIgnoreCase));
            foreach (var id in stagingIds)
            {
                Assert.DoesNotContain(id, url, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(headers.Values, v => v.Contains(id, StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    [Theory]
    [InlineData("0.1.1")] // the installed version again
    [InlineData("0.1.0")] // an older one (a downgrade)
    public async Task The_same_or_an_older_release_is_never_offered(string published)
    {
        using var workspace = new TestWorkspace("upd-same");
        var downloader = new RecordingDownloader();
        Serve(downloader, "[" + ReleaseJson(published, prerelease: false, withFeed: true) + "]");
        downloader.Responses[$"https://github.com/va-plugin/clementine/releases/download/v{published}/releases.win.json"] =
            Encoding.UTF8.GetBytes(FeedJson(published, [1, 2, 3]));

        var backend = new ManagerBackend(Manager(downloader, workspace.Path("packages")));
        Assert.Null(await backend.CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_prerelease_is_not_offered_to_stable_copies()
    {
        using var workspace = new TestWorkspace("upd-pre");
        var downloader = new RecordingDownloader();
        Serve(downloader, "[" + ReleaseJson("0.2.0-beta.1", prerelease: true, withFeed: true) + "]");
        downloader.Responses["https://github.com/va-plugin/clementine/releases/download/v0.2.0-beta.1/releases.win.json"] =
            Encoding.UTF8.GetBytes(FeedJson("0.2.0-beta.1", [1, 2, 3]));

        Assert.Null(await new ManagerBackend(Manager(downloader, workspace.Path("packages"))).CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_newest_release_without_update_files_doesnt_hide_an_older_newer_one_or_break_the_check()
    {
        using var workspace = new TestWorkspace("upd-nofeed");
        var downloader = new RecordingDownloader();
        Serve(downloader, "[" + ReleaseJson("0.1.4", prerelease: false, withFeed: false) + "," + ReleaseJson("0.1.3", prerelease: false, withFeed: true) + "]");
        downloader.Responses["https://github.com/va-plugin/clementine/releases/download/v0.1.3/releases.win.json"] =
            Encoding.UTF8.GetBytes(FeedJson("0.1.3", [1, 2, 3]));

        var offer = await new ManagerBackend(Manager(downloader, workspace.Path("packages"))).CheckAsync(CancellationToken.None);
        Assert.Equal("0.1.3", offer?.Version);
    }

    [Fact]
    public async Task No_releases_at_all_is_up_to_date()
    {
        using var workspace = new TestWorkspace("upd-none");
        var downloader = new RecordingDownloader();
        Serve(downloader, "[]");
        Assert.Null(await new ManagerBackend(Manager(downloader, workspace.Path("packages"))).CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Rate_limiting_reaches_the_updater_as_a_quiet_failure()
    {
        using var workspace = new TestWorkspace("upd-403");
        var downloader = new RecordingDownloader
        {
            Failure = new HttpRequestException("Response status code does not indicate success: 403 (rate limit exceeded).", null, HttpStatusCode.Forbidden),
        };
        var seen = new List<UpdateStatus>();
        using var updater = new Updater(new ManagerBackend(Manager(downloader, workspace.Path("packages"))), () => true, _log);
        updater.StatusChanged += (_, s) =>
        {
            lock (seen)
                seen.Add(s);
        };

        updater.Start(TimeSpan.Zero, TimeSpan.FromHours(1));
        await WaitFor(() => _log.Lines.Any(l => l.StartsWith("[WARN] Update check failed", StringComparison.Ordinal)));
        await WaitFor(() => updater.Status.Stage == UpdateStage.Idle);
        lock (seen)
            Assert.DoesNotContain(seen, s => s.Stage == UpdateStage.Failed);
        Assert.Equal(UpdateStatus.CheckFailedText, (await updater.CheckNowAsync()).Text);
    }

    [Fact]
    public async Task A_malformed_feed_never_offers_anything()
    {
        using var workspace = new TestWorkspace("upd-bad");
        var downloader = new RecordingDownloader();
        Serve(downloader, "[" + ReleaseJson("0.1.3", prerelease: false, withFeed: true) + "]");
        downloader.Responses["https://github.com/va-plugin/clementine/releases/download/v0.1.3/releases.win.json"] = Encoding.UTF8.GetBytes("<html>rate limited</html>");
        using var updater = new Updater(new ManagerBackend(Manager(downloader, workspace.Path("packages"))), () => true, _log);

        var status = await updater.CheckNowAsync();
        Assert.Contains(status.Stage, new[] { UpdateStage.UpToDate, UpdateStage.Failed });
        Assert.Null(updater.ReadyVersion);
    }

    [Fact]
    public async Task A_tampered_package_is_rejected_and_never_offered_for_a_restart()
    {
        using var workspace = new TestWorkspace("upd-tamper");
        var genuine = Encoding.UTF8.GetBytes("the package the release feed describes");
        var downloader = new RecordingDownloader();
        Serve(downloader, "[" + ReleaseJson("0.1.3", prerelease: false, withFeed: true) + "]");
        downloader.Responses["https://github.com/va-plugin/clementine/releases/download/v0.1.3/releases.win.json"] = Encoding.UTF8.GetBytes(FeedJson("0.1.3", genuine));
        downloader.Responses["https://github.com/va-plugin/clementine/releases/download/v0.1.3/ClementineApp-0.1.3-full.nupkg"] =
            Encoding.UTF8.GetBytes("a different package of the same lengthXX")[..genuine.Length];
        using var updater = new Updater(new ManagerBackend(Manager(downloader, workspace.Path("packages"))), () => true, _log);

        var status = await updater.CheckNowAsync();

        Assert.Equal(UpdateStage.Failed, status.Stage);
        Assert.Equal("Couldn't download 0.1.3 — try again later", status.Text);
        Assert.Null(updater.ReadyVersion);
        Assert.False(await updater.ApplyAfterExitAsync(VelopackHooks.RestartArgs));
    }

    // ===== What actually goes over the wire =====

    [Fact]
    public async Task Velopacks_http_requests_carry_no_identifying_headers()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            var buffer = new byte[16384];
            var received = new StringBuilder();
            while (!received.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0)
                    break;
                received.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }
            var reply = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n[]");
            await stream.WriteAsync(reply);
            return received.ToString();
        });

        // GithubSource asks for the release list with exactly these request headers (no token).
        var body = await new HttpClientFileDownloader().DownloadString($"http://127.0.0.1:{port}/repos/va-plugin/clementine/releases?per_page=10&page=1",
            new Dictionary<string, string> { ["Accept"] = "application/vnd.github.v3+json" }, 30);
        var request = await server.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("[]", body);
        var headers = request.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .Select(l => l.Split(':', 2)).ToDictionary(p => p[0].Trim(), p => p.Length > 1 ? p[1].Trim() : "", StringComparer.OrdinalIgnoreCase);
        Assert.Subset(new HashSet<string>(["Host", "Accept", "User-Agent", "Accept-Encoding", "Connection", "traceparent"], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(headers.Keys, StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain(headers, h => h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || h.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase));
        var everything = string.Join("\n", headers.Values);
        Assert.DoesNotContain(Environment.UserName, everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.MachineName, everything, StringComparison.OrdinalIgnoreCase);
    }
}
