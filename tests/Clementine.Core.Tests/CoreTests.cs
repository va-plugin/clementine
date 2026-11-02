using Clementine.Core;
using Clementine.TestSupport;

namespace Clementine.Core.Tests;

public class FileTypesTests
{
    [Theory]
    [InlineData("C:\\a\\Clip.MP4", ".mp4", FileKind.Video)]
    [InlineData("C:\\a\\layout.psd", ".psd", FileKind.Image)]
    [InlineData("C:\\a\\backup.tar.gz", ".tar.gz", FileKind.Archive)]
    [InlineData("C:\\a\\song.flac", ".flac", FileKind.Audio)]
    [InlineData("C:\\a\\notes", "", FileKind.Unknown)]
    public void Recognises_extensions(string path, string extension, FileKind kind)
    {
        Assert.Equal(extension, FileTypes.ExtensionOf(path));
        Assert.Equal(kind, FileTypes.KindOf(extension));
    }

    [Fact]
    public void Input_file_splits_compound_extension()
    {
        using var ws = new TestWorkspace("types");
        var file = InputFile.FromPath(ws.WriteText("site backup.tar.gz", "x"));
        Assert.Equal("site backup", file.BaseName);
        Assert.Equal(".tar.gz", file.Extension);
        Assert.Equal(FileKind.Archive, file.Kind);
    }
}

public class OutputPlannerTests
{
    [Fact]
    public void Never_overwrites_and_commits_atomically()
    {
        using var ws = new TestWorkspace("planner");
        var input = InputFile.FromPath(ws.WriteText("clip.mp4", "original"));
        ws.WriteText("clip.mov", "existing");

        using var first = ws.Output.BeginFile(input, ".mov");
        using var second = ws.Output.BeginFile(input, ".mov");
        Assert.Equal(ws.Path("clip (2).mov"), first.FinalPath);
        Assert.Equal(ws.Path("clip (3).mov"), second.FinalPath);
        Assert.EndsWith(".mov", first.WorkingPath);

        File.WriteAllText(first.WorkingPath, "converted");
        Assert.Equal(ws.Path("clip (2).mov"), first.Commit());
        Assert.False(File.Exists(first.WorkingPath));
        Assert.Equal("existing", File.ReadAllText(ws.Path("clip.mov")));
        Assert.Equal("original", File.ReadAllText(input.Path));
    }

    [Fact]
    public void Uncommitted_output_is_cleaned_up()
    {
        using var ws = new TestWorkspace("planner");
        var input = InputFile.FromPath(ws.WriteText("photo.png", "x"));
        string working;
        using (var pending = ws.Output.BeginFile(input, ".jpg", suffix: "small"))
        {
            Assert.Equal(ws.Path("photo (small).jpg"), pending.FinalPath);
            working = pending.WorkingPath;
            File.WriteAllText(working, "partial");
        }
        Assert.False(File.Exists(working));
        Assert.False(File.Exists(ws.Path("photo (small).jpg")));
    }

    [Fact]
    public void Same_extension_output_never_targets_the_input()
    {
        using var ws = new TestWorkspace("planner");
        var input = InputFile.FromPath(ws.WriteText("clip.mp4", "x"));
        using var pending = ws.Output.BeginFile(input, ".mp4");
        Assert.NotEqual(input.Path, pending.FinalPath, StringComparer.OrdinalIgnoreCase);
    }
}

public class OptionValuesTests
{
    [Theory]
    [InlineData("1:02:03.5", 3723.5)]
    [InlineData("02:03", 123)]
    [InlineData("7.25", 7.25)]
    [InlineData("0,5", 0.5)]
    public void Parses_times(string text, double seconds)
    {
        Assert.True(OptionValues.TryParseTime(text, out var time));
        Assert.Equal(seconds, time.TotalSeconds, 3);
    }

    [Fact]
    public void Getters_accept_strings_from_the_cli()
    {
        var values = new OptionValues { ["quality"] = "82", ["keep"] = "yes", ["start"] = "00:01.500" };
        Assert.Equal(82, values.GetInt("quality", 0));
        Assert.True(values.GetBool("keep", false));
        Assert.Equal(1.5, values.GetTime("start", TimeSpan.Zero).TotalSeconds, 3);
        Assert.Equal("fallback", values.GetString("missing", "fallback"));
    }

