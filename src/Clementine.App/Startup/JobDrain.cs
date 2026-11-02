using Clementine.Core;

namespace Clementine.App.Startup;

/// <summary>
/// Cancels every job and gives the running ones a moment to stop: a cancelled job kills its tool, deletes its
/// partial output and its temp folder before its <see cref="Job.Completion"/> completes, so exiting right after
/// <see cref="JobQueue.CancelAll"/> would leave those behind.
/// </summary>
internal static class JobDrain
{
    /// <summary>How long Quit waits for cancelled jobs (the UI stays responsive meanwhile).</summary>
    public static readonly TimeSpan QuitGrace = TimeSpan.FromSeconds(5);

    /// <summary>How long sign-out / shutdown waits: Windows only allows a few seconds before it asks the user.</summary>
    public static readonly TimeSpan SessionEndGrace = TimeSpan.FromSeconds(3);

    /// <summary>Cancels all jobs and waits up to <paramref name="timeout"/>; true when every job finished in time.</summary>
    public static async Task<bool> CancelAndWaitAsync(JobQueue jobs, TimeSpan timeout)
    {
        var pending = CancelAll(jobs);
        if (pending.IsCompleted)
            return true;
        // Stays on the caller's context (the UI thread), which keeps pumping messages while this waits.
        var finished = await Task.WhenAny(pending, Task.Delay(timeout));
        return finished == pending;
    }

    /// <summary>
    /// Blocking variant for <see cref="System.Windows.Application.SessionEnding"/>, which must decide before it returns.
    /// Jobs never need the UI thread to finish, so blocking it is safe; the wait is bounded either way.
    /// </summary>
    public static bool CancelAndWait(JobQueue jobs, TimeSpan timeout)
    {
        var pending = CancelAll(jobs);
        try
        {
            return pending.Wait(timeout);
        }
        catch (AggregateException)
        {
            return true; // Completion never faults today; if it ever does, the job is still over.
        }
    }

    static Task CancelAll(JobQueue jobs)
    {
        var running = jobs.Snapshot().Where(j => !j.IsFinished).Select(j => j.Completion).ToList();
        jobs.CancelAll();
        return running.Count == 0 ? Task.CompletedTask : Task.WhenAll(running);
    }
}
