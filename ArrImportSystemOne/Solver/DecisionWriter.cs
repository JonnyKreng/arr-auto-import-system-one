using ArrImportSystemOne.Hub;
using ArrImportSystemOne.Store;

namespace ArrImportSystemOne.Solver;

/// <summary>
/// Persists a decision and pushes <c>decision.resolved</c> to the UI. Kept separate so both the
/// poller (system skips) and the worker (System One decisions) write through one path.
/// </summary>
public sealed class DecisionWriter(DecisionRepository repository, IDecisionBroadcaster broadcaster)
{
    public async Task<DecisionRecord> WriteAsync(DecisionRecord record, CancellationToken ct)
    {
        record.UtcTimestamp = DateTime.UtcNow;
        record.Id = repository.InsertDecision(record);
        await broadcaster.DecisionResolvedAsync(DecisionView.From(record), ct).ConfigureAwait(false);
        return record;
    }
}
