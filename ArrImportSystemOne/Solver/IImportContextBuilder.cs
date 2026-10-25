using ArrImportSystemOne.Lidarr;

namespace ArrImportSystemOne.Solver;

/// <summary>
/// Builds the System One request pieces (candidates, questions, state) and the Lidarr import update.
/// This is the former <c>ImportDecisionEngine</c> with all deterministic decision logic removed
/// (systemOne.md §6.2 / §14.2).
/// </summary>
public interface IImportContextBuilder
{
    /// <summary>Candidate tracks from Lidarr's matched row, deduped.</summary>
    IReadOnlyList<Candidate> BuildCandidates(ManualImportFile row);

    /// <summary>
    /// Candidate tracks from the album's full release track lists. Releases must already be ordered
    /// target-release-first; duplicates are removed so each logical track appears once.
    /// </summary>
    IReadOnlyList<Candidate> BuildCandidates(ManualImportFile row, IReadOnlyList<ReleaseTracks> releases);

    /// <summary>
    /// Builds the single <c>mapping</c> choice question. Enforces the option cap: at most
    /// <see cref="ImportContextBuilder.MaxCandidates"/> candidates plus the reserved
    /// <c>no_match</c> and <c>album_mismatch</c> outcomes (systemOne.md §7).
    /// </summary>
    SystemOneQuestion BuildQuestions(ManualImportFile row, IReadOnlyList<Candidate> candidates, out IReadOnlyList<Candidate> offered);

    /// <summary>The shared System One <c>state</c> object for a row.</summary>
    object BuildModelState(LidarrQueueItem queueItem, ManualImportFile row);

    /// <summary>Maps a chosen candidate to a Lidarr manual-import update.</summary>
    ManualImportUpdate ToImportUpdate(ManualImportFile row, Candidate candidate);
}
