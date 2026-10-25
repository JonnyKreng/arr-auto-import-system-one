namespace ArrImportSystemOne.Solver;

/// <summary>
/// Provider-agnostic client for System One decisions. The worker only depends on this interface, so
/// swapping Mica for a better model/provider later means registering another implementation — no
/// pipeline changes. The default implementation speaks Mica's <c>POST /v1/systemone</c> protocol.
/// </summary>
public interface ISystemOneDecisionClient
{
    /// <summary>
    /// Sends a decision request. Failures are returned in <see cref="SystemOneDecisionResponse.Error"/>
    /// with an <see cref="SystemOneDecisionResponse.ErrorKind"/>; cancellation is the only exception
    /// that escapes.
    /// </summary>
    Task<SystemOneDecisionResponse?> DecideAsync(SystemOneDecisionRequest request, CancellationToken ct);

    /// <summary>Checks whether the provider can accept decisions right now.</summary>
    Task<SystemOneHealthResult> CheckHealthAsync(CancellationToken ct);
}
