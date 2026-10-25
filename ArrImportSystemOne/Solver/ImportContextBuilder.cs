using ArrImportSystemOne.Lidarr;

namespace ArrImportSystemOne.Solver;

/// <summary>
/// One selectable mapping target offered to the System One decision model as a <c>choice</c> option.
/// Ordered target-release-first so truncation at the 253 cap keeps the most likely tracks
/// (systemOne.md §7 / §15).
/// </summary>
public sealed record Candidate(
    string Key,
    int TrackId,
    int AlbumReleaseId,
    string ReleaseTitle,
    string Title,
    string TrackNumber,
    string Duration,
    bool HasFile)
{
    /// <summary>
    /// Criteria string for this option, e.g.
    /// <c>release: "Album (Deluxe)" | title: "Song" | length: 3:20 | track: 1</c>.
    /// Values are JSON-quoted so apostrophes in titles do not make the criteria ambiguous.
    /// </summary>
    public string ToCriteria()
    {
        var release = string.IsNullOrWhiteSpace(ReleaseTitle)
            ? string.Empty
            : $"release: {Quote(ReleaseTitle)} | ";
        return $"{release}title: {Quote(Title)} | length: {Duration} | track: {TrackNumber}";
    }

    private static string Quote(string value) =>
        $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
}

/// <summary>Tracks for one album release, used to build the candidate list across editions.</summary>
public sealed record ReleaseTracks(int AlbumReleaseId, string? Title, IReadOnlyList<LidarrTrack> Tracks);

