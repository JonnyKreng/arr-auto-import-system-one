namespace ArrImportSystemOne.Options;

/// <summary>
/// Root options for the Lidarr integration. The section name stays "Lidarr" (see systemOne.md §10):
/// the Lidarr client options are untouched and the System One/Model settings live underneath it.
/// </summary>
public class LidarrOptions
{
    public const string SectionName = "Lidarr";

    public string Url { get; set; } = "http://localhost:8686";
    public string Key { get; set; } = "";
    public bool DryRun { get; set; } = true;
    public int PollIntervalSeconds { get; set; } = 60;

    /// <summary>Connection settings for the current System One decision provider (Mica today).</summary>
    public SystemOneOptions SystemOne { get; set; } = new();

    public ModelOptions Model { get; set; } = new();
}

public class SystemOneOptions
{
    public string Url { get; set; } = "http://localhost:8010";
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>How often the provider's <c>/health</c> endpoint is probed.</summary>
    public int HealthCheckSeconds { get; set; } = 15;
}

public class ModelOptions
{
    /// <summary>
    /// Minimum System One choice confidence to act on a decision; below this the file is left to a
    /// human.
    /// </summary>
    public double MinConfidence { get; set; } = 0.75;
}
