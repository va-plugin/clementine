using System.Diagnostics;

namespace Clementine.Core;

public enum JobState
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Canceled,
}

/// <summary>One queued run of an operation. Properties change on worker threads; listen to <see cref="JobQueue.JobChanged"/>.</summary>
public sealed class Job
{
    readonly CancellationTokenSource _cts = new();
    readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Job(IOperation operation, IReadOnlyList<InputFile> inputs, OptionValues options)
    {
        Operation = operation;
        Inputs = inputs;
        Options = options;
    }

    public Guid Id { get; } = Guid.NewGuid();

    public IOperation Operation { get; }

    public IReadOnlyList<InputFile> Inputs { get; }

    public OptionValues Options { get; }

    public JobState State { get; internal set; } = JobState.Queued;

    /// <summary>0..1, or null while indeterminate.</summary>
    public double? Progress { get; internal set; }

    public string? Status { get; internal set; }

    public OperationResult? Result { get; internal set; }

    public string? ErrorMessage { get; internal set; }

    public string? ErrorDetails { get; internal set; }

    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.Now;

    public DateTimeOffset? StartedAt { get; internal set; }

    public DateTimeOffset? FinishedAt { get; internal set; }

    public bool IsFinished => State is JobState.Succeeded or JobState.Failed or JobState.Canceled;

    /// <summary>"clip.mp4" or "clip.mp4 + 3 more".</summary>
    public string DisplayName => Inputs.Count == 1 ? Inputs[0].Name : $"{Inputs[0].Name} + {Inputs.Count - 1} more";

    /// <summary>Completes (never faults) when the job reaches a finished state.</summary>
    public Task Completion => _completion.Task;

    internal CancellationToken CancellationToken => _cts.Token;

    public void Cancel()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    internal void MarkCompleted() => _completion.TrySetResult();
}

/// <summary>Runs jobs in the background: heavy operations share a small pool, light ones a larger one.</summary>
public sealed class JobQueue
{
    readonly EngineContext _context;
    readonly OutputPlanner _output;
    readonly ConcurrencyGate _heavy;
    readonly ConcurrencyGate _light;
    readonly List<Job> _jobs = [];
    readonly object _gate = new();

    /// <param name="maxHeavyJobs">Fixed heavy-job limit (tests); null follows <see cref="ClementineSettings.MaxConcurrentHeavyJobs"/> live.</param>
    public JobQueue(EngineContext context, OutputPlanner output, int? maxHeavyJobs = null)
    {
        _context = context;
        _output = output;
        _heavy = new ConcurrencyGate(() => Math.Clamp(maxHeavyJobs ?? _context.Settings.MaxConcurrentHeavyJobs, 1, 16));
        var lightLimit = Math.Clamp(Environment.ProcessorCount, 2, 16);
        _light = new ConcurrencyGate(() => lightLimit);
    }

    /// <summary>Re-applies concurrency limits after settings change (lets queued heavy jobs start if the limit grew).</summary>
    public void RefreshLimits() => _heavy.Pump();

    /// <summary>Raised (on a worker thread) when a job is queued.</summary>
    public event EventHandler<Job>? JobAdded;

    /// <summary>Raised (on a worker thread, throttled to ~10/s per job for progress) when a job changes.</summary>
    public event EventHandler<Job>? JobChanged;

    public IReadOnlyList<Job> Snapshot()
    {
        lock (_gate)
            return _jobs.ToList();
    }

    /// <summary>Queues the operation: one job per file for per-file operations, one job for combined ones.</summary>
    public IReadOnlyList<Job> Enqueue(IOperation operation, IReadOnlyList<InputFile> inputs, IReadOnlyDictionary<string, object?>? options = null)
    {
        if (inputs.Count == 0)
            return [];
        var merged = OptionValues.Merge(OptionValues.Defaults(operation.Options), options);
        var jobs = operation.InputMode == InputMode.PerFile
            ? inputs.Select(input => new Job(operation, [input], new OptionValues(merged))).ToList()
            : [new Job(operation, inputs, merged)];

        foreach (var job in jobs)
        {
            lock (_gate)
                _jobs.Add(job);
            JobAdded?.Invoke(this, job);
            _ = RunAsync(job);
        }
        return jobs;
    }

