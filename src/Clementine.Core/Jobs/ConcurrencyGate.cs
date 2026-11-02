namespace Clementine.Core;

/// <summary>
/// A FIFO async gate like <see cref="SemaphoreSlim"/>, except its limit is read live, so changing the
/// "heavy jobs at once" setting applies to queued jobs without a restart. Call <see cref="Pump"/> after the limit grows.
/// </summary>
internal sealed class ConcurrencyGate(Func<int> limit)
{
    readonly object _lock = new();
    readonly LinkedList<TaskCompletionSource> _waiters = new();
    int _running;

    int Limit => Math.Max(1, limit());

    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TaskCompletionSource waiter;
        LinkedListNode<TaskCompletionSource> node;
        lock (_lock)
        {
            if (_running < Limit && _waiters.Count == 0)
            {
                _running++;
                return;
            }
            waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            node = _waiters.AddLast(waiter);
        }

        await using (cancellationToken.Register(() =>
        {
            lock (_lock)
            {
                if (node.List is null)
                    return; // already admitted
                _waiters.Remove(node);
            }
            waiter.TrySetCanceled(cancellationToken);
        }))
        {
            await waiter.Task.ConfigureAwait(false);
        }
    }

    public void Release()
    {
        lock (_lock)
        {
            _running = Math.Max(0, _running - 1);
            PumpLocked();
        }
    }

    /// <summary>Admits queued waiters if the limit now allows more to run.</summary>
    public void Pump()
    {
        lock (_lock)
            PumpLocked();
    }

    void PumpLocked()
    {
        while (_waiters.First is { } first && _running < Limit)
        {
            _waiters.RemoveFirst();
            _running++;
            first.Value.TrySetResult();
        }
    }
}
