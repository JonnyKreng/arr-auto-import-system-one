using LiteDB;

namespace ArrImportSystemOne.Store;

/// <summary>
/// Durable store (LiteDB): <c>decisions</c> + <c>jobs</c> + <c>done</c>. The chat collections of the
/// old LLM design are gone (systemOne.md §5 / §14.4).
/// </summary>
public sealed class DecisionRepository : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<DecisionRecord> _decisions;
    private readonly ILiteCollection<JobRecord> _jobs;
    private readonly ILiteCollection<DoneRecord> _done;
    private readonly object _sync = new();

    public DecisionRepository(IHostEnvironment env, ILogger<DecisionRepository> logger)
    {
        var path = Path.Combine(env.ContentRootPath, "data", "arrimport.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        _db = new LiteDatabase($"Filename={path};Connection=shared");
        _decisions = _db.GetCollection<DecisionRecord>("decisions");
        _jobs = _db.GetCollection<JobRecord>("jobs");
        _done = _db.GetCollection<DoneRecord>("done");

        _decisions.EnsureIndex(d => d.DownloadId);
        _decisions.EnsureIndex(d => d.UtcTimestamp);
        _jobs.EnsureIndex(j => j.DownloadId);

        MigrateLegacyDryRunMarkers(logger);

        logger.LogInformation("Decision store at {Path}", path);
    }

    /// <summary>
    /// Older databases stored done markers without the <see cref="DoneRecord.DryRun"/> flag. A
    /// marker whose download only has <c>SkippedDryRun</c> decisions is a dry-run marker: flag it
    /// so disabling dry-run later makes the download eligible again.
    /// </summary>
    private void MigrateLegacyDryRunMarkers(ILogger logger)
    {
        foreach (var marker in _done.FindAll().ToList())
        {
            if (marker.DryRun)
                continue;

            var downloadId = marker.DownloadId;
            var decisions = _decisions.Query().Where(d => d.DownloadId == downloadId).ToList();

            var hasSkippedDryRun = decisions.Any(d => d.Status == "SkippedDryRun");
            var hasAppliedAction = decisions.Any(d =>
                (d.Action is "Import" or "RejectBlock") && (d.Status is "Done" or "Applied"));

            if (!hasSkippedDryRun || hasAppliedAction)
                continue;

            marker.DryRun = true;
            _done.Update(marker);
            logger.LogInformation(
                "Migrated done marker for {DownloadId} to dry-run; it will be applied after DryRun is disabled",
                downloadId);
        }
    }

    // ---- decisions -------------------------------------------------------------------------

    public int InsertDecision(DecisionRecord record)
    {
        lock (_sync)
            return _decisions.Insert(record).AsInt32;
    }

    public void UpdateDecision(DecisionRecord record)
    {
        lock (_sync)
            _decisions.Update(record);
    }

    public DecisionRecord? GetDecision(int id)
    {
        lock (_sync)
            return _decisions.FindById(id);
    }

    public IReadOnlyList<DecisionRecord> GetDecisions(int limit)
    {
        lock (_sync)
            return _decisions.Query().OrderByDescending(d => d.Id).Limit(limit).ToList();
    }

    /// <summary>Per-row re-decision guard: the most recent decision for a given download+row.</summary>
    public DecisionRecord? FindLatest(string downloadId, int rowId)
    {
        lock (_sync)
            return _decisions.Query()
                .Where(d => d.DownloadId == downloadId && d.RowId == rowId)
                .OrderByDescending(d => d.Id)
                .FirstOrDefault();
    }

    /// <summary>Flags every decision of a download as Restarted (used by RestartAlbum).</summary>
    public int FlagRestarted(string downloadId)
    {
        lock (_sync)
        {
            var rows = _decisions.Query().Where(d => d.DownloadId == downloadId).ToList();
            foreach (var row in rows)
            {
                row.Status = "Restarted";
                _decisions.Update(row);
            }
            return rows.Count;
        }
    }

    // ---- jobs ------------------------------------------------------------------------------

    public int InsertJob(JobRecord record)
    {
        lock (_sync)
            return _jobs.Insert(record).AsInt32;
    }

    public IReadOnlyList<JobRecord> GetJobs(int limit)
    {
        lock (_sync)
            return _jobs.Query().OrderByDescending(j => j.Id).Limit(limit).ToList();
    }

    // ---- done ------------------------------------------------------------------------------

    /// <summary>
    /// Whether a download needs no further work. A dry-run marker is only honoured while
    /// <paramref name="dryRun"/> is true; disabling dry-run re-opens the download so its previewed
    /// decisions can be applied.
    /// </summary>
    public bool IsDownloadDone(string downloadId, bool dryRun = false)
    {
        lock (_sync)
        {
            var marker = _done.FindById(downloadId);
            return marker is not null && (!marker.DryRun || dryRun);
        }
    }

    /// <summary>
    /// Writes the done marker. <paramref name="dryRun"/> should be true only when the download was
    /// finished by a dry-run pass (imports/rejects skipped). A later non-dry-run completion always
    /// upgrades the marker so it stays done regardless of the current mode.
    /// </summary>
    public void MarkDone(string downloadId, bool dryRun = false)
    {
        lock (_sync)
        {
            var existing = _done.FindById(downloadId);
            var effectiveDryRun = dryRun && (existing is null || existing.DryRun);
            _done.Upsert(new DoneRecord
            {
                DownloadId = downloadId,
                DoneUtc = DateTime.UtcNow,
                DryRun = effectiveDryRun
            });
        }
    }

    public void ClearDone(string downloadId)
    {
        lock (_sync)
            _done.Delete(downloadId);
    }

    public void Dispose() => _db.Dispose();
}
