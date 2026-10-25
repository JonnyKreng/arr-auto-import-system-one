using ArrImportSystemOne.Lidarr;

namespace ArrImportSystemOne.Solver;

/// <summary>
/// One selectable mapping target offered to the System One decision model as a <c>choice</c> option.
/// Ordered target-release-first so truncation at the 254 cap keeps the most likely tracks
/// (systemOne.md §7 / §15).
/// </summary>
public sealed record Candidate(
    string Key,
    int TrackId,
    int AlbumReleaseId,
    string Title,
    string TrackNumber,
    string Duration,
    bool HasFile)
{
    /// <summary>Criteria string for this option, e.g. <c>title: 'Song' | length: 3:20 | track: 1</c>.</summary>
    public string ToCriteria() =>
        $"title: '{Title}' | length: {Duration} | track: {TrackNumber}";
}

/// <summary>Tracks for one album release, used to build the candidate list across editions.</summary>
public sealed record ReleaseTracks(int AlbumReleaseId, IReadOnlyList<LidarrTrack> Tracks);

public sealed class ImportContextBuilder(ILogger<ImportContextBuilder> logger)
    : IImportContextBuilder
{
    /// <summary>One option slot is reserved for <c>no_match</c>, so at most 254 candidates (2–255 total).</summary>
    public const int MaxCandidates = 254;

    public const string NoMatchKey = "no_match";
    public const string MappingQuestionId = "mapping";

    private const string MappingInstructions =
        "Task: match the downloaded audio file to exactly one track of the target album. " +
        "Use the file name, the album/artist, and the per-option title, length and track number. " +
        "Pick the single best matching option. If none of the listed tracks is plausibly the downloaded " +
        "track, choose 'no_match'.";

    public IReadOnlyList<Candidate> BuildCandidates(ManualImportFile row)
    {
        // row.Tracks is Lidarr's matched candidate set for this file. It is usually one track and
        // is only a fallback when the release track lists cannot be loaded.
        var seen = new HashSet<int>();
        var candidates = new List<Candidate>();

        foreach (var track in row.Tracks)
        {
            if (track.Id <= 0 || !seen.Add(track.Id))
                continue;

            candidates.Add(ToCandidate(track, row.AlbumReleaseId));
        }

        return candidates;
    }

    public IReadOnlyList<Candidate> BuildCandidates(ManualImportFile row, IReadOnlyList<ReleaseTracks> releases)
    {
        // Track IDs are release-specific, so dedupe across editions by normalized title. Durations
        // differ slightly between pressings, and using them would turn one song into many options.
        // The first occurrence wins; callers pass the target release first.
        var seenIds = new HashSet<int>();
        var seenTracks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<Candidate>();

        foreach (var release in releases)
        {
            foreach (var track in release.Tracks)
            {
                if (track.Id <= 0 || !seenIds.Add(track.Id))
                    continue;

                var title = NormalizeTitle(track.Title);
                var key = title.Length > 0 ? title : $"id:{track.Id}";
                if (!seenTracks.Add(key))
                    continue;

                candidates.Add(ToCandidate(track, release.AlbumReleaseId));
            }
        }

        return candidates;
    }

    private static string NormalizeTitle(string? title)
    {
        var normalized = (title ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .Replace('\u2019', '\'')
            .Replace('\u2018', '\'');
        return string.Join(' ', normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static Candidate ToCandidate(LidarrTrack track, int albumReleaseId) => new(
        Key: string.Empty, // assigned in BuildQuestions once truncation is known
        TrackId: track.Id,
        AlbumReleaseId: albumReleaseId,
        Title: track.Title ?? string.Empty,
        TrackNumber: track.TrackNumber ?? string.Empty,
        Duration: FormatDuration(track.Duration),
        HasFile: track.HasFile);

    public SystemOneQuestion BuildQuestions(
        ManualImportFile row,
        IReadOnlyList<Candidate> candidates,
        out IReadOnlyList<Candidate> offered)
    {
        var take = Math.Min(candidates.Count, MaxCandidates);
        if (candidates.Count > MaxCandidates)
        {
            logger.LogWarning(
                "Candidate cap hit for download {DownloadId} row {RowId}: {Total} candidates, {Dropped} dropped",
                row.DownloadId, row.Id, candidates.Count, candidates.Count - MaxCandidates);
        }

        var chosen = new List<Candidate>(take);
        var criteria = new Dictionary<string, string?>(take + 1);

        for (var i = 0; i < take; i++)
        {
            var key = $"option_{i}";
            var candidate = candidates[i] with { Key = key };
            chosen.Add(candidate);
            criteria[key] = candidate.ToCriteria();
        }

        criteria[NoMatchKey] = "none of the listed tracks is the downloaded track";

        offered = chosen;

        var question = new SystemOneQuestion
        {
            Type = "choice",
            Instructions = MappingInstructions,
            Criteria = criteria
        };

        // Validate the hard 2–255 limit. Callers must never invoke the model for < 2 options.
        var optionCount = criteria.Count;
        if (optionCount < 2 || optionCount > 255)
            throw new InvalidOperationException($"Invalid System One option count {optionCount} for row {row.Id}");

        return question;
    }

    public object BuildModelState(LidarrQueueItem queueItem, ManualImportFile row)
    {
        // Lidarr's matched row tracks, not the full option list: every option is already in the
        // mapping criteria, and duplicating them here only doubles the prompt size.
        return new
        {
            file_name = row.Name,
            relative_path = row.Path,
            artist = row.Artist?.ArtistName,
            album = row.Album?.Title,
            album_release_id = row.AlbumReleaseId,
            quality = row.Quality?.Quality?.Name,
            rejections = row.Rejections.Select(r => r.Reason).ToArray(),
            tracks = row.Tracks.Select(t => new
            {
                title = t.Title,
                number = t.TrackNumber,
                duration = FormatDuration(t.Duration),
                has_file = t.HasFile
            }).ToArray(),
            download_name = queueItem.Title,
            download_title = queueItem.Title,
            queue_status = queueItem.Status
        };
    }

    public ManualImportUpdate ToImportUpdate(ManualImportFile row, Candidate candidate)
    {
        return new ManualImportUpdate
        {
            Path = row.Path,
            ArtistId = row.Artist?.Id ?? 0,
            AlbumId = row.Album?.Id ?? 0,
            AlbumReleaseId = candidate.AlbumReleaseId,
            TrackIds = new List<int> { candidate.TrackId },
            Quality = row.Quality
        };
    }

    /// <summary>Formats an ms duration as <c>m:ss</c> / <c>h:mm:ss</c> to match the state example.</summary>
    public static string FormatDuration(int milliseconds)
    {
        if (milliseconds <= 0)
            return "0:00";

        var span = TimeSpan.FromMilliseconds(milliseconds);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes}:{span.Seconds:00}";
    }

    /// <summary>Whether a queue item's state means Lidarr is still working on the download.</summary>
    public static bool IsStillDownloading(LidarrQueueItem item) => item.IsDownloading;

    /// <summary>
    /// Stable per-file key. Lidarr manual-import rows are transient and may arrive with
    /// <c>id = 0</c>; fall back to a deterministic path hash so the per-row re-decision guard
    /// still works and survives restarts.
    /// </summary>
    public static int RowKey(ManualImportFile row)
    {
        if (row.Id > 0)
            return row.Id;

        var text = row.Path ?? row.Name ?? string.Empty;
        uint hash = 2166136261; // FNV-1a
        foreach (var c in text)
        {
            hash ^= c;
            hash *= 16777619;
        }

        return (int)(hash & 0x7FFFFFFF);
    }
}
