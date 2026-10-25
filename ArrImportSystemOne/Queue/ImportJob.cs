namespace ArrImportSystemOne.Queue;

/// <summary>
/// A unit of work is a whole download, not a file: Lidarr's import is a single ManualImport command
/// and reject+blocklist is a single queue delete, so per-download jobs keep those actions atomic
/// (systemOne.md §3 / §5.1).
/// </summary>
public sealed record ImportJob(
    string DownloadId,
    int QueueId,
    string? Title,
    DateTime DiscoveredUtc);
