using Clementine.App.Launching;
using Clementine.Core;
using Clementine.Core.Hosting;
using Clementine.TestSupport;

namespace Clementine.App.Tests;

public class UiLauncherTests
{
    static readonly OptionSchema TrimSchema = new("Trim",
    [
        new TimeField("start", "Start", TimeSpan.Zero),
        new TimeField("end", "End", TimeSpan.FromSeconds(10)),
        new NumberField("quality", "Quality", 70, 10, 100),
        new TextField("password", "Password", "") { IsPassword = true },
    ]);

    static (HostServices Services, TestWorkspace Workspace) Create(params IOperation[] operations)
    {
        var ws = new TestWorkspace("launch");
        var services = HostServices.Create([new TestOps.Provider(operations)], ws.Settings, ws.Log);
        return (services, ws);
    }

    static IOperation TrimOp(OptionValues? dynamicDefaults = null) => new TestOps.WithDynamicDefaults(
        new OperationInfo { Id = "video.trim", Category = OperationCategory.Tool, Group = "Trim", Title = "Trim" },
        TrimSchema,
        dynamicDefaults ?? new OptionValues { ["end"] = TimeSpan.FromSeconds(42) });

    [Fact]
    public async Task Dialog_is_prefilled_then_jobs_are_queued_and_values_remembered()
    {
        var op = TrimOp();
        var (services, ws) = Create(op);
        using var _ = ws;
        services.Settings.Update(s => s.RememberedOptions["video.trim"] = new Dictionary<string, string> { ["start"] = "1.5", ["quality"] = "80", ["end"] = "3" });
        var input = InputFile.FromPath(ws.WriteText("clip.mp4", "x"));

        OptionValues? shown = null;
        ScreenPoint? anchorSeen = null;
        var launcher = new UiLauncher(services, toasts: null, prompt: (_, _, initial, anchor) =>
        {
            shown = new OptionValues(initial);
            anchorSeen = anchor;
            var chosen = new OptionValues(initial) { ["quality"] = 95.0, ["password"] = "secret" };
            return Task.FromResult<OptionValues?>(chosen);
        });

        var jobs = await launcher.LaunchAsync(op, [input], LaunchSource.Wheel, new ScreenPoint(100, 200));

        Assert.NotNull(shown);
        Assert.Equal(TimeSpan.FromSeconds(1.5), shown["start"]); // remembered
        Assert.Equal(TimeSpan.FromSeconds(42), shown["end"]); // dynamic default wins over the remembered 3 s
        Assert.Equal(80.0, shown["quality"]);
        Assert.Equal(new ScreenPoint(100, 200), anchorSeen);

        var job = Assert.Single(jobs);
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(JobState.Succeeded, job.State);
        Assert.Equal(95.0, job.Options.GetDouble("quality", 0));
        Assert.Equal("secret", job.Options.GetString("password", ""));

        var remembered = services.Settings.Current.RememberedOptions["video.trim"];
        Assert.Equal("95", remembered["quality"]);
        Assert.Equal("42", remembered["end"]);
        Assert.False(remembered.ContainsKey("password"));
    }

    [Fact]
    public async Task Cancelling_the_dialog_queues_nothing_and_remembers_nothing()
    {
        var op = TrimOp();
        var (services, ws) = Create(op);
        using var _ = ws;
        var launcher = new UiLauncher(services, null, (_, _, _, _) => Task.FromResult<OptionValues?>(null));
        var jobs = await launcher.LaunchAsync(op, [InputFile.FromPath(ws.WriteText("clip.mp4", "x"))], LaunchSource.MainWindow);
        Assert.Empty(jobs);
        Assert.Empty(services.Jobs.Snapshot());
        Assert.False(services.Settings.Current.RememberedOptions.ContainsKey("video.trim"));
    }

    [Fact]
    public async Task Operations_without_options_run_immediately_one_job_per_file()
    {
        var ran = 0;
        var op = TestOps.Op("image.png", _ =>
        {
            Interlocked.Increment(ref ran);
            return Task.FromResult(new OperationResult());
        });
        var (services, ws) = Create(op);
        using var _ = ws;
        var prompted = false;
        var launcher = new UiLauncher(services, null, (_, _, _, _) =>
        {
            prompted = true;
            return Task.FromResult<OptionValues?>(null);
        });
        var inputs = new[] { ws.WriteText("a.psd", "x"), ws.WriteText("b.psd", "x") }.Select(InputFile.FromPath).ToList();
        var jobs = await launcher.LaunchAsync(op, inputs, LaunchSource.Wheel);
        await Task.WhenAll(jobs.Select(j => j.Completion)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(prompted);
        Assert.Equal(2, jobs.Count);
        Assert.Equal(2, ran);
    }

    [Fact]
    public async Task Unavailable_operations_are_not_queued()
    {
        var op = TestOps.Op("doc.pdf", availability: () => Availability.Unavailable("Requires LibreOffice"));
        var (services, ws) = Create(op);
        using var _ = ws;
        var launcher = new UiLauncher(services, null, (_, _, _, _) => throw new InvalidOperationException("must not prompt"));
        var jobs = await launcher.LaunchAsync(op, [InputFile.FromPath(ws.WriteText("a.docx", "x"))], LaunchSource.Wheel);
        Assert.Empty(jobs);
    }

    [Fact]
    public async Task A_failing_dynamic_default_falls_back_to_static_defaults()
    {
        var op = new ThrowingDefaults();
        var (services, ws) = Create(op);
        using var _ = ws;
        var launcher = new UiLauncher(services, null);
        var values = await launcher.PrefillAsync(op, op.Options!, [InputFile.FromPath(ws.WriteText("a.mp4", "x"))]);
        Assert.Equal(TimeSpan.FromSeconds(10), values["end"]);
        Assert.Contains(ws.Log.Lines, l => l.Contains("dynamic defaults failed", StringComparison.Ordinal));
    }

    sealed class ThrowingDefaults() : OperationBase(new OperationInfo { Id = "video.broken", Category = OperationCategory.Tool, Group = "Broken", Title = "Broken" })
    {
        public override OptionSchema? Options => TrimSchema;

        public override bool Accepts(InputFile file) => true;

        public override Task<OptionValues> GetDynamicDefaultsAsync(IReadOnlyList<InputFile> inputs, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("ffprobe missing");

        public override Task<OperationResult> RunAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken) =>
            Task.FromResult(new OperationResult());
    }
}
