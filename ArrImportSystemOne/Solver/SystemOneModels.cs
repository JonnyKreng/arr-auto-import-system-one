using System.Text.Json.Serialization;

namespace ArrImportSystemOne.Solver;

/// <summary>
/// Request body for the System One decision protocol (<c>POST /v1/systemone</c>). The protocol is
/// provider-agnostic; Mica is the first provider that implements it.
/// </summary>
public sealed class SystemOneDecisionRequest
{
    public object? State { get; set; }
    public Dictionary<string, SystemOneQuestion> Questions { get; set; } = new();
}

public sealed class SystemOneQuestion
{
    public string Type { get; set; } = "choice";
    public string? Instructions { get; set; }

    /// <summary>choice: option key → description. noul/score variants are intentionally omitted (systemOne.md §12).</summary>
    public Dictionary<string, string?>? Criteria { get; set; }

    // Mica rejects null for these optionals: they may only be present with their documented type
    // (and only on the relevant question kind), so omit them unless explicitly set.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Multi { get; set; }

    [JsonPropertyName("allow_cannot_grade")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AllowCannotGrade { get; set; }

    [JsonPropertyName("abstain_threshold")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? AbstainThreshold { get; set; }
}

public sealed class SystemOneDecisionResponse
{
    public string? Model { get; set; }
    public Dictionary<string, SystemOneAnswer> Answers { get; set; } = new();
    public SystemOneUsage? Usage { get; set; }

    [JsonPropertyName("latency_ms")]
    public double LatencyMs { get; set; }

    /// <summary>Client-side field: transport / HTTP / parse failure. Null on success.</summary>
    [JsonIgnore]
    public string? Error { get; set; }

    /// <summary>
    /// Client-side classification of <see cref="Error"/>. <see cref="SystemOneErrorKind.Unavailable"/>
    /// pauses decision processing; the other failures are recorded as leave-to-human.
    /// </summary>
    [JsonIgnore]
    public SystemOneErrorKind ErrorKind { get; set; }
}

public sealed class SystemOneAnswer
{
    public string Type { get; set; } = "";
    public string? Choice { get; set; }
    public double Confidence { get; set; }
    public Dictionary<string, double>? Probabilities { get; set; }
    public bool? Noul { get; set; }
}

public sealed class SystemOneUsage
{
    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; set; }

    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; set; }
}

/// <summary>Why a System One decision call failed.</summary>
public enum SystemOneErrorKind
{
    None = 0,

    /// <summary>Transport failure, timeout, 429, or 5xx — pause and retry later.</summary>
    Unavailable,

    /// <summary>The request itself was rejected (400/413/422) — leave the row to a human.</summary>
    InvalidRequest,

    /// <summary>The provider answered with something we cannot safely use — leave the row to a human.</summary>
    InvalidResponse
}

/// <summary>Result of a provider health probe.</summary>
public sealed record SystemOneHealthResult(bool Healthy, string? Detail);

/// <summary>Minimal shape shared by System One health endpoints (Mica reports <c>warming</c>).</summary>
public sealed class SystemOneHealthResponse
{
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("warming")]
    public bool? Warming { get; set; }
}
