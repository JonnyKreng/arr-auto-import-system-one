using System.Collections.Concurrent;
using System.Threading.Channels;

namespace ArrImportSystemOne.Queue;

/// <summary>
/// In-memory pending-job queue (bounded <see cref="Channel{T}"/>) plus the in-flight download dedupe
/// set. Nothing pending is persisted: on restart the poller simply re-discovers the Lidarr queue
/// (systemOne.md §4).
/// </summary>
public sealed class JobQueue
{
    public const int Capacity = 256;

    private readonly Channel<ImportJob> _channel;
    private readonly ConcurrentDictionary<string, byte> _inFlight = new();
    private readonly ConcurrentQueue<ImportJob> _pending = new();

    // In-memory per-download retry/backoff for jobs that were not marked done (systemOne.md §11.3).
    // Deliberately not persisted: a restart may retry immediately.
    private readonly ConcurrentDictionary<string, int> _attempts = new();
    private readonly ConcurrentDictionary<string, DateTime> _retryAfter = new();

    public JobQueue()
    {
        _channel = Channel.CreateBounded<ImportJob>(new BoundedChannelOptions(Capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    /// <summary>Enqueues a download unless it is already in flight. Returns false when deduped.</summary>
    public bool TryEnqueue(ImportJob job)
    {
        if (!_inFlight.TryAdd(job.DownloadId, 0))
            return false;

        if (_channel.Writer.TryWrite(job))
        {
            _pending.Enqueue(job);
            return true;
        }

        // Channel full / completed: roll back the dedupe entry so a later poll can retry.
        _inFlight.TryRemove(job.DownloadId, out _);
        return false;
    }

    public async ValueTask<ImportJob> DequeueAsync(CancellationToken ct)
    {
        var job = await _channel.Reader.ReadAsync(ct).ConfigureAwait(false);
        RemovePending(job.DownloadId);
        return job;
    }

    /// <summary>Marks a download as no longer in flight (call after the worker is done with it).</summary>
    public void Complete(string downloadId)
    {
        _inFlight.TryRemove(downloadId, out _);
        RemovePending(downloadId);
    }

    private void RemovePending(string downloadId)
    {
        // Drop the first matching pending entry (queue order is preserved).
        var remaining = _pending.Count;
        for (var i = 0; i < remaining; i++)
        {
            if (!_pending.TryDequeue(out var job))
                break;
            if (job.DownloadId != downloadId)
                _pending.Enqueue(job);
        }
    }

    public bool IsInFlight(string downloadId) => _inFlight.ContainsKey(downloadId);

    /// <summary>
    /// Schedules a retry for a job that must not mark the download done (left to human / failure).
    /// Exponential backoff capped at 10 minutes, attempt state kept in memory only.
    /// </summary>
    public void ScheduleRetry(string downloadId, TimeSpan baseDelay)
    {
        var attempt = _attempts.AddOrUpdate(downloadId, 1, (_, current) => current + 1);
        var seconds = baseDelay.TotalSeconds * Math.Pow(2, Math.Min(attempt - 1, 4));
        _retryAfter[downloadId] = DateTime.UtcNow.AddSeconds(Math.Min(seconds, 600));
    }

    /// <summary>True while a download is waiting out its retry backoff.</summary>
    public bool IsBackedOff(string downloadId) =>
        _retryAfter.TryGetValue(downloadId, out var retryAt) && retryAt > DateTime.UtcNow;

    public void ClearRetry(string downloadId)
    {
        _attempts.TryRemove(downloadId, out _);
        _retryAfter.TryRemove(downloadId, out _);
    }

    public int InFlightCount => _inFlight.Count;

    public IReadOnlyList<ImportJob> PendingSnapshot() => _pending.ToArray();

    public int PendingCount => _pending.Count;
}
