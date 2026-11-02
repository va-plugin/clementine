using Clementine.Core;
using Clementine.TestSupport;

namespace Clementine.Core.Tests;

public class SettingsTests
{
    [Fact]
    public void Remembered_options_stay_case_insensitive_after_updates_and_reloads()
    {
        using var ws = new TestWorkspace("settings");
        var path = ws.Path("settings.json");
        var store = new SettingsStore(path);
        store.Update(s => s.RememberedOptions["video.trim"] = new() { ["Start"] = "1.5" });
        store.Update(s => s.PlaySounds = false);

        Assert.True(store.Current.RememberedOptions.TryGetValue("VIDEO.TRIM", out var values));
        Assert.Equal("1.5", values!["start"]);

        var reloaded = new SettingsStore(path);
        Assert.True(reloaded.Current.RememberedOptions.ContainsKey("Video.Trim"));
        Assert.False(reloaded.Current.PlaySounds);
    }
}

public class CatalogTests
{
    sealed class Provider(string name, params string[] ids) : IOperationProvider
    {
        public string Name => name;

        public IEnumerable<IOperation> CreateOperations(EngineContext context) => ids.Select(id => new DelegateOperation(
            new OperationInfo { Id = id, Category = OperationCategory.Convert, Group = "G", Title = id },
            _ => true,
            (_, _, _) => Task.FromResult(new OperationResult())));
    }

    sealed class Broken : IOperationProvider
    {
        public string Name => "Broken";

        public IEnumerable<IOperation> CreateOperations(EngineContext context) => throw new InvalidOperationException("boom");
    }

    [Fact]
    public void Duplicate_ids_and_broken_providers_are_skipped_not_fatal()
    {
        using var ws = new TestWorkspace("catalog");
        var catalog = new OperationCatalog(ws.Context, [new Provider("A", "x.one", "x.two"), new Broken(), new Provider("B", "x.two", "x.three")]);
        Assert.Equal(["x.one", "x.two", "x.three"], catalog.All.Select(o => o.Info.Id));
        Assert.Contains(ws.Log.Lines, l => l.Contains("Duplicate operation id 'x.two'"));
        Assert.Contains(ws.Log.Lines, l => l.Contains("Broken"));
    }
}

public class OutputCaseTests
{
    [Fact]
    public void Extension_case_is_kept()
    {
        using var ws = new TestWorkspace("case");
        var input = InputFile.FromPath(ws.WriteText("Photo.JPG", "x"));
        using var pending = ws.Output.BeginFile(input, ".JPG", "copy");
        Assert.EndsWith("Photo (copy).JPG", pending.FinalPath);
    }
}

public class ConcurrencyGateTests
{
    [Fact]
    public async Task Limit_changes_apply_to_waiting_jobs()
    {
        var limit = 1;
        var gate = new ConcurrencyGate(() => limit);
        await gate.WaitAsync(CancellationToken.None);
        var second = gate.WaitAsync(CancellationToken.None);
        await Task.Delay(50);
        Assert.False(second.IsCompleted);

        limit = 2;
        gate.Pump();
        await second.WaitAsync(TimeSpan.FromSeconds(5));

        var third = gate.WaitAsync(CancellationToken.None);
        gate.Release();
        await third.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Cancelled_waiters_leave_the_queue()
    {
        var gate = new ConcurrencyGate(() => 1);
        await gate.WaitAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var waiting = gate.WaitAsync(cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        var next = gate.WaitAsync(CancellationToken.None);
        gate.Release();
        await next.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
