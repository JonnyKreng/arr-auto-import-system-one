using System.Text.Json;
using ArrImportSystemOne.Store;

namespace ArrImportSystemOne.Hub;

/// <summary>Row shape used by the decisions table and the <c>decision.resolved</c> broadcast.</summary>
public sealed record DecisionView(
    int Id,
    string DownloadId,
    int QueueId,
    string? FileName,
    string? FilePath,
    string Resolver,
    string Action,
    string Status,
    string? Chosen,
    double Confidence,
    long LatencyMs,
    string? Error,
    DateTime UtcTimestamp)
{
    public static DecisionView From(DecisionRecord d) => new(
        d.Id, d.DownloadId, d.QueueId, d.FileName, d.FilePath, d.Resolver, d.Action, d.Status,
        d.Chosen, d.Confidence, d.LatencyMs, d.Error, d.UtcTimestamp);
}

/// <summary>Payload of <c>GetSnapshot</c> (systemOne.md §9.1).</summary>
public sealed record SnapshotDto(
    IReadOnlyList<DecisionView> Decisions,
    IReadOnlyList<JobRecord> Jobs,
    Queue.QueueUpdated Queue);

/// <summary>Payload of <c>GetDecisionDetail</c> (the info button).</summary>
public sealed record DecisionDetailDto(
    DecisionView Decision,
    SystemOneRequestDto? Request,
    JsonElement? Result);

public sealed record SystemOneRequestDto(JsonElement? State, JsonElement? Questions);
