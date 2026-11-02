using System.Windows.Threading;
using Clementine.App.Dialogs;
using Clementine.App.Interop;
using Clementine.Core;
using Clementine.Core.Hosting;

namespace Clementine.App.Toasts;

/// <summary>
/// Shows a toast card per launch (a batch drop collapses into one card), keeps it in sync with the job queue,
/// plays the optional sound, reveals outputs when asked to and auto-dismisses successes. UI thread only, except
/// for the job-queue event handlers which marshal themselves.
/// </summary>
public sealed class ToastManager
{
    static readonly TimeSpan SuccessTimeout = TimeSpan.FromSeconds(6);
    static readonly TimeSpan TextTimeout = TimeSpan.FromSeconds(15);
    static readonly TimeSpan WarningTimeout = TimeSpan.FromSeconds(10);
    static readonly TimeSpan CanceledTimeout = TimeSpan.FromSeconds(2.5);
    static readonly TimeSpan InfoTimeout = TimeSpan.FromSeconds(9);
    const int MaxCards = 6;

    readonly HostServices _services;
    readonly Dispatcher _dispatcher;
    readonly Dictionary<Guid, Entry> _byJob = [];
    readonly List<Entry> _entries = [];
    readonly HashSet<Guid> _dirty = [];
    readonly object _dirtyGate = new();
    bool _flushPending;
    ToastHostWindow? _host;

    public ToastManager(HostServices services, Dispatcher dispatcher)
    {
        _services = services;
        _dispatcher = dispatcher;
        services.Jobs.JobAdded += (_, job) => _dispatcher.BeginInvoke(() => AdoptUntracked(job), DispatcherPriority.Background);
        services.Jobs.JobChanged += (_, job) => MarkDirty(job.Id);
    }

    /// <summary>Raised (UI thread) when a job of any card finishes; the main window refreshes its recent list.</summary>
    public event EventHandler? JobsChanged;

    /// <summary>Starts a card for jobs from one launch. Call on the UI thread right after <c>JobQueue.Enqueue</c>.</summary>
    public void Track(IReadOnlyList<Job> jobs)
    {
        var fresh = jobs.Where(j => !_byJob.ContainsKey(j.Id)).ToList();
        if (fresh.Count == 0)
            return;
        var entry = new Entry(fresh);
        foreach (var job in fresh)
            _byJob[job.Id] = entry;
        _entries.Add(entry);
        Update(entry);
    }

    /// <summary>
    /// Shows a message card (first-run hint, "this can't run here", an update to restart into, errors outside jobs).
    /// It closes by itself after <paramref name="timeout"/> (default 9 s) unless the pointer is on it.
    /// </summary>
    public void ShowInfo(ToastState state, Action? action = null, TimeSpan? timeout = null)
    {
        var entry = new Entry([]) { Custom = action };
        _entries.Add(entry);
        Show(entry, state);
        StartTimer(entry, timeout ?? InfoTimeout);
    }

    /// <summary>Removes every card (quit, tests).</summary>
    public void DismissAll()
    {
        foreach (var entry in _entries.ToList())
            Dismiss(entry, animate: false);
    }

    void AdoptUntracked(Job job)
    {
        // Jobs queued by something other than the UI launcher still get a card.
        if (!_byJob.ContainsKey(job.Id))
            Track([job]);
    }

    void MarkDirty(Guid jobId)
    {
        lock (_dirtyGate)
        {
            _dirty.Add(jobId);
            if (_flushPending)
                return;
            _flushPending = true;
        }
        _dispatcher.BeginInvoke(Flush, DispatcherPriority.Background);
    }

    void Flush()
    {
        Guid[] ids;
        lock (_dirtyGate)
        {
            ids = [.. _dirty];
            _dirty.Clear();
            _flushPending = false;
        }
        foreach (var entry in ids.Select(id => _byJob.GetValueOrDefault(id)).OfType<Entry>().Distinct())
            Update(entry);
        if (ids.Length > 0)
            JobsChanged?.Invoke(this, EventArgs.Empty);
    }

    void Update(Entry entry)
    {
        ToastState state;
        try
        {
            state = ToastState.FromJobs(entry.Jobs);
        }
        catch (Exception ex)
        {
            _services.Log.Warn("Couldn't build toast state", ex);
            return;
        }

        var settings = _services.Settings.Current;
        var failed = state.Kind is ToastKind.Failed or ToastKind.PartlyFailed;
        var quiet = entry.Jobs.All(j => j.Operation is Actions.IQuietOperation);
        if (failed || (!quiet && (settings.ShowNotifications || state.HasText)))
            Show(entry, state);

        if (state.IsFinished && !entry.FinishHandled)
        {
            entry.FinishHandled = true;
            OnFinished(entry, state);
        }
    }

