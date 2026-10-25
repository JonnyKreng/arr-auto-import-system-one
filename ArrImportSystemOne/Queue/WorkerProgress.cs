using ArrImportSystemOne.Solver;

namespace ArrImportSystemOne.Queue;

/// <summary>
/// Live progress shared by the poller and worker and pushed to the UI as <c>queue.updated</c>.
/// Also carries the System One availability gate so the UI can show a paused banner.
/// Kept from the existing design (systemOne.md §6.2).
/// </summary>
public sealed class WorkerProgress(SystemOneAvailability availability)
{
    private readonly object _sync = new();

    private int _queueCount;
    private int _processedCount;
    private int _pendingCount;
    private string? _currentItem;
    private string? _currentDownloadId;
    private string? _currentFile;
    private bool _inProgress;
    private string[] _pending = Array.Empty<string>();

    public void ReportQueue(int queueCount)
    {
        lock (_sync)
            _queueCount = queueCount;
    }

    public void ReportPending(IEnumerable<ImportJob> pending)
    {
        lock (_sync)
        {
            _pending = pending
                .Select(j => string.IsNullOrWhiteSpace(j.Title) ? j.DownloadId : $"{j.Title} ({j.DownloadId})")
                .ToArray();
            _pendingCount = _pending.Length;
        }
    }

    public void ReportJobStarted(ImportJob job)
    {
        lock (_sync)
        {
            _currentItem = job.Title;
            _currentDownloadId = job.DownloadId;
            _currentFile = null;
            _inProgress = true;
        }
    }

    public void ReportCurrentFile(string? file)
    {
        lock (_sync)
            _currentFile = file;
    }

    public void ReportJobFinished()
    {
        lock (_sync)
        {
            _currentItem = null;
            _currentDownloadId = null;
            _currentFile = null;
            _inProgress = false;
            _processedCount++;
        }
    }

    public QueueUpdated BuildQueueUpdated()
    {
        lock (_sync)
        {
            var (available, pauseReason) = availability.Snapshot();

            return new QueueUpdated(
                _queueCount,
                _processedCount,
                _pendingCount,
                _pending,
                _currentItem,
                _currentDownloadId,
                _currentFile,
                _inProgress,
                !available,
                pauseReason);
        }
    }
}

/// <summary>Payload of the <c>queue.updated</c> broadcast (systemOne.md §9.2).</summary>
public sealed record QueueUpdated(
    int QueueCount,
    int ProcessedCount,
    int PendingCount,
    string[] Pending,
    string? CurrentItem,
    string? CurrentDownloadId,
    string? CurrentFile,
    bool InProgress,
    bool Paused,
    string? PauseReason);
