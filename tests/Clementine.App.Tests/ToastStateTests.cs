using Clementine.App.Toasts;
using Clementine.Core;
using Clementine.TestSupport;

namespace Clementine.App.Tests;

public class ToastStateTests
{
    static async Task<IReadOnlyList<Job>> RunAsync(TestWorkspace ws, IOperation op, params string[] names)
    {
        var queue = new JobQueue(ws.Context, ws.Output, maxHeavyJobs: 2);
        var jobs = queue.Enqueue(op, names.Select(n => InputFile.FromPath(ws.WriteText(n, "x"))).ToList());
        await Task.WhenAll(jobs.Select(j => j.Completion)).WaitAsync(TimeSpan.FromSeconds(10));
        return jobs;
    }

    [Fact]
    public async Task Single_conversion_success_names_the_output()
    {
        using var ws = new TestWorkspace("toast");
        var op = TestOps.Op("video.prores", r => Task.FromResult(OperationResult.FromOutputs(r.Input.Path + ".mov")), title: "ProRes 4444 · alpha · with audio");
        var state = ToastState.FromJobs(await RunAsync(ws, op, "clip.mp4"));
        Assert.Equal(ToastKind.Succeeded, state.Kind);
        Assert.Equal("Converted to ProRes 4444 · alpha · with audio", state.Title);
        Assert.Equal("clip.mp4.mov", state.Subtitle);
        Assert.Single(state.Outputs);
        Assert.True(state.IsFinished);
    }

    [Fact]
    public async Task Failure_carries_the_friendly_message_and_details()
    {
        using var ws = new TestWorkspace("toast");
        var op = TestOps.Op("video.mp3", _ => throw new OperationException("This video has no audio track.", "stderr tail"), title: "Audio · MP3");
        var state = ToastState.FromJobs(await RunAsync(ws, op, "clip.mp4"));
        Assert.Equal(ToastKind.Failed, state.Kind);
        Assert.Equal("Couldn't convert to Audio · MP3", state.Title);
        Assert.Equal("This video has no audio track.", state.ErrorMessage);
        Assert.Contains("stderr tail", state.ErrorDetails, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Batches_collapse_into_one_card_with_partial_failures()
    {
        using var ws = new TestWorkspace("toast");
        var op = TestOps.Op("video.mp4", r => r.Input.Name == "c.mp4"
            ? throw new OperationException("Damaged file.")
            : Task.FromResult(OperationResult.FromOutputs(r.Input.Path + ".out")), title: "MP4 · H.264");
        var state = ToastState.FromJobs(await RunAsync(ws, op, "a.mp4", "b.mp4", "c.mp4", "d.mp4"));
        Assert.Equal(ToastKind.PartlyFailed, state.Kind);
        Assert.Equal("Converted 3 of 4 · 1 failed", state.Title);
        Assert.Equal(3, state.Outputs.Count);
        Assert.Equal(4, state.Total);
        Assert.Contains("c.mp4: Damaged file.", state.ErrorDetails, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Running_batches_report_how_many_are_done()
    {
        using var ws = new TestWorkspace("toast");
        var release = new TaskCompletionSource<OperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var op = TestOps.Op("video.mp4", r => r.Input.Name == "a.mp4" ? Task.FromResult(new OperationResult()) : release.Task, title: "MP4 · H.264");
        var queue = new JobQueue(ws.Context, ws.Output);
        var jobs = queue.Enqueue(op, new[] { "a.mp4", "b.mp4", "c.mp4" }.Select(n => InputFile.FromPath(ws.WriteText(n, "x"))).ToList());
        await jobs[0].Completion.WaitAsync(TimeSpan.FromSeconds(10));

        var state = ToastState.FromJobs(jobs);
        Assert.Equal(ToastKind.Running, state.Kind);
        Assert.Equal("Converting 3 files…", state.Title);
        Assert.Equal("1 of 3 done", state.Status);
        Assert.InRange(state.Progress ?? -1, 0.3, 0.4);

        release.SetResult(new OperationResult());
        await Task.WhenAll(jobs.Select(j => j.Completion)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ToastKind.Succeeded, ToastState.FromJobs(jobs).Kind);
    }

    [Fact]
    public async Task Text_results_are_combined_and_previewed()
    {
        using var ws = new TestWorkspace("toast");
        var op = TestOps.Op("files.checksum", r => Task.FromResult(OperationResult.FromText("SHA-256", $"hash-of-{r.Input.Name}")),
            category: OperationCategory.Action, title: "SHA-256 checksum");
        var state = ToastState.FromJobs(await RunAsync(ws, op, "a.bin", "b.bin"));
        Assert.Equal("SHA-256 checksum · 2 files", state.Title);
        Assert.Contains("== a.bin ==", state.Text, StringComparison.Ordinal);
        Assert.Contains("hash-of-b.bin", state.Text, StringComparison.Ordinal);
        Assert.Equal("SHA-256", state.TextTitle);
        Assert.EndsWith("…", state.TextPreview(2), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelled_jobs_show_as_cancelled()
    {
        using var ws = new TestWorkspace("toast");
        var queue = new JobQueue(ws.Context, ws.Output);
        var cancellable = new DelegateOperation(
            new OperationInfo { Id = "video.cancel", Category = OperationCategory.Tool, Group = "Trim", Title = "Trim" },
            _ => true,
            async (_, _, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new OperationResult();
            });
        var jobs = queue.Enqueue(cancellable, [InputFile.FromPath(ws.WriteText("clip.mp4", "x"))]);
        jobs[0].Cancel();
        await jobs[0].Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var state = ToastState.FromJobs(jobs);
        Assert.Equal(ToastKind.Canceled, state.Kind);
        Assert.Equal("Canceled", state.Title);
    }
}
