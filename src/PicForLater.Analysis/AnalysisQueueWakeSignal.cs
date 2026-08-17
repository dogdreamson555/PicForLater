using PicForLater.Core.Analysis;

namespace PicForLater.Analysis;

/// <summary>
/// Process-local wake signal. Durable timing remains in SQLite, so losing a
/// notification cannot lose a job and no idle polling loop is required.
/// </summary>
public sealed class AnalysisQueueWakeSignal : IAnalysisQueueNotifier, IDisposable
{
    private readonly SemaphoreSlim _signal = new(0, 1);
    private bool _disposed;

    public event EventHandler<AnalysisItemChangedEventArgs>? ItemChanged;

    public void Notify()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_signal.CurrentCount == 0)
        {
            try
            {
                _signal.Release();
            }
            catch (SemaphoreFullException)
            {
                // Another producer already coalesced a wake-up.
            }
        }
    }

    internal void NotifyItemChanged(Guid imageItemId)
    {
        var handlers = ItemChanged;
        if (handlers is null)
        {
            return;
        }

        var args = new AnalysisItemChangedEventArgs(imageItemId);
        foreach (EventHandler<AnalysisItemChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch
            {
                // UI observers are best-effort. A stale view can be refreshed from
                // SQLite later and must never change durable worker completion.
            }
        }
    }

    internal async Task WaitAsync(
        DateTimeOffset? wakeAtUtc,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (wakeAtUtc is null)
        {
            await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var delay = wakeAtUtc.Value - timeProvider.GetUtcNow();
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        await _signal.WaitAsync(delay, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _signal.Dispose();
    }
}

public sealed class AnalysisItemChangedEventArgs(Guid imageItemId) : EventArgs
{
    public Guid ImageItemId { get; } = imageItemId;
}
