namespace ArrImportSystemOne.Lidarr;

/// <summary>Response payload of <c>GET /api/v1/queue</c>.</summary>
public sealed class LidarrQueueResponse
{
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalRecords { get; set; }
    public List<LidarrQueueItem> Records { get; set; } = new();
}

public sealed class LidarrQueueItem
{
    public int Id { get; set; }
    public string? Title { get; set; }
    public string? DownloadId { get; set; }
    public string? Status { get; set; }
    public string? TrackedDownloadStatus { get; set; }
    public string? TrackedDownloadState { get; set; }
    public List<LidarrQueueStatusMessage>? StatusMessages { get; set; }

    /// <summary>True while Lidarr is still downloading / auto-importing this queue item.</summary>
    public bool IsDownloading =>
        string.Equals(TrackedDownloadState, "downloading", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(TrackedDownloadState, "importing", StringComparison.OrdinalIgnoreCase);
}

public sealed class LidarrQueueStatusMessage
{
    public string? Title { get; set; }
    public List<string>? Messages { get; set; }
}

/// <summary>One row of <c>GET /api/v1/manualimport?downloadId=…</c>.</summary>
public sealed class ManualImportFile
{
    public int Id { get; set; }
    public string? Path { get; set; }
    public string? Name { get; set; }
    public string? FolderName { get; set; }
    public long Size { get; set; }
    public string? DownloadId { get; set; }
    public bool AdditionalFile { get; set; }
    public LidarrArtist? Artist { get; set; }
    public LidarrAlbum? Album { get; set; }
    public List<AlbumRelease>? AlbumReleases { get; set; }
    public int AlbumReleaseId { get; set; }
    public QualityModel? Quality { get; set; }
    public List<Rejection> Rejections { get; set; } = new();
    public List<LidarrTrack> Tracks { get; set; } = new();

    /// <summary>Tags Lidarr read from the file itself (manual-import <c>audioTags</c>).</summary>
    public ParsedTrackInfo? AudioTags { get; set; }
}

public sealed class LidarrArtist
{
    public int Id { get; set; }
    public string? ArtistName { get; set; }
}

public sealed class LidarrAlbum
{
    public int Id { get; set; }
    public string? Title { get; set; }
    public string? ReleaseDate { get; set; }
    public List<AlbumRelease>? Releases { get; set; }
}

/// <summary>One release/edition of an album as returned by Lidarr's manual-import payload.</summary>
public sealed class AlbumRelease
{
    public int? Id { get; set; }
    public string? Title { get; set; }
    public string? ReleaseDate { get; set; }
    public int TrackCount { get; set; }
}

public sealed class LidarrTrack
{
    public int Id { get; set; }
    public string? Title { get; set; }
    public string? TrackNumber { get; set; }
    public int Duration { get; set; } // milliseconds
    public bool HasFile { get; set; }
}

/// <summary>
/// The subset of Lidarr's <c>ParsedTrackInfo</c> that helps identify a file: its embedded
/// title/artist/album, track numbers, disc and duration. Optional throughout so old Lidarr
/// versions or files without tags still produce a request.
/// </summary>
public sealed class ParsedTrackInfo
{
    public string? Title { get; set; }
    public string? ArtistTitle { get; set; }
    public string? AlbumTitle { get; set; }
    public string? ReleaseGroup { get; set; }
    public string? Disambiguation { get; set; }
    public TimeSpan Duration { get; set; }
    public int[] TrackNumbers { get; set; } = Array.Empty<int>();
    public int DiscNumber { get; set; }
    public int DiscCount { get; set; }
    public int Year { get; set; }
}

public sealed class Rejection
{
    public string? Reason { get; set; }
    public string? Type { get; set; }
}

public sealed class QualityModel
{
    public Quality Quality { get; set; } = new();
    public Revision Revision { get; set; } = new();
}

public sealed class Quality
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? Source { get; set; }
    public string? Modifier { get; set; }
}

public sealed class Revision
{
    public int Version { get; set; }
    public int Real { get; set; }
    public bool IsRepack { get; set; }
}

/// <summary>Body of <c>POST /api/v1/command</c> for a manual import.</summary>
public sealed class ManualImportCommand
{
    public string Name { get; set; } = "ManualImport";
    public string ImportMode { get; set; } = "auto";
    public List<ManualImportUpdate> Files { get; set; } = new();
}

/// <summary>One entry of the <c>ManualImport</c> command's <c>files</c> array.</summary>
public sealed class ManualImportUpdate
{
    public string? Path { get; set; }
    public int ArtistId { get; set; }
    public int AlbumId { get; set; }
    public int AlbumReleaseId { get; set; }
    public List<int> TrackIds { get; set; } = new();
    public QualityModel? Quality { get; set; }
}
