using ArrImportSystemOne.Hub;
using ArrImportSystemOne.Lidarr;
using ArrImportSystemOne.Options;
using ArrImportSystemOne.Solver;
using ArrImportSystemOne.Store;
using Microsoft.Extensions.Options;

namespace ArrImportSystemOne.Queue;

/// <summary>
/// Discovers work only: it never calls the decision provider and never imports. Downloads already marked done,
/// already in flight, or waiting out a retry backoff are skipped (systemOne.md §3 / §4).
/// Rows are re-read by the worker, so the poller does not build decisions from a stale snapshot.
/// </summary>
public sealed class QueuePoller(
    LidarrClient lidarr,
    DecisionRepository repository,
    JobQueue jobs,
    DecisionWriter decisions,
    WorkerProgress progress,
    IDecisionBroadcaster broadcaster,
    PollNotifier notifier,
    IOptionsMonitor<LidarrOptions> options,
    ILogger<QueuePoller> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, options.CurrentValue.PollIntervalSeconds));
        logger.LogInformation("Queue poller started (interval {Interval}s)", interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Queue poll failed");
            }

            try
            {
                await notifier.WaitAsync(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    internal async Task PollOnceAsync(CancellationToken ct)
    {
        var queue = await lidarr.GetQueueAsync(ct).ConfigureAwait(false);
        progress.ReportQueue(queue.Count);

        foreach (var item in queue)
        {
            var downloadId = item.DownloadId;
            if (string.IsNullOrWhiteSpace(downloadId) || item.Id <= 0)
                continue;

            if (repository.IsDownloadDone(downloadId) || jobs.IsInFlight(downloadId) || jobs.IsBackedOff(downloadId))
                continue;

            // Lidarr is still downloading / auto-importing: rediscover on a later poll.
            if (item.IsDownloading)
                continue;

            List<ManualImportFile> rows;
            try
            {
                rows = await lidarr.GetManualImportAsync(downloadId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Skipping download {DownloadId}: manual import lookup failed", downloadId);
                continue;
            }

            if (rows.Count == 0)
            {
                // Nothing to import. If Lidarr is still downloading, rediscover it on a later poll;
                // otherwise clean up the stale queue entry and mark the download done.
                if (!item.IsDownloading)
                {
                    var removed = await lidarr.RemoveStaleQueueItemAsync(item.Id, ct).ConfigureAwait(false);
                    if (removed)
                        await FinishWithoutRowsAsync(item, "no importable files", rows.Count, ct).ConfigureAwait(false);
                }
                continue;
            }

            var importable = rows.Where(r => !r.AdditionalFile).ToList();
            if (importable.Count == 0)
            {
                // Additional-file-only download: finished immediately with system skip decisions.
                foreach (var row in rows)
                    await decisions.WriteAsync(SkipDecision(item, row, "additional file"), ct).ConfigureAwait(false);

                await FinishWithoutRowsAsync(item, "additional files only", rows.Count, ct).ConfigureAwait(false);
                continue;
            }

            jobs.TryEnqueue(new ImportJob(downloadId, item.Id, item.Title, DateTime.UtcNow));
        }

        PublishProgress();
        await broadcaster.QueueUpdatedAsync(progress.BuildQueueUpdated(), ct).ConfigureAwait(false);
    }

    private async Task FinishWithoutRowsAsync(LidarrQueueItem item, string reason, int fileCount, CancellationToken ct)
    {
        repository.MarkDone(item.DownloadId!);
        jobs.ClearRetry(item.DownloadId!);

        var record = new JobRecord
        {
            DownloadId = item.DownloadId!,
            QueueId = item.Id,
            Title = item.Title,
            CreatedUtc = DateTime.UtcNow,
            FinishedUtc = DateTime.UtcNow,
            Status = "Finished",
            Outcome = "Skip",
            FileCount = fileCount,
            LatencyMs = 0
        };
        record.Id = repository.InsertJob(record);
        logger.LogInformation("Download {DownloadId} skipped: {Reason}", item.DownloadId, reason);
        await broadcaster.JobFinishedAsync(record, ct).ConfigureAwait(false);
    }

    private static DecisionRecord SkipDecision(LidarrQueueItem item, ManualImportFile row, string reason) => new()
    {
        App = "Lidarr",
        DownloadId = item.DownloadId ?? row.DownloadId ?? "",
        QueueId = item.Id,
        FilePath = row.Path,
        FileName = row.Name,
        RowId = ImportContextBuilder.RowKey(row),
        Resolver = "System",
        Action = "Skip",
        Status = "Applied",
        Error = reason
    };

    private void PublishProgress()
    {
        progress.ReportPending(jobs.PendingSnapshot());
    }
}
