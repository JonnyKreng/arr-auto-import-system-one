namespace ArrImportSystemOne.Solver;

/// <summary>
/// Thrown when the System One decision provider is unavailable. The worker pauses and retries the
/// same download after the provider recovers; it must not write leave-to-human decisions or a done
/// marker for this failure.
/// </summary>
public sealed class SystemOneUnavailableException(string message) : Exception(message);
