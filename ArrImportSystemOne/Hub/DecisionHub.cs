using System.Text.Json;
using ArrImportSystemOne.Queue;
using ArrImportSystemOne.Solver;
using ArrImportSystemOne.Store;
using Microsoft.AspNetCore.SignalR;

namespace ArrImportSystemOne.Hub;

/// <summary>
/// The single UI transport: snapshot, decision detail (info button) and restart. There are no
/// <c>/api/*</c> endpoints and no hand-rolled WebSocket (systemOne.md §9).
/// </summary>
public sealed class DecisionHub(
    DecisionRepository repository,
    WorkerProgress progress,
    PollNotifier notifier,
    ILogger<DecisionHub> logger) : Microsoft.AspNetCore.SignalR.Hub
{
    public SnapshotDto GetSnapshot(int decisionLimit = 200)
    {
        var decisions = repository.GetDecisions(decisionLimit).Select(DecisionView.From).ToList();
        var jobs = repository.GetJobs(decisionLimit);
        return new SnapshotDto(decisions, jobs, progress.BuildQueueUpdated());
    }

    public DecisionDetailDto? GetDecisionDetail(int decisionId)
    {
        var decision = repository.GetDecision(decisionId);
        if (decision is null)
            return null;

        var request = new SystemOneRequestDto(Parse(decision.State), Parse(decision.Questions));
        return new DecisionDetailDto(DecisionView.From(decision), request, Parse(decision.ModelAnswer));
    }

    public bool RestartAlbum(string downloadId)
    {
        if (string.IsNullOrWhiteSpace(downloadId))
            return false;

        repository.ClearDone(downloadId);
        var flagged = repository.FlagRestarted(downloadId);
        notifier.Signal();

        logger.LogInformation("Restart requested for download {DownloadId} ({Count} decisions flagged)", downloadId, flagged);
        return true;
    }

    private static JsonElement? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
