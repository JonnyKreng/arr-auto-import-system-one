namespace ArrImportSystemOne.Queue;

/// <summary>
/// Wakes the poller immediately after a restart so a cleared download is re-enqueued without waiting
/// a full poll interval (kept from the existing design, systemOne.md §6.2).
/// </summary>
public sealed class PollNotifier
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Signal()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signalled; the poller will pick it up.
        }
    }

    /// <summary>Waits for the interval or a signal, whichever comes first.</summary>
    public async Task WaitAsync(TimeSpan interval, CancellationToken ct)
    {
        await _signal.WaitAsync(interval, ct).ConfigureAwait(false);
    }
}