    [Fact]
    public void Remembered_values_round_trip()
    {
        var values = new OptionValues { ["n"] = 2.5, ["b"] = true, ["t"] = TimeSpan.FromSeconds(90), ["s"] = "x" };
        var restored = OptionValues.FromInvariantStrings(values.ToInvariantStrings());
        Assert.Equal(2.5, restored.GetDouble("n", 0));
        Assert.True(restored.GetBool("b", false));
        Assert.Equal(90, restored.GetTime("t", TimeSpan.Zero).TotalSeconds, 3);
        Assert.Equal("x", restored.GetString("s", ""));
    }
}

public class JobQueueTests
{
    [Fact]
    public async Task Runs_per_file_jobs_and_reports_results()
    {
        using var ws = new TestWorkspace("queue");
        var a = InputFile.FromPath(ws.WriteText("a.txt", "a"));
        var b = InputFile.FromPath(ws.WriteText("b.txt", "b"));
        var op = new DelegateOperation(
            new OperationInfo { Id = "test.upper", Category = OperationCategory.Convert, Group = "UPPER", Title = "Upper" },
            f => f.Extension == ".txt",
            async (request, progress, ct) =>
            {
                progress.Report(new JobProgress(0.5, "halfway"));
                using var output = request.Output.BeginFile(request.Input, ".txt", "upper");
                await File.WriteAllTextAsync(output.WorkingPath, (await File.ReadAllTextAsync(request.Input.Path, ct)).ToUpperInvariant(), ct);
                return OperationResult.FromOutputs(output.Commit());
            });

        var queue = new JobQueue(ws.Context, ws.Output);
        var jobs = queue.Enqueue(op, [a, b]);
        await Task.WhenAll(jobs.Select(j => j.Completion));

        Assert.All(jobs, j => Assert.Equal(JobState.Succeeded, j.State));
        Assert.Equal("A", File.ReadAllText(ws.Path("a (upper).txt")));
        Assert.Equal("B", File.ReadAllText(ws.Path("b (upper).txt")));
    }

    [Fact]
    public async Task Failures_and_cancellation_are_reported_not_thrown()
    {
        using var ws = new TestWorkspace("queue");
        var file = InputFile.FromPath(ws.WriteText("a.txt", "a"));
        var failing = new DelegateOperation(
            new OperationInfo { Id = "test.fail", Category = OperationCategory.Tool, Group = "Fail", Title = "Fail" },
            _ => true,
            (_, _, _) => throw new OperationException("Nope", "details"));
        var slow = new DelegateOperation(
            new OperationInfo { Id = "test.slow", Category = OperationCategory.Tool, Group = "Slow", Title = "Slow" },
            _ => true,
            async (_, _, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return new OperationResult();
            });

        var queue = new JobQueue(ws.Context, ws.Output);
        var failed = queue.Enqueue(failing, [file]).Single();
        var canceled = queue.Enqueue(slow, [file]).Single();
        canceled.Cancel();
        await Task.WhenAll(failed.Completion, canceled.Completion).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(JobState.Failed, failed.State);
        Assert.Equal("Nope", failed.ErrorMessage);
        Assert.Equal("details", failed.ErrorDetails);
        Assert.Equal(JobState.Canceled, canceled.State);
    }
}

public class ProcessRunnerTests
{
    [Fact]
    public async Task Captures_output_and_exit_code()
    {
        var result = await ProcessRunner.RunAsync("cmd.exe", ["/c", "echo héllo & echo oops 1>&2 & exit 3"],
            new ProcessRunOptions { OutputEncoding = System.Text.Encoding.Default });
        Assert.Equal(3, result.ExitCode);
        Assert.Contains("oops", result.StdErrTail);
    }

    [Fact]
    public async Task Cancellation_kills_the_process()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var started = DateTime.UtcNow;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ProcessRunner.RunAsync("powershell.exe", ["-NoProfile", "-Command", "Start-Sleep -Seconds 20"], cancellationToken: cts.Token));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }
}
