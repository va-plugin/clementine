using System.Globalization;
using System.IO;
using System.Text;
using Clementine.Core;

namespace Clementine.App.Toasts;

public enum ToastKind
{
    Running,
    Succeeded,
    PartlyFailed,
    Failed,
    Canceled,

    /// <summary>A message that isn't about a job (first-run hint, "can't run this").</summary>
    Info,
}

/// <summary>
/// Everything a toast card shows, derived from one job or a batch of jobs from the same drop.
/// Pure data so it can be unit-tested and rendered in snapshots without running anything.
/// </summary>
public sealed record ToastState
{
    public required ToastKind Kind { get; init; }

    public required string Title { get; init; }

    public string? Subtitle { get; init; }

    /// <summary>Second line under the progress bar ("42% · Encoding…", "4 of 12 done").</summary>
    public string? Status { get; init; }

    /// <summary>0..1, or null for an indeterminate bar. Only meaningful while running.</summary>
    public double? Progress { get; init; }

    public IReadOnlyList<string> Outputs { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public string? Text { get; init; }

    public string? TextTitle { get; init; }

    public string? ErrorMessage { get; init; }

    public string? ErrorDetails { get; init; }

    /// <summary>File whose shell icon represents the card (first output when done, else first input).</summary>
    public string? IconPath { get; init; }

    public string? IconKey { get; init; }

    /// <summary>Optional extra button for <see cref="ToastKind.Info"/> cards.</summary>
    public string? ActionLabel { get; init; }

    /// <summary>Glyph for an <see cref="ToastKind.Info"/> card instead of the default "i".</summary>
    public string? Glyph { get; init; }

    public int Total { get; init; } = 1;

    public int Completed { get; init; }

    public int FailedCount { get; init; }

    public bool IsFinished => Kind != ToastKind.Running;

    public bool HasText => !string.IsNullOrEmpty(Text);

    /// <summary>First few lines of <see cref="Text"/> for the card.</summary>
    public string? TextPreview(int lines = 4)
    {
        if (Text is null)
            return null;
        var all = Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var shown = all.Take(lines).Select(l => l.Length > 120 ? l[..117] + "…" : l);
        return string.Join("\n", shown) + (all.Length > lines ? "\n…" : "");
    }

    public static ToastState Info(string title, string? message = null, string? actionLabel = null) => new()
    {
        Kind = ToastKind.Info,
        Title = title,
        Subtitle = message,
        ActionLabel = actionLabel,
        IconKey = null,
    };

    /// <summary>"Clementine 0.1.3 is ready" with a Restart to update button (the updater downloaded it quietly).</summary>
    public static ToastState UpdateReady(string version) =>
        Info($"Clementine {version} is ready", "Downloaded in the background. Restarting takes a few seconds.", "Restart to update")
            with { Glyph = Theme.Glyphs.Download };

    /// <summary>The first card after Velopack restarted Clementine into a new version.</summary>
    public static ToastState Updated(string version) =>
        Info($"Updated to Clementine {version}", "Your settings and shortcuts are as you left them.", "What's new")
            with { Glyph = Theme.Glyphs.Check };

    /// <summary>Derives the card for <paramref name="jobs"/> (one job, or a batch from the same launch).</summary>
    public static ToastState FromJobs(IReadOnlyList<Job> jobs)
    {
        if (jobs.Count == 0)
            throw new ArgumentException("A toast needs at least one job.", nameof(jobs));
        var operation = jobs[0].Operation.Info;
        var isConvert = operation.Category == OperationCategory.Convert;
        var total = jobs.Count;
        var succeeded = jobs.Where(j => j.State == JobState.Succeeded).ToList();
        var failed = jobs.Where(j => j.State == JobState.Failed).ToList();
        var canceled = jobs.Count(j => j.State == JobState.Canceled);
        var finished = jobs.Count(j => j.IsFinished);
        var outputs = succeeded.SelectMany(j => j.Result?.Outputs ?? []).ToList();
        var warnings = succeeded.SelectMany(j => j.Result?.Warnings ?? []).Distinct(StringComparer.CurrentCulture).ToList();
        var text = CombineText(succeeded);
        var firstInput = jobs[0].Inputs[0];
        var subject = total == 1 ? jobs[0].DisplayName : $"{total} files";

        if (finished < total)
        {
            var progress = Average(jobs);
            string? status;
            if (total == 1)
            {
                var job = jobs[0];
                status = job.State == JobState.Queued
                    ? "Waiting for other jobs…"
                    : job.Progress is { } p
                        ? string.IsNullOrWhiteSpace(job.Status) ? Percent(p) : $"{Percent(p)} · {job.Status}"
                        : job.Status ?? "Working…";
            }
            else
            {
                status = $"{finished} of {total} done" + (failed.Count > 0 ? $" · {failed.Count} failed" : "");
            }
            return new ToastState
            {
                Kind = ToastKind.Running,
                Title = total == 1
                    ? isConvert ? $"Converting to {operation.Title}" : operation.Title
                    : isConvert ? $"Converting {total} files…" : $"{operation.Title} · {total} files",
                Subtitle = total == 1 ? subject : operation.Title,
                Status = status,
                Progress = total == 1 && jobs[0].State == JobState.Running ? jobs[0].Progress : total == 1 ? 0 : progress,
                IconPath = firstInput.Path,
                IconKey = operation.IconKey,
                Total = total,
                Completed = finished,
                FailedCount = failed.Count,
            };
        }

        var details = failed.Count == 0 ? null : CombineErrors(failed, total > 1);
        if (failed.Count == 0 && succeeded.Count == 0)
        {
            return new ToastState
            {
                Kind = ToastKind.Canceled,
                Title = "Canceled",
                Subtitle = total == 1 ? $"{operation.Title} · {subject}" : $"{operation.Title} · {total} files",
                IconPath = firstInput.Path,
                IconKey = operation.IconKey,
                Total = total,
                Completed = finished,
            };
        }

        if (failed.Count > 0 && succeeded.Count == 0)
        {
            return new ToastState
            {
                Kind = ToastKind.Failed,
                Title = total == 1
                    ? isConvert ? $"Couldn't convert to {operation.Title}" : $"{operation.Title} didn't work"
                    : isConvert ? $"Couldn't convert {total} files" : $"{operation.Title} failed on {failed.Count} files",
                Subtitle = subject,
                ErrorMessage = failed.Count == 1 || failed.Select(f => f.ErrorMessage).Distinct().Count() == 1
                    ? failed[0].ErrorMessage
                    : $"{failed[0].ErrorMessage} (and {failed.Count - 1} more problems)",
                ErrorDetails = details,
                IconPath = firstInput.Path,
                IconKey = operation.IconKey,
                Total = total,
                Completed = finished,
                FailedCount = failed.Count,
            };
        }

        var doneTitle = total == 1
            ? isConvert ? $"Converted to {operation.Title}" : operation.Title
            : isConvert ? $"Converted {succeeded.Count} files" : $"{operation.Title} · {succeeded.Count} files";
        if (failed.Count > 0)
        {
            doneTitle = isConvert
                ? $"Converted {succeeded.Count} of {total} · {failed.Count} failed"
                : $"{succeeded.Count} of {total} done · {failed.Count} failed";
        }

        return new ToastState
        {
            Kind = failed.Count > 0 ? ToastKind.PartlyFailed : ToastKind.Succeeded,
            Title = doneTitle,
            Subtitle = total == 1
                ? outputs.Count == 1 ? Path.GetFileName(outputs[0].TrimEnd('\\')) : subject
                : isConvert ? operation.Title : canceled > 0 ? $"{canceled} canceled" : null,
            Status = canceled > 0 && total == 1 ? null : Elapsed(jobs),
            Outputs = outputs,
            Warnings = warnings,
            Text = text,
            TextTitle = succeeded.Select(j => j.Result?.TextTitle).FirstOrDefault(t => t is not null) ?? operation.Title,
            ErrorMessage = failed.Count > 0 ? failed[0].ErrorMessage : null,
            ErrorDetails = details,
            IconPath = outputs.FirstOrDefault() ?? firstInput.Path,
            IconKey = operation.IconKey,
            Total = total,
            Completed = finished,
            FailedCount = failed.Count,
        };
    }

    static double Average(IReadOnlyList<Job> jobs) =>
        jobs.Sum(j => j.IsFinished ? 1 : j.State == JobState.Running ? j.Progress ?? 0 : 0) / jobs.Count;

    static string Percent(double fraction) => (fraction * 100).ToString("0", CultureInfo.CurrentCulture) + "%";

    static string? Elapsed(IReadOnlyList<Job> jobs)
    {
        var start = jobs.Where(j => j.StartedAt is not null).Select(j => j.StartedAt!.Value).DefaultIfEmpty().Min();
        var end = jobs.Where(j => j.FinishedAt is not null).Select(j => j.FinishedAt!.Value).DefaultIfEmpty().Max();
        if (start == default || end == default || end < start)
            return null;
        var seconds = (end - start).TotalSeconds;
        return seconds < 1 ? null
            : seconds < 60 ? $"Done in {seconds.ToString("0.#", CultureInfo.CurrentCulture)} s"
            : $"Done in {(int)(seconds / 60)} min {(int)(seconds % 60)} s";
    }

    static string? CombineText(IReadOnlyList<Job> succeeded)
    {
        var withText = succeeded.Where(j => !string.IsNullOrEmpty(j.Result?.Text)).ToList();
        if (withText.Count == 0)
            return null;
        if (withText.Count == 1)
            return withText[0].Result!.Text;
        var builder = new StringBuilder();
        foreach (var job in withText)
        {
            if (builder.Length > 0)
                builder.AppendLine();
            builder.AppendLine($"== {job.DisplayName} ==");
            builder.AppendLine(job.Result!.Text!.TrimEnd());
        }
        return builder.ToString().TrimEnd();
    }

    static string CombineErrors(IReadOnlyList<Job> failed, bool includeNames)
    {
        var builder = new StringBuilder();
        foreach (var job in failed)
        {
            if (builder.Length > 0)
                builder.AppendLine().AppendLine();
            builder.AppendLine(includeNames ? $"{job.DisplayName}: {job.ErrorMessage}" : job.ErrorMessage);
            if (!string.IsNullOrWhiteSpace(job.ErrorDetails))
                builder.AppendLine(job.ErrorDetails.TrimEnd());
        }
        return builder.ToString().TrimEnd();
    }
}