    void Show(Entry entry, ToastState state)
    {
        if (entry.Dismissed)
            return;
        if (entry.Card is null)
        {
            var card = new ToastCard();
            card.ActionRequested += (_, action) => OnAction(entry, action);
            card.MouseEnter += (_, _) => entry.Timer?.Stop();
            card.MouseLeave += (_, _) =>
            {
                if (entry.Timer is { } timer)
                {
                    timer.Interval = TimeSpan.FromSeconds(3);
                    timer.Start();
                }
            };
            entry.Card = card;
            card.Apply(state);
            Host.AddCard(card);
            TrimCards();
        }
        else
        {
            entry.Card.Apply(state);
        }
    }

    void OnFinished(Entry entry, ToastState state)
    {
        var settings = _services.Settings.Current;
        if (settings.PlaySounds && state.Kind != ToastKind.Canceled)
        {
            try
            {
                if (state.Kind == ToastKind.Succeeded)
                    System.Media.SystemSounds.Asterisk.Play();
                else
                    System.Media.SystemSounds.Hand.Play();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }

        if (settings.RevealInExplorerWhenDone && state.Outputs.Count > 0)
            ExplorerShell.Reveal(state.Outputs, _services.Log);

        var timeout = state.Kind switch
        {
            ToastKind.Succeeded when state.HasText => TextTimeout,
            ToastKind.Succeeded when state.Warnings.Count > 0 => WarningTimeout,
            ToastKind.Succeeded => SuccessTimeout,
            ToastKind.Canceled => CanceledTimeout,
            _ => (TimeSpan?)null,
        };
        if (timeout is { } t)
            StartTimer(entry, t);
    }

    void StartTimer(Entry entry, TimeSpan timeout)
    {
        entry.Timer?.Stop();
        var timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = timeout };
        timer.Tick += (_, _) =>
        {
            if (entry.Card?.IsMouseOver == true)
                return;
            timer.Stop();
            Dismiss(entry, animate: true);
        };
        entry.Timer = timer;
        if (entry.Card?.IsMouseOver != true)
            timer.Start();
    }

    void OnAction(Entry entry, ToastAction action)
    {
        var state = entry.Card?.State;
        try
        {
            switch (action)
            {
                case ToastAction.Close:
                    if (entry.Jobs.Any(j => !j.IsFinished))
                    {
                        foreach (var job in entry.Jobs)
                            job.Cancel();
                    }
                    else
                    {
                        Dismiss(entry, animate: true);
                    }
                    break;
                case ToastAction.ShowInFolder when state is not null:
                    ExplorerShell.Reveal(state.Outputs, _services.Log);
                    break;
                case ToastAction.Open when state is { Outputs.Count: > 0 }:
                    ExplorerShell.Open(state.Outputs[0], _services.Log);
                    break;
                case ToastAction.CopyText when state?.Text is { } text:
                    ClipboardText.TrySet(text);
                    break;
                case ToastAction.ViewText when state?.Text is { } text:
                    TextViewerWindow.Open(state.TextTitle ?? "Result", state.Subtitle, text);
                    break;
                case ToastAction.Details when state is not null:
                    TextViewerWindow.Open("Details", state.Title, state.ErrorDetails ?? state.ErrorMessage ?? "");
                    break;
                case ToastAction.Custom:
                    entry.Custom?.Invoke();
                    Dismiss(entry, animate: true);
                    break;
            }
        }
        catch (OperationException ex)
        {
            ShowInfo(ToastState.Info("That didn't work", ex.Message));
        }
        catch (Exception ex)
        {
            _services.Log.Warn($"Toast action {action} failed", ex);
        }
    }

    void Dismiss(Entry entry, bool animate)
    {
        if (entry.Dismissed)
            return;
        entry.Dismissed = true;
        entry.Timer?.Stop();
        _entries.Remove(entry);
        foreach (var job in entry.Jobs)
        {
            if (job.IsFinished)
                _byJob.Remove(job.Id);
        }
        if (entry.Card is not { } card)
            return;
        if (animate)
            card.AnimateOut(() => Host.RemoveCard(card));
        else
            Host.RemoveCard(card);
    }

    void TrimCards()
    {
        var shown = _entries.Where(e => e.Card is not null && !e.Dismissed).ToList();
        foreach (var old in shown.Where(e => e.Jobs.All(j => j.IsFinished)).Take(Math.Max(0, shown.Count - MaxCards)))
            Dismiss(old, animate: false);
    }

    ToastHostWindow Host => _host ??= new ToastHostWindow();

    sealed class Entry(IReadOnlyList<Job> jobs)
    {
        public IReadOnlyList<Job> Jobs { get; } = jobs;

        public ToastCard? Card { get; set; }

        public DispatcherTimer? Timer { get; set; }

        public bool FinishHandled { get; set; }

        public bool Dismissed { get; set; }

        public Action? Custom { get; init; }
    }
}
