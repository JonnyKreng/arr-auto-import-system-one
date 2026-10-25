using System.Net.Http.Json;

namespace ArrImportSystemOne.Lidarr;

/// <summary>
/// Thin Lidarr v1 API client. Kept intentionally small and dependency free (systemOne.md §6.2:
/// the Lidarr client stays as-is). Configured as a typed <see cref="HttpClient"/> with the base
/// address and <c>X-Api-Key</c> header from <see cref="LidarrOptions"/>.
/// </summary>
public sealed class LidarrClient(HttpClient http, ILogger<LidarrClient> logger)
{
    private static readonly string[] ImportableStates = { "failed", "warning", "completed" };

    /// <summary>All queue items Lidarr currently reports (paginated through).</summary>
    public async Task<List<LidarrQueueItem>> GetQueueAsync(CancellationToken ct)
    {
        var all = new List<LidarrQueueItem>();
        var page = 1;
        const int pageSize = 200;

        while (true)
        {
            var url = $"api/v1/queue?page={page}&pageSize={pageSize}&includeUnknownArtistItems=true";
            var response = await http.GetFromJsonAsync<LidarrQueueResponse>(url, ct).ConfigureAwait(false);
            if (response?.Records is null || response.Records.Count == 0)
                break;

            all.AddRange(response.Records);

            if (response.Records.Count < pageSize)
                break;
            page++;
        }

        return all;
    }

    /// <summary>Manual-import rows for one download (fresh read; the worker re-reads before deciding).</summary>
    public async Task<List<ManualImportFile>> GetManualImportAsync(string downloadId, CancellationToken ct)
    {
        var url = $"api/v1/manualimport?downloadId={Uri.EscapeDataString(downloadId)}&filterExistingFiles=false";
        try
        {
            return await http.GetFromJsonAsync<List<ManualImportFile>>(url, ct).ConfigureAwait(false)
                   ?? new List<ManualImportFile>();
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Manual import lookup failed for download {DownloadId}", downloadId);
            throw;
        }
    }

    /// <summary>
    /// Tracks belonging to one album release. The manual-import row only carries Lidarr's matched
    /// track, so candidates come from the release's full track list instead.
    /// </summary>
    public async Task<IReadOnlyList<LidarrTrack>> GetReleaseTracksAsync(int albumReleaseId, CancellationToken ct)
    {
        var url = $"api/v1/track?albumReleaseId={albumReleaseId}";
        try
        {
            return await http.GetFromJsonAsync<List<LidarrTrack>>(url, ct).ConfigureAwait(false)
                   ?? new List<LidarrTrack>();
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Release track lookup failed for album release {AlbumReleaseId}", albumReleaseId);
            throw;
        }
    }

    /// <summary>Submits a single ManualImport command carrying all chosen files of a download.</summary>
    public async Task<bool> ImportAsync(IReadOnlyList<ManualImportUpdate> files, CancellationToken ct)
    {
        if (files.Count == 0)
            return true;

        var command = new ManualImportCommand { Files = files.ToList() };
        var response = await http.PostAsJsonAsync("api/v1/command", command, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            logger.LogWarning("ManualImport command failed ({Status}): {Body}", response.StatusCode, body);
            return false;
        }

        return true;
    }

    /// <summary>Deletes a queue item. Used to reject+blocklist (removeFromClient: true, blocklist: true).</summary>
    public async Task<bool> DeleteQueueItemAsync(int queueId, bool removeFromClient, bool blocklist, CancellationToken ct)
    {
        var url = $"api/v1/queue/{queueId}?removeFromClient={removeFromClient.ToString().ToLowerInvariant()}" +
                  $"&blocklist={blocklist.ToString().ToLowerInvariant()}";
        var response = await http.DeleteAsync(url, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            logger.LogWarning("Queue delete {QueueId} failed ({Status}): {Body}", queueId, response.StatusCode, body);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Cleans up a queue item that is no longer being downloaded and has no importable files.
    /// Removes it from the client without blocklisting (existing stale-cleanup behaviour).
    /// </summary>
    public async Task<bool> RemoveStaleQueueItemAsync(int queueId, CancellationToken ct)
    {
        try
        {
            return await DeleteQueueItemAsync(queueId, removeFromClient: true, blocklist: false, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Stale queue cleanup failed for {QueueId}", queueId);
            return false;
        }
    }

    internal static bool IsImportableState(string? state) =>
        state is not null && ImportableStates.Contains(state, StringComparer.OrdinalIgnoreCase);
}