public sealed class ImportContextBuilder(ILogger<ImportContextBuilder> logger)
    : IImportContextBuilder
{
    /// <summary>
    /// Two slots are reserved for the non-track outcomes (<c>no_match</c> and <c>album_mismatch</c>),
    /// so at most 253 candidates (3–255 total, Mica's choice limit) are offered.
    /// </summary>
    public const int MaxCandidates = 253;

    public const string NoMatchKey = "no_match";
    public const string AlbumMismatchKey = "album_mismatch";
    public const string MappingQuestionId = "mapping";

    private const string MappingInstructions =
        "Task: match the downloaded audio file to exactly one offered track of the target album, " +
        "or choose the outcome that says why it cannot be matched. " +
        "Use file_tags first (embedded title, album, track numbers, disc and duration), then file_name " +
        "and relative_path, and finally the target artist/album. " +
        "The track title is the decisive evidence: choose an option only when its title is the same song " +
        "as the file title. Artist, album and track-number similarity must never turn a different song " +
        "into a match. Use track number and length only to confirm or reject a title match. " +
        "Release-edition words (deluxe, remaster, anniversary, US/UK/Japanese/Chinese edition, bonus) " +
        "do not by themselves make a track a different recording: if the song is offered, choose it. " +
        "Recording-version words (Taylor's Version, re-recorded, live, acoustic, demo, commentary, " +
        "instrumental, karaoke, remix, edit, mono/stereo) identify a different recording: match only an " +
        "option carrying the same qualifier, and never match a re-recording to the original or vice versa. " +
        "Choose 'album_mismatch' when the file is a re-recording or other recording version and no " +
        "option carries the same qualifier, when the album identity is ambiguous, or when the file is " +
        "from the target album but its song is not offered (for example an unlisted bonus or vault " +
        "track). The download is then left for a human instead of being rejected. " +
        "Choose 'no_match' only when the file clearly belongs to a different album (not an edition or " +
        "re-recording of the target) or a different artist. " +
        "Do not guess from a similar-sounding title or from artist/album similarity; if the evidence is " +
        "ambiguous, choose 'album_mismatch'.";

    public IReadOnlyList<Candidate> BuildCandidates(ManualImportFile row)
    {
        // row.Tracks is Lidarr's matched candidate set for this file. It is usually one track and
        // is only a fallback when the release track lists cannot be loaded.
        var seen = new HashSet<int>();
        var candidates = new List<Candidate>();
        var releaseTitle = TargetReleaseTitle(row) ?? row.Album?.Title;

        foreach (var track in row.Tracks)
        {
            if (track.Id <= 0 || !seen.Add(track.Id))
                continue;

            candidates.Add(ToCandidate(track, row.AlbumReleaseId, releaseTitle));
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
            // A manual-import album can expose releases that belong to a different album. Those
            // tracks are never valid targets and make the choice needlessly large, so keep the
            // target release and only same-album editions. Missing titles are kept rather than
            // silently hiding a possible edition.
            if (!IsSameAlbumRelease(row, release))
                continue;

            foreach (var track in release.Tracks)
            {
                if (track.Id <= 0 || !seenIds.Add(track.Id))
                    continue;

                var title = NormalizeTitle(track.Title);
                var key = title.Length > 0 ? title : $"id:{track.Id}";
                if (!seenTracks.Add(key))
                    continue;

                candidates.Add(ToCandidate(track, release.AlbumReleaseId, release.Title));
            }
        }

        return candidates;
    }

    private static bool IsSameAlbumRelease(ManualImportFile row, ReleaseTracks release)
    {
        if (release.AlbumReleaseId == row.AlbumReleaseId)
            return true;

        var album = NormalizeTitle(row.Album?.Title);
        var title = NormalizeTitle(release.Title);
        return album.Length == 0 || title.Length == 0 || title.Contains(album, StringComparison.Ordinal);
    }

    private static string? TargetReleaseTitle(ManualImportFile row) =>
        row.Album?.Releases?.FirstOrDefault(r => r.Id == row.AlbumReleaseId)?.Title;

    internal static string NormalizeTitle(string? title)
    {
        var normalized = (title ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .Replace('\u2019', '\'')
            .Replace('\u2018', '\'');
        return string.Join(' ', normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static Candidate ToCandidate(LidarrTrack track, int albumReleaseId, string? releaseTitle) => new(
        Key: string.Empty, // assigned in BuildQuestions once truncation is known
        TrackId: track.Id,
        AlbumReleaseId: albumReleaseId,
        ReleaseTitle: releaseTitle ?? string.Empty,
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
        var criteria = new Dictionary<string, string?>(take + 2);

        for (var i = 0; i < take; i++)
        {
            var key = $"option_{i}";
            var candidate = candidates[i] with { Key = key };
            chosen.Add(candidate);
            criteria[key] = candidate.ToCriteria();
        }

        criteria[NoMatchKey] = "the file clearly belongs to a different album or artist, not an edition of the target album";
        criteria[AlbumMismatchKey] = "the file is a different edition/re-recording of the target album, the album is ambiguous, or the song is not offered; leave for human review";

        offered = chosen;

        var question = new SystemOneQuestion
        {
            Type = "choice",
            Instructions = MappingInstructions,
            Criteria = criteria
        };

        // Validate the hard 3–255 limit: at least one candidate plus the two reserved outcomes.
        var optionCount = criteria.Count;
        if (optionCount < 3 || optionCount > 255)
            throw new InvalidOperationException($"Invalid System One option count {optionCount} for row {row.Id}");

        return question;
    }

    public object BuildModelState(LidarrQueueItem queueItem, ManualImportFile row)
    {
        // Lidarr's matched row tracks, not the full option list: every option is already in the
        // mapping criteria, and duplicating them here only doubles the prompt size. The embedded
        // tags are the file's own evidence and are deliberately kept separate from Lidarr's match.
        return new
        {
            file_name = row.Name,
            relative_path = row.Path,
            artist = row.Artist?.ArtistName,
            album = row.Album?.Title,
            album_release_id = row.AlbumReleaseId,
            target_release = TargetReleaseTitle(row),
            quality = row.Quality?.Quality?.Name,
            rejections = row.Rejections.Select(r => r.Reason).ToArray(),
            file_tags = BuildFileTags(row.AudioTags),
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

    private static object? BuildFileTags(ParsedTrackInfo? tags)
    {
        if (tags is null)
            return null;

        var duration = tags.Duration > TimeSpan.Zero
            ? FormatDuration((int)tags.Duration.TotalMilliseconds)
            : null;

        return new
        {
            title = tags.Title,
            artist = tags.ArtistTitle,
            album = tags.AlbumTitle,
            release_group = tags.ReleaseGroup,
            disambiguation = tags.Disambiguation,
            track_numbers = tags.TrackNumbers,
            disc_number = tags.DiscNumber,
            year = tags.Year == 0 ? (int?)null : tags.Year,
            duration
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
