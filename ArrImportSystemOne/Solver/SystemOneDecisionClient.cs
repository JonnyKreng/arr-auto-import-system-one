using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ArrImportSystemOne.Options;
using Microsoft.Extensions.Options;

namespace ArrImportSystemOne.Solver;

/// <summary>
/// HTTP implementation of the System One decision protocol (<c>POST /v1/systemone</c>).
/// Mica is the current provider; the class name stays provider-neutral so a future model can be
/// swapped by registering a different <see cref="ISystemOneDecisionClient"/>.
///
/// The provider returns probabilities, never text — there is no streaming, no JSON repair, no
/// retries (systemOne.md §8 / §12).
/// </summary>
public sealed class SystemOneDecisionClient(
    HttpClient http,
    IOptionsMonitor<LidarrOptions> options,
    ILogger<SystemOneDecisionClient> logger) : ISystemOneDecisionClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<SystemOneHealthResult> CheckHealthAsync(CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var response = await http.GetAsync("health", timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new SystemOneHealthResult(false, $"health check returned HTTP {(int)response.StatusCode}");

            var health = await response.Content
                .ReadFromJsonAsync<SystemOneHealthResponse>(Json, timeout.Token)
                .ConfigureAwait(false);

            if (health?.Status is { Length: > 0 } status
                && !string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
                return new SystemOneHealthResult(false, $"model status is '{status}'");

            // Mica captures CUDA graphs after loading; decisions during warmup are unsafe.
            if (health?.Warming == true)
                return new SystemOneHealthResult(false, "model is warming up");

            return new SystemOneHealthResult(true, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new SystemOneHealthResult(false, "health check timed out");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // A provider may expose a non-JSON /health endpoint. HTTP success is enough.
            return new SystemOneHealthResult(true, null);
        }
        catch (Exception ex)
        {
            return new SystemOneHealthResult(false, ex.Message);
        }
    }

    public async Task<SystemOneDecisionResponse?> DecideAsync(
        SystemOneDecisionRequest request,
        CancellationToken ct)
    {
        var settings = options.CurrentValue.SystemOne;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));

        try
        {
            // Buffer the JSON and send Content-Length. Mica's minimal HTTP server reads
            // Content-Length only; a chunked body is seen as empty ("request.state is required").
            var json = JsonSerializer.Serialize(request, Json);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await http
                .PostAsync("v1/systemone", content, timeout.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                var kind = IsInvalidRequest(response.StatusCode)
                    ? SystemOneErrorKind.InvalidRequest
                    : SystemOneErrorKind.Unavailable;

                logger.LogWarning(
                    "System One decision provider returned {Status} ({Kind}): {Body}",
                    response.StatusCode, kind, body);

                return new SystemOneDecisionResponse
                {
                    Error = $"HTTP {(int)response.StatusCode}: {body}",
                    ErrorKind = kind
                };
            }

            var result = await response.Content
                .ReadFromJsonAsync<SystemOneDecisionResponse>(Json, timeout.Token)
                .ConfigureAwait(false);

            if (result is null)
                return new SystemOneDecisionResponse
                {
                    Error = "Empty System One response",
                    ErrorKind = SystemOneErrorKind.InvalidResponse
                };

            result.Answers ??= new Dictionary<string, SystemOneAnswer>();

            // A choice that was never offered must never be mapped to an arbitrary candidate.
            var answer = result.Answers.GetValueOrDefault(ImportContextBuilder.MappingQuestionId);
            if (answer?.Choice is { } choice
                && request.Questions.TryGetValue(ImportContextBuilder.MappingQuestionId, out var question)
                && question.Criteria is { } criteria
                && !criteria.ContainsKey(choice))
            {
                return new SystemOneDecisionResponse
                {
                    Error = $"System One returned unknown choice '{choice}'",
                    ErrorKind = SystemOneErrorKind.InvalidResponse
                };
            }

            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("System One request timed out after {Timeout}s", settings.TimeoutSeconds);
            return new SystemOneDecisionResponse
            {
                Error = "System One request timed out",
                ErrorKind = SystemOneErrorKind.Unavailable
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            logger.LogWarning(ex, "System One request failed");
            return new SystemOneDecisionResponse
            {
                Error = $"Transport error: {ex.Message}",
                ErrorKind = SystemOneErrorKind.Unavailable
            };
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            logger.LogWarning(ex, "System One response could not be parsed");
            return new SystemOneDecisionResponse
            {
                Error = $"Parse error: {ex.Message}",
                ErrorKind = SystemOneErrorKind.InvalidResponse
            };
        }
    }

    private static bool IsInvalidRequest(HttpStatusCode status) =>
        status is HttpStatusCode.BadRequest
            or HttpStatusCode.RequestEntityTooLarge
            or HttpStatusCode.UnprocessableEntity;
}
