using Clementine.App.Startup;
using Clementine.Core;
using Clementine.Core.Hosting;
using Clementine.TestSupport;

namespace Clementine.App.Tests;

public class CommandLineTests
{
    [Fact]
    public void No_arguments_open_the_main_window()
    {
        var command = CommandLine.Parse([], @"C:\Work");
        Assert.Equal(ShellCommandKind.OpenMainWindow, command.Kind);
        Assert.Empty(command.Paths);
    }

    [Fact]
    public void Background_wheel_open_and_quit_are_recognised()
    {
        Assert.Equal(ShellCommandKind.Background, CommandLine.Parse(["--background"]).Kind);
        Assert.Equal(ShellCommandKind.Quit, CommandLine.Parse(["--quit"]).Kind);
        Assert.Equal(ShellCommandKind.Settings, CommandLine.Parse(["--settings"]).Kind);

        var wheel = CommandLine.Parse(["--wheel", @"C:\Clips\a b.mov", @"D:\x.png"]);
        Assert.Equal(ShellCommandKind.Wheel, wheel.Kind);
        Assert.Equal([@"C:\Clips\a b.mov", @"D:\x.png"], wheel.Paths);
        Assert.Equal(WheelMode.Convert, wheel.Mode);

        var open = CommandLine.Parse(["--open", @"C:\a.pdf"]);
        Assert.Equal(ShellCommandKind.OpenFiles, open.Kind);
    }

    [Fact]
    public void Relative_paths_resolve_against_the_launching_directory()
    {
        var command = CommandLine.Parse(["--wheel", "clip.mp4", @"sub\photo.psd"], @"C:\Users\Me\Videos");
        Assert.Equal([@"C:\Users\Me\Videos\clip.mp4", @"C:\Users\Me\Videos\sub\photo.psd"], command.Paths);
    }

    [Fact]
    public void Bare_paths_mean_open()
    {
        var command = CommandLine.Parse([@"C:\a.mov"]);
        Assert.Equal(ShellCommandKind.OpenFiles, command.Kind);
    }

    [Fact]
    public void Snapshot_and_make_icon_take_a_directory_and_are_standalone()
    {
        var snapshot = CommandLine.Parse(["--snapshot-ui", @"C:\shots"]);
        Assert.Equal(ShellCommandKind.Snapshot, snapshot.Kind);
        Assert.Equal(@"C:\shots", snapshot.Directory);
        Assert.True(snapshot.IsStandalone);
        Assert.True(CommandLine.Parse(["--make-icon", @"C:\icons"]).IsStandalone);
        Assert.False(CommandLine.Parse(["--wheel", @"C:\a"]).IsStandalone);
    }

    [Fact]
    public void Wheel_mode_can_be_chosen()
    {
        Assert.Equal(WheelMode.Tools, CommandLine.Parse(["--wheel", "--mode", "tools", @"C:\a.mp4"]).Mode);
        Assert.Equal(WheelMode.Actions, CommandLine.Parse(["--wheel", "--actions", @"C:\a.mp4"]).Mode);
    }

    [Fact]
    public void A_command_wins_over_background()
    {
        Assert.Equal(ShellCommandKind.Wheel, CommandLine.Parse(["--background", "--wheel", @"C:\a.mp4"]).Kind);
    }

    [Theory]
    [InlineData("--wheel", @"C:\Clips\Festival – Ünïcødé 01.mov")]
    [InlineData("--open", @"D:\a b\c.psd")]
    [InlineData("--background", null)]
    [InlineData("--quit", null)]
    public void Format_round_trips(string verb, string? path)
    {
        string[] args = path is null ? [verb] : [verb, path];
        var parsed = CommandLine.Parse(args);
        Assert.Equal(parsed, CommandLine.Parse(CommandLine.Format(parsed).ToArray()), new CommandComparer());
    }

    sealed class CommandComparer : IEqualityComparer<ShellCommand>
    {
        public bool Equals(ShellCommand? x, ShellCommand? y) =>
            x is not null && y is not null && x.Kind == y.Kind && x.Mode == y.Mode && x.Directory == y.Directory && x.Paths.SequenceEqual(y.Paths);

        public int GetHashCode(ShellCommand obj) => obj.Kind.GetHashCode();
    }
}

