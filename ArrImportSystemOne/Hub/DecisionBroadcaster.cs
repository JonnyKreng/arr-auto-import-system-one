using ArrImportSystemOne.Store;
using Microsoft.AspNetCore.SignalR;

namespace ArrImportSystemOne.Hub;

/// <summary>
/// Typed façade over <see cref="IHubContext{THub}"/> so background services never depend on the hub
/// class itself. Broadcasts are fire-and-forget (systemOne.md §9.2).
/// </summary>
public interface IDecisionBroadcaster
{
    Task QueueUpdatedAsync(Queue.QueueUpdated payload, CancellationToken ct = default);
    Task JobFinishedAsync(JobRecord job, CancellationToken ct = default);
    Task DecisionResolvedAsync(DecisionView decision, CancellationToken ct = default);
}

public sealed class DecisionBroadcaster(IHubContext<DecisionHub> hub) : IDecisionBroadcaster
{
    public Task QueueUpdatedAsync(Queue.QueueUpdated payload, CancellationToken ct = default) =>
        hub.Clients.All.SendAsync("queue.updated", payload, ct);

    public Task JobFinishedAsync(JobRecord job, CancellationToken ct = default) =>
        hub.Clients.All.SendAsync("job.finished", job, ct);

    public Task DecisionResolvedAsync(DecisionView decision, CancellationToken ct = default) =>
        hub.Clients.All.SendAsync("decision.resolved", decision, ct);
}