    public void CancelAll()
    {
        foreach (var job in Snapshot().Where(j => !j.IsFinished))
            job.Cancel();
    }

    /// <summary>Forgets finished jobs.</summary>
    public void ClearFinished()
    {
        lock (_gate)
            _jobs.RemoveAll(j => j.IsFinished);
    }

    async Task RunAsync(Job job)
    {
        var pool = job.Operation.Info.Weight == OperationWeight.Heavy ? _heavy : _light;
        try
        {
            await pool.WaitAsync(job.CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Finish(job, JobState.Canceled);
            return;
        }

        var temp = Path.Combine(AppPaths.TempRoot, job.Id.ToString("N"));
        var final = JobState.Failed;
        try
        {
            Directory.CreateDirectory(temp);
            job.State = JobState.Running;
            job.StartedAt = DateTimeOffset.Now;
            Raise(job);

            var request = new OperationRequest
            {
                Inputs = job.Inputs,
                Options = job.Options,
                Output = _output,
                Context = _context,
                TempDirectory = temp,
            };
            var progress = new ThrottledProgress(job, () => Raise(job));
            var result = await Task.Run(() => job.Operation.RunAsync(request, progress, job.CancellationToken), job.CancellationToken)
                .ConfigureAwait(false);

            job.Result = result;
            job.Progress = 1;
            _context.Log.Info($"{job.Operation.Info.Id} OK: {job.DisplayName} -> {string.Join(", ", result.Outputs)}");
            final = JobState.Succeeded;
        }
        catch (OperationCanceledException) when (job.CancellationToken.IsCancellationRequested)
        {
            final = JobState.Canceled;
        }
        catch (OperationException ex)
        {
            job.ErrorMessage = ex.Message;
            job.ErrorDetails = ex.Details ?? ex.InnerException?.ToString();
            _context.Log.Warn($"{job.Operation.Info.Id} failed on {job.DisplayName}: {ex.Message}\n{job.ErrorDetails}");
        }
        catch (Exception ex)
        {
            job.ErrorMessage = $"Something went wrong: {ex.Message}";
            job.ErrorDetails = ex.ToString();
            _context.Log.Error($"{job.Operation.Info.Id} crashed on {job.DisplayName}", ex);
        }
        finally
        {
            pool.Release();
        }

        // Clean up before announcing completion, so hosts that exit on Completion (CLI, Quit) leave no temp folder.
        await DeleteDirectoryWithRetriesAsync(temp).ConfigureAwait(false);
        Finish(job, final);
    }

    void Finish(Job job, JobState state)
    {
        job.State = state;
        job.FinishedAt = DateTimeOffset.Now;
        Raise(job);
        job.MarkCompleted();
    }

    void Raise(Job job)
    {
        try
        {
            JobChanged?.Invoke(this, job);
        }
        catch (Exception ex)
        {
            _context.Log.Warn("JobChanged handler threw", ex);
        }
    }

    // A just-killed tool can hold a file for a moment; retry briefly instead of leaking the folder.
    static async Task DeleteDirectoryWithRetriesAsync(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(100 * (attempt + 1)).ConfigureAwait(false);
            }
        }
    }

    sealed class ThrottledProgress(Job job, Action notify) : IProgress<JobProgress>
    {
        readonly Stopwatch _sinceLast = Stopwatch.StartNew();
        readonly object _lock = new();

        public void Report(JobProgress value)
        {
            bool raise;
            lock (_lock)
            {
                var statusChanged = value.Status is not null && value.Status != job.Status;
                job.Progress = value.Fraction is { } f ? Math.Clamp(f, 0, 1) : null;
                if (value.Status is not null)
                    job.Status = value.Status;
                raise = statusChanged || _sinceLast.ElapsedMilliseconds >= 100;
                if (raise)
                    _sinceLast.Restart();
            }
            if (raise)
                notify();
        }
    }
}
