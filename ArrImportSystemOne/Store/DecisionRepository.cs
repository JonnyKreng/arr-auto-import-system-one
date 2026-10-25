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

        logger.LogInformation("Decision store at {Path}", path);
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

    public bool IsDownloadDone(string downloadId)
    {
        lock (_sync)
            return _done.Exists(d => d.DownloadId == downloadId);
    }

    public void MarkDone(string downloadId)
    {
        lock (_sync)
            _done.Upsert(new DoneRecord { DownloadId = downloadId, DoneUtc = DateTime.UtcNow });
    }

    public void ClearDone(string downloadId)
    {
        lock (_sync)
            _done.Delete(downloadId);
    }

    public void Dispose() => _db.Dispose();
}
