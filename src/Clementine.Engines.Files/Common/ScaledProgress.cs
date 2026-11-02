namespace Clementine.Engines.Files.Common;

/// <summary>Maps a sub-task's 0..1 progress into a slice [from, to] of the job's progress.</summary>
internal sealed class ScaledProgress(IProgress<JobProgress> inner, double from, double to) : IProgress<JobProgress>
{
    public void Report(JobProgress value) =>
        inner.Report(value with { Fraction = value.Fraction is { } f ? from + (to - from) * Math.Clamp(f, 0, 1) : null });
}
