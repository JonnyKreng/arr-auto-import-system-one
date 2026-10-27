using LiteDB;

namespace ArrImportSystemOne.Store;

/// <summary>
/// One row per manual-import file (LiteDB collection <c>decisions</c>). <see cref="State"/> and
/// <see cref="Questions"/> are the persisted System One request; <see cref="ModelAnswer"/> is the
/// persisted System One result. Together they back the UI info button (systemOne.md §5.3 / §9.1).
/// </summary>
public sealed class DecisionRecord
{
    [BsonId] public int Id { get; set; }

    public DateTime UtcTimestamp { get; set; } = DateTime.UtcNow;
    public string App { get; set; } = "Lidarr";
    public string DownloadId { get; set; } = "";
    public int QueueId { get; set; }
    public string? FilePath { get; set; }
    public string? FileName { get; set; }
    public int RowId { get; set; }

    public string? Candidates { get; set; }   // JSON
    public string? Questions { get; set; }    // JSON — persisted System One request (questions)
    public string? State { get; set; }        // JSON — persisted System One request (state)
    public string? ModelAnswer { get; set; }  // JSON — persisted System One result

    /// <summary>"SystemOne" for model decisions, "System" for additional-file / stale / no-file skips.</summary>
    public string Resolver { get; set; } = "SystemOne";

    public string Action { get; set; } = "";  // Import | RejectBlock | LeaveToHuman | Skip
    public string Status { get; set; } = "";  // Applied | SkippedDryRun | Failed | Done | Restarted
    public string? Error { get; set; }
    public long InputSize { get; set; }
    public long LatencyMs { get; set; }
    public double Confidence { get; set; }
    public string? Chosen { get; set; }
}

/// <summary>Finished download job (LiteDB collection <c>jobs</c>). There is no Pending row.</summary>
public sealed class JobRecord
{
    [BsonId] public int Id { get; set; }

    public string DownloadId { get; set; } = "";
    public int QueueId { get; set; }
    public string? Title { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime FinishedUtc { get; set; }

    public string Status { get; set; } = "";   // Finished | Failed
    public string Outcome { get; set; } = "";  // Imported | Rejected | Skip | LeftToHuman | None

    public int FileCount { get; set; }
    public string? Error { get; set; }
    public long LatencyMs { get; set; }
}

/// <summary>Download-level "done" marker (LiteDB collection <c>done</c>).</summary>
public sealed class DoneRecord
{
    [BsonId] public string DownloadId { get; set; } = "";
    public DateTime DoneUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// True when the marker was written by a dry-run pass. A dry-run marker only suppresses
    /// re-processing while <c>DryRun</c> is still enabled; once dry-run is disabled the download
    /// becomes eligible again so its previewed Import/Reject decisions can be applied.
    /// </summary>
    public bool DryRun { get; set; }
}
