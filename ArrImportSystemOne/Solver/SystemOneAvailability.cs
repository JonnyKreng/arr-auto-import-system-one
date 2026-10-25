namespace ArrImportSystemOne.Solver;

/// <summary>
/// Gate shared by the health monitor and the worker. It starts closed: no decisions are made until
/// the first successful provider health check, so a cold/misconfigured model can never cause a
/// download to be marked done or left to a human.
/// </summary>
public sealed class SystemOneAvailability
{
    private readonly object _sync = new();
    private TaskCompletionSource _changed = NewSignal();
    private bool _available;
    private string? _reason = "waiting for the System One model health check";

    public (bool Available, string? Reason) Snapshot()
    {
        lock (_sync)
            return (_available, _reason);
    }

    public void ReportAvailable() => Update(true, null);

    public void ReportUnavailable(string? reason) =>
        Update(false, string.IsNullOrWhiteSpace(reason) ? "System One model unavailable" : reason);

    /// <summary>Waits until the provider is available. Returns immediately when the gate is open.</summary>
    public async Task WaitUntilAvailableAsync(CancellationToken ct)
    {
        while (true)
        {
            Task wait;
            lock (_sync)
            {
                if (_available)
                    return;

                wait = _changed.Task;
            }

            await wait.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    private void Update(bool available, string? reason)
    {
        TaskCompletionSource? changed = null;
        lock (_sync)
        {
            // A changed reason while still unavailable is only metadata; do not wake waiters.
            if (_available == available)
            {
                _reason = reason;
                return;
            }

            _available = available;
            _reason = reason;
            changed = _changed;
            _changed = NewSignal();
        }

        changed.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