public class InstanceChannelTests
{
    [Fact]
    public async Task Arguments_round_trip_over_the_pipe()
    {
        var pipe = $"Clementine.Test.{Guid.NewGuid():N}";
        using var server = new InstanceChannel.Server(pipe, new RecordingLog(), listeners: 2);
        var received = new TaskCompletionSource<InstanceMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.MessageReceived += (_, message) => received.TrySetResult(message);

        string[] args = ["--wheel", "clip – ünïcødé.mov", @"C:\Other Folder\photo.psd"];
        Assert.True(InstanceChannel.TrySend(pipe, new InstanceMessage(args, @"C:\Users\Me\Videos"), TimeSpan.FromSeconds(5)));

        var message = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(args, message.Args);
        Assert.Equal(@"C:\Users\Me\Videos", message.WorkingDirectory);
        var command = message.ToCommand();
        Assert.Equal(ShellCommandKind.Wheel, command.Kind);
        Assert.Equal([@"C:\Users\Me\Videos\clip – ünïcødé.mov", @"C:\Other Folder\photo.psd"], command.Paths);
    }

    [Fact]
    public async Task Many_launches_at_once_all_arrive()
    {
        var pipe = $"Clementine.Test.{Guid.NewGuid():N}";
        using var server = new InstanceChannel.Server(pipe, new RecordingLog());
        var paths = new System.Collections.Concurrent.ConcurrentBag<string>();
        var all = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.MessageReceived += (_, message) =>
        {
            paths.Add(message.Args[1]);
            if (paths.Count == 12)
                all.TrySetResult();
        };

        // Explorer starts one process per selected file for a context-menu verb.
        var sends = Enumerable.Range(1, 12).Select(i => Task.Run(() =>
            InstanceChannel.TrySend(pipe, new InstanceMessage(["--wheel", $@"C:\clip{i}.mp4"], @"C:\"), TimeSpan.FromSeconds(10))));
        Assert.All(await Task.WhenAll(sends), Assert.True);
        await all.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(12, paths.Distinct().Count());
    }

    [Fact]
    public void Sending_without_a_listener_fails_quickly()
    {
        var started = DateTime.UtcNow;
        Assert.False(InstanceChannel.TrySend($"Clementine.Test.{Guid.NewGuid():N}", new InstanceMessage(["--quit"], @"C:\"), TimeSpan.FromMilliseconds(300)));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }
}

public class SingleInstanceTests
{
    [Fact]
    public async Task Second_acquire_while_the_first_owner_lives_is_not_primary()
    {
        var name = $"Clementine.Test.Mutex.{Guid.NewGuid():N}";
        using var acquired = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var firstWasPrimary = false;

        // Mutex ownership is per thread, so the "first instance" is a thread that stays alive, like the app's UI thread.
        var owner = new Thread(() =>
        {
            using var first = SingleInstance.Acquire(name);
            firstWasPrimary = first.IsPrimary;
            acquired.Set();
            release.Wait(TimeSpan.FromSeconds(30));
        });
        owner.Start();
        acquired.Wait(TimeSpan.FromSeconds(10));
        try
        {
            Assert.True(firstWasPrimary);
            var secondWasPrimary = await RunOnThread(() =>
            {
                using var second = SingleInstance.Acquire(name);
                return second.IsPrimary;
            });
            Assert.False(secondWasPrimary);
        }
        finally
        {
            release.Set();
            owner.Join(TimeSpan.FromSeconds(10));
        }

        // Once the first instance has gone, the next launch becomes primary.
        Assert.True(await RunOnThread(() =>
        {
            using var next = SingleInstance.Acquire(name);
            return next.IsPrimary;
        }));
    }

    [Fact]
    public void Default_names_include_the_user_sid()
    {
        Assert.StartsWith("Clementine.SingleInstance.S-1-", SingleInstance.DefaultMutexName);
        Assert.StartsWith("Clementine.Pipe.S-1-", SingleInstance.DefaultPipeName);
    }

    static Task<T> RunOnThread<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>();
        var thread = new Thread(() =>
        {
            try
            {
                tcs.SetResult(func());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        thread.Start();
        return tcs.Task;
    }
}

public class WheelRequestAggregatorTests
{
    static readonly DateTimeOffset T0 = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Requests_within_the_quiet_period_become_one_wheel()
    {
        var aggregator = new WheelRequestAggregator(TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(3));
        aggregator.Add([@"C:\a.mp4"], WheelMode.Convert, T0);
        aggregator.Add([@"C:\b.mp4"], WheelMode.Convert, T0.AddMilliseconds(150));
        aggregator.Add([@"C:\c.mp4", @"C:\A.MP4"], WheelMode.Convert, T0.AddMilliseconds(300));

        Assert.Null(aggregator.TryFlush(T0.AddMilliseconds(600)));
        Assert.Equal(TimeSpan.FromMilliseconds(100), aggregator.DueIn(T0.AddMilliseconds(600)));

        var batch = aggregator.TryFlush(T0.AddMilliseconds(700));
        Assert.NotNull(batch);
        Assert.Equal([@"C:\a.mp4", @"C:\b.mp4", @"C:\c.mp4"], batch.Value.Paths);
        Assert.False(aggregator.HasPending);
    }

    [Fact]
    public void A_long_stream_of_requests_is_flushed_after_the_max_delay()
    {
        var aggregator = new WheelRequestAggregator(TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(1));
        for (var i = 0; i < 10; i++)
            aggregator.Add([$@"C:\{i}.mp4"], WheelMode.Convert, T0.AddMilliseconds(i * 200));
        // Last request at 1.8 s, but the batch was due 1 s after the first.
        Assert.Equal(TimeSpan.Zero, aggregator.DueIn(T0.AddMilliseconds(1800)));
        Assert.Equal(10, aggregator.TryFlush(T0.AddMilliseconds(1800))!.Value.Paths.Count);
    }

    [Fact]
    public void Separate_bursts_make_separate_wheels_and_the_first_request_sets_the_mode()
    {
        var aggregator = new WheelRequestAggregator(TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(3));
        aggregator.Add([@"C:\a.mp4"], WheelMode.Tools, T0);
        aggregator.Add([@"C:\b.mp4"], WheelMode.Convert, T0.AddMilliseconds(100));
        var first = aggregator.TryFlush(T0.AddSeconds(1));
        Assert.Equal(WheelMode.Tools, first!.Value.Mode);
        Assert.Equal(2, first.Value.Paths.Count);

        Assert.Null(aggregator.DueIn(T0.AddSeconds(2)));
        aggregator.Add([@"C:\c.mp4"], WheelMode.Convert, T0.AddSeconds(5));
        Assert.Equal([@"C:\c.mp4"], aggregator.TryFlush(T0.AddSeconds(6))!.Value.Paths);
    }
}

public class GuardedProviderTests
{
    [Fact]
    public void A_broken_engine_or_a_duplicate_id_does_not_stop_the_catalog()
    {
        using var ws = new TestWorkspace("guard");
        var log = new RecordingLog();
        var good = new TestOps.Provider(TestOps.Op("video.one"), TestOps.Op("video.two"));
        var duplicate = new TestOps.Provider(TestOps.Op("video.one"), TestOps.Op("image.three"));
        var guarded = new GuardedProvider([good, new TestOps.ThrowingProvider(), duplicate], log);

        var catalog = new OperationCatalog(ws.Context, [guarded]);

        Assert.Equal(["video.one", "video.two", "image.three"], catalog.All.Select(o => o.Info.Id));
        Assert.Equal(2, guarded.Problems.Count);
        Assert.Contains(log.Lines, l => l.Contains("Broken engine failed", StringComparison.Ordinal));
        Assert.Contains(log.Lines, l => l.Contains("Duplicate operation id 'video.one'", StringComparison.Ordinal));
    }

    [Fact]
    public void Extra_providers_are_deduplicated_against_the_catalog()
    {
        using var ws = new TestWorkspace("guard");
        var catalog = new OperationCatalog(ws.Context, [new TestOps.Provider(TestOps.Op("action.copy-path"))]);
        catalog.Add(GuardedProvider.Deduplicated(new TestOps.Provider(TestOps.Op("action.copy-path"), TestOps.Op("action.other")), catalog, ws.Log));
        Assert.Equal(2, catalog.All.Count);
    }
}
