using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ArrImportSystemOne.Hub;
using ArrImportSystemOne.Lidarr;
using ArrImportSystemOne.Options;
using ArrImportSystemOne.Solver;
using ArrImportSystemOne.Store;
using Microsoft.Extensions.Options;

namespace ArrImportSystemOne.Queue;

/// <summary>
/// The single reader of <see cref="JobQueue"/> and the only component that calls the System One
/// decision provider and Lidarr's mutating endpoints. Draining one job at a time keeps a single GPU
/// from being oversubscribed (systemOne.md §3 / §6).
///
/// When the provider is unavailable, the worker pauses before dequeuing and retries an in-flight
/// download once the provider recovers. A provider outage never writes leave-to-human decisions or
/// marks the download done.
/// </summary>
public sealed class JobWorker(
    LidarrClient lidarr,
    ISystemOneDecisionClient systemOne,
    IImportContextBuilder builder,
    DecisionRepository repository,
    JobQueue jobs,
    DecisionWriter writer,
    WorkerProgress progress,
    SystemOneAvailability availability,
    IDecisionBroadcaster broadcaster,
    IOptionsMonitor<LidarrOptions> options,
    ILogger<JobWorker> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Job worker started (dry run: {DryRun}, min confidence: {MinConfidence:P0})",
            options.CurrentValue.DryRun,
            options.CurrentValue.Model.MinConfidence);

        while (!stoppingToken.IsCancellationRequested)
        {
            ImportJob job;
            try
            {
                // Do not dequeue new work while the decision provider is down. Pending jobs stay
                // visible in the queue and are picked up as soon as the gate reopens.
                await availability.WaitUntilAvailableAsync(stoppingToken).ConfigureAwait(false);
                job = await jobs.DequeueAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                progress.ReportJobStarted(job);
                PublishProgress();
                await broadcaster.QueueUpdatedAsync(progress.BuildQueueUpdated(), stoppingToken).ConfigureAwait(false);

                // Retry the same download in place after a provider outage. ProcessJobAsync performs
                // every model call before it applies any Lidarr action, so a retry cannot duplicate
                // imports/rejects; no decisions are persisted before the batch actions.
                while (!stoppingToken.IsCancellationRequested)
                {
                    await availability.WaitUntilAvailableAsync(stoppingToken).ConfigureAwait(false);

                    try
                    {
                        await ProcessJobAsync(job, stoppingToken).ConfigureAwait(false);
                        break;
                    }
                    catch (SystemOneUnavailableException ex)
                    {
                        availability.ReportUnavailable(ex.Message);
                        logger.LogWarning(
                            "System One provider unavailable while processing {DownloadId}; pausing and retrying. {Reason}",
                            job.DownloadId, ex.Message);

                        try
                        {
                            await broadcaster
                                .QueueUpdatedAsync(progress.BuildQueueUpdated(), stoppingToken)
                                .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception broadcastEx)
                        {
                            // Never fail a download because a progress push failed.
                            logger.LogDebug(broadcastEx, "queue.updated broadcast failed");
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Job {DownloadId} failed", job.DownloadId);
                await FinishUnexpectedFailureAsync(job, ex, stoppingToken).ConfigureAwait(false);
            }
            finally
            {
                jobs.Complete(job.DownloadId);
                progress.ReportJobFinished();
                PublishProgress();
                try
                {
                    await broadcaster.QueueUpdatedAsync(progress.BuildQueueUpdated(), CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "queue.updated broadcast failed");
                }
            }
        }
    }

    internal async Task ProcessJobAsync(ImportJob job, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var settings = options.CurrentValue;
        var dryRun = settings.DryRun;
        var minConfidence = settings.Model.MinConfidence;

        List<ManualImportFile> rows;
        try
        {
            rows = await lidarr.GetManualImportAsync(job.DownloadId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await FinishJobAsync(job, "Failed", "None", 0, ex.Message, stopwatch.ElapsedMilliseconds,
                markDone: false, dryRunDone: false, ct).ConfigureAwait(false);
            return;
        }

        if (rows.Count == 0)
        {
            // Empty + still downloading → drop; the poller re-discovers it later. Empty + stale is
            // cleaned up by the poller. Nothing is marked done here (systemOne.md §6).
            logger.LogInformation("Job {DownloadId} has no manual-import rows; dropping", job.DownloadId);
            return;
        }

        if (repository.IsDownloadDone(job.DownloadId, dryRun))
            return;

        var queueItem = new LidarrQueueItem
        {
            Id = job.QueueId,
            Title = job.Title,
            DownloadId = job.DownloadId,
            Status = "completed"
        };

        var plans = new List<DecisionPlan>();
        var importUpdates = new List<ManualImportUpdate>();
        var importPlans = new List<DecisionPlan>();
        var rejectPlans = new List<DecisionPlan>();
        var releaseTracks = new Dictionary<int, IReadOnlyList<LidarrTrack>>();
        var leftToHuman = false;
        var decisionFailed = false;

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            var rowId = ImportContextBuilder.RowKey(row);
            progress.ReportCurrentFile(row.Name);

            if (row.AdditionalFile)
            {
                plans.Add(new DecisionPlan(SkipDecision(job, row, rowId), PlanKind.Skip));
                continue;
            }

            // Per-row re-decision guard: already-handled import/reject rows are not re-applied.
            // LeaveToHuman is deliberately excluded so it is retried on a later poll (never marked
            // done). SkippedDryRun only counts while dry-run is still enabled: once it is disabled
            // the row must be re-decided and the action applied.
            var existing = repository.FindLatest(job.DownloadId, rowId);
            var alreadyHandled = existing is not null
                && existing.Action is "Import" or "RejectBlock"
                && (existing.Status is "Done" or "Applied"
                    || (existing.Status == "SkippedDryRun" && dryRun));
            if (alreadyHandled)
                continue;

            var candidates = await BuildCandidatesAsync(row, releaseTracks, ct).ConfigureAwait(false);
            if (candidates.Count == 0)
            {
                var noCandidate = BaseDecision(job, row, rowId);
                noCandidate.Action = "LeaveToHuman";
                noCandidate.Status = dryRun ? "Applied" : "Failed";
                noCandidate.Error = "No candidate tracks";
                plans.Add(new DecisionPlan(noCandidate, PlanKind.LeftToHuman));
                leftToHuman = true;
                continue;
            }

            var question = builder.BuildQuestions(row, candidates, out var offered);
            var state = builder.BuildModelState(queueItem, row);
            var questions = new Dictionary<string, SystemOneQuestion> { [ImportContextBuilder.MappingQuestionId] = question };
            var request = new SystemOneDecisionRequest { State = state, Questions = questions };

            var stateJson = Serialize(state);
            var questionsJson = Serialize(questions);
            var candidatesJson = Serialize(offered);

            var decision = BaseDecision(job, row, rowId);
            decision.State = stateJson;
            decision.Questions = questionsJson;
            decision.Candidates = candidatesJson;
            decision.InputSize = Encoding.UTF8.GetByteCount(stateJson ?? "") + Encoding.UTF8.GetByteCount(questionsJson ?? "");

            var response = await systemOne.DecideAsync(request, ct).ConfigureAwait(false);
            decision.LatencyMs = response is null ? 0 : (long)Math.Round(response.LatencyMs);
            decision.ModelAnswer = response is null || response.Error is not null
                ? null
                : Serialize(new
                {
                    choice = response.Answers.GetValueOrDefault(ImportContextBuilder.MappingQuestionId)?.Choice,
                    confidence = response.Answers.GetValueOrDefault(ImportContextBuilder.MappingQuestionId)?.Confidence ?? 0,
                    probabilities = response.Answers.GetValueOrDefault(ImportContextBuilder.MappingQuestionId)?.Probabilities,
                    latencyMs = response.LatencyMs,
                    model = response.Model,
                    usage = response.Usage
                });

            if (response is null || response.Error is not null)
            {
                // A provider outage is not a decision. Pause and retry the whole download later
                // without persisting a leave-to-human row or a done marker.
                if (response?.ErrorKind == SystemOneErrorKind.Unavailable)
                {
                    availability.ReportUnavailable(response.Error);
                    throw new SystemOneUnavailableException(response.Error);
                }

                decision.Action = "LeaveToHuman";
                decision.Status = dryRun ? "Applied" : "Failed";
                decision.Error = response?.Error ?? "No System One decision response";
                plans.Add(new DecisionPlan(decision, PlanKind.LeftToHuman));
                leftToHuman = true;
                decisionFailed = true;
                continue;
            }

            var answer = response.Answers.GetValueOrDefault(ImportContextBuilder.MappingQuestionId);
            if (answer?.Choice is null)
            {
                decision.Action = "LeaveToHuman";
                decision.Status = dryRun ? "Applied" : "Failed";
                decision.Error = "System One returned no choice";
                plans.Add(new DecisionPlan(decision, PlanKind.LeftToHuman));
                leftToHuman = true;
                decisionFailed = true;
                continue;
            }

            decision.Confidence = answer.Confidence;
            decision.Chosen = answer.Choice;

            if (answer.Confidence < minConfidence)
            {
                decision.Action = "LeaveToHuman";
                decision.Status = dryRun ? "Applied" : "Failed";
                decision.Error = $"Confidence {answer.Confidence:P0} below minimum {minConfidence:P0}";
                plans.Add(new DecisionPlan(decision, PlanKind.LeftToHuman));
                leftToHuman = true;
                continue;
            }

            if (answer.Choice == ImportContextBuilder.AlbumMismatchKey)
            {
                // The model identified the file as a different edition/re-recording, an unlisted
                // track, or an ambiguous album. Never turn that into a destructive rejection.
                decision.Action = "LeaveToHuman";
                decision.Status = dryRun ? "Applied" : "Failed";
                decision.Error = "Album or edition mismatch; left to human";
                plans.Add(new DecisionPlan(decision, PlanKind.LeftToHuman));
                leftToHuman = true;
                continue;
            }

            if (answer.Choice == ImportContextBuilder.NoMatchKey)
            {
                // A track-level no_match on an album Lidarr already flagged as a weak match is
                // not enough evidence to remove and blocklist the download. Leave it for a human.
                if (HasAlbumMismatch(row))
                {
                    decision.Action = "LeaveToHuman";
                    decision.Status = dryRun ? "Applied" : "Failed";
                    decision.Error = "Album match is uncertain; left to human instead of rejecting";
                    plans.Add(new DecisionPlan(decision, PlanKind.LeftToHuman));
                    leftToHuman = true;
                    continue;
                }

                decision.Action = "RejectBlock";
                var reject = new DecisionPlan(decision, PlanKind.Reject);
                plans.Add(reject);
                rejectPlans.Add(reject);
                continue;
            }

            var candidate = offered.FirstOrDefault(c => c.Key == answer.Choice);
            if (candidate is null)
            {
                decision.Action = "LeaveToHuman";
                decision.Status = dryRun ? "Applied" : "Failed";
                decision.Error = $"System One returned unknown choice '{answer.Choice}'";
                plans.Add(new DecisionPlan(decision, PlanKind.LeftToHuman));
                leftToHuman = true;
                continue;
            }

            if (candidate.HasFile)
            {
                // Existing semantics: a target track that already has a file means reject+blocklist.
                decision.Action = "RejectBlock";
                decision.Chosen = candidate.Title;
                var reject = new DecisionPlan(decision, PlanKind.Reject);
                plans.Add(reject);
                rejectPlans.Add(reject);
                continue;
            }

            decision.Action = "Import";
            decision.Chosen = candidate.Title;
            var import = new DecisionPlan(decision, PlanKind.Import);
            plans.Add(import);
            importPlans.Add(import);
            importUpdates.Add(builder.ToImportUpdate(row, candidate));
        }

        // ---- batch actions per download (systemOne.md §6 step 3) ---------------------------------
        var importSucceeded = false;
        var rejectApplied = false;
        var failed = false;
        string? actionError = null;

        if (importPlans.Count > 0)
        {
            if (dryRun)
            {
                foreach (var plan in importPlans)
                    plan.Record.Status = "SkippedDryRun";
            }
            else
            {
                var ok = await lidarr.ImportAsync(importUpdates, ct).ConfigureAwait(false);
                if (ok)
                {
                    importSucceeded = true;
                    foreach (var plan in importPlans)
                        plan.Record.Status = "Done";
                }
                else
                {
                    failed = true;
                    actionError = "ManualImport command failed";
                    foreach (var plan in importPlans)
                        plan.Record.Status = "Failed";

                    // Existing behaviour: a failed import clears the queue item without blocklisting.
                    await lidarr.DeleteQueueItemAsync(job.QueueId, removeFromClient: true, blocklist: false, ct)
                        .ConfigureAwait(false);
                }
            }
        }

        if (rejectPlans.Count > 0)
        {
            if (dryRun)
            {
                foreach (var plan in rejectPlans)
                    plan.Record.Status = "SkippedDryRun";
            }
            else
            {
                var ok = await lidarr.DeleteQueueItemAsync(job.QueueId, removeFromClient: true, blocklist: true, ct)
                    .ConfigureAwait(false);
                if (ok)
                {
                    rejectApplied = true;
                    foreach (var plan in rejectPlans)
                        plan.Record.Status = "Applied";
                }
                else
                {
                    failed = true;
                    actionError ??= "Reject+blocklist queue delete failed";
                    foreach (var plan in rejectPlans)
                        plan.Record.Status = "Failed";
                }
            }
        }

        // ---- persist decisions after their action is known (§11.1) -------------------------------
        foreach (var plan in plans)
            await writer.WriteAsync(plan.Record, ct).ConfigureAwait(false);

        var markDone = !leftToHuman && !failed;
        // A dry-run pass that skipped an import/reject is not final: the marker must only suppress
        // work while dry-run is enabled. Otherwise disabling dry-run would leave the preview
        // decisions permanently unapplied.
        var dryRunDone = markDone && dryRun && (importPlans.Count > 0 || rejectPlans.Count > 0);
        if (markDone)
            jobs.ClearRetry(job.DownloadId);
        else
        {
            // No done marker → re-discovered next poll, with in-memory backoff to avoid a hot loop.
            jobs.ScheduleRetry(job.DownloadId, TimeSpan.FromSeconds(Math.Max(5, settings.PollIntervalSeconds)));
        }

        var outcome = importSucceeded ? "Imported"
            : rejectApplied ? "Rejected"
            : plans.All(p => p.Kind == PlanKind.Skip) ? "Skip"
            : leftToHuman ? "LeftToHuman"
            : "None";

        if (actionError is not null)
            outcome = "None";

        await FinishJobAsync(job, failed || decisionFailed ? "Failed" : "Finished", outcome, plans.Count,
            actionError ?? (leftToHuman ? "Left to human" : null), stopwatch.ElapsedMilliseconds,
            markDone, dryRunDone, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the candidate list from every release of the row's album, target release first.
    /// Lidarr's manual-import row only contains the track it already matched, so using it alone
    /// would hide the rest of the album from the model.
    /// </summary>
    private async Task<IReadOnlyList<Candidate>> BuildCandidatesAsync(
        ManualImportFile row,
        Dictionary<int, IReadOnlyList<LidarrTrack>> releaseTracks,
        CancellationToken ct)
    {
        var releases = (row.AlbumReleases is { Count: > 0 } ? row.AlbumReleases : row.Album?.Releases);
        var ordered = (releases ?? new List<AlbumRelease>())
            .Where(r => r.Id is > 0)
            .OrderByDescending(r => r.Id == row.AlbumReleaseId)
            .ToList();

        if (ordered.Count == 0)
            return builder.BuildCandidates(row);

        var loaded = new List<ReleaseTracks>(ordered.Count);
        var seenReleases = new HashSet<int>();

        foreach (var release in ordered)
        {
            var releaseId = release.Id!.Value;
            if (!seenReleases.Add(releaseId))
                continue;

            if (!releaseTracks.TryGetValue(releaseId, out var tracks))
            {
                try
                {
                    tracks = await lidarr.GetReleaseTracksAsync(releaseId, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (releaseId == row.AlbumReleaseId)
                    {
                        logger.LogWarning(ex,
                            "Target release {AlbumReleaseId} track lookup failed; using Lidarr's matched track only",
                            releaseId);
                        return builder.BuildCandidates(row);
                    }

                    logger.LogWarning(ex,
                        "Release {AlbumReleaseId} track lookup failed; skipping that release", releaseId);
                    continue;
                }

                releaseTracks[releaseId] = tracks;
            }

            loaded.Add(new ReleaseTracks(releaseId, release.Title, tracks));
        }

        var candidates = builder.BuildCandidates(row, loaded);
        return candidates.Count > 0 ? candidates : builder.BuildCandidates(row);
    }

    private async Task FinishUnexpectedFailureAsync(ImportJob job, Exception ex, CancellationToken ct)
    {
        jobs.ScheduleRetry(job.DownloadId, TimeSpan.FromSeconds(30));
        try
        {
            await FinishJobAsync(job, "Failed", "None", 0, ex.Message, 0,
                markDone: false, dryRunDone: false, ct).ConfigureAwait(false);
        }
        catch (Exception inner)
        {
            logger.LogError(inner, "Could not write failed JobRecord for {DownloadId}", job.DownloadId);
        }
    }

    private async Task FinishJobAsync(
        ImportJob job,
        string status,
        string outcome,
        int fileCount,
        string? error,
        long latencyMs,
        bool markDone,
        bool dryRunDone,
        CancellationToken ct)
    {
        var record = new JobRecord
        {
            DownloadId = job.DownloadId,
            QueueId = job.QueueId,
            Title = job.Title,
            CreatedUtc = job.DiscoveredUtc,
            FinishedUtc = DateTime.UtcNow,
            Status = status,
            Outcome = outcome,
            FileCount = fileCount,
            Error = error,
            LatencyMs = latencyMs
        };
        record.Id = repository.InsertJob(record);

        if (markDone)
            repository.MarkDone(job.DownloadId, dryRunDone);

        await broadcaster.JobFinishedAsync(record, ct).ConfigureAwait(false);
        logger.LogInformation(
            "Download {DownloadId} {Status}: {Outcome} ({FileCount} files, {Latency} ms)",
            job.DownloadId, status, outcome, fileCount, latencyMs);
    }

    private static bool HasAlbumMismatch(ManualImportFile row)
    {
        if (row.Rejections.Any(r =>
                string.Equals(r.Type, "Album", StringComparison.OrdinalIgnoreCase)
                || (r.Reason?.StartsWith("Album match", StringComparison.OrdinalIgnoreCase) ?? false)))
            return true;

        var target = ImportContextBuilder.NormalizeTitle(row.Album?.Title);
        var tagged = ImportContextBuilder.NormalizeTitle(row.AudioTags?.AlbumTitle);
        if (target.Length == 0 || tagged.Length == 0 || string.Equals(target, tagged, StringComparison.Ordinal))
            return false;

        // "Speak Now" vs "Speak Now (Taylor's Version)" is an edition mismatch. "Speak Now" vs
        // "Fearless" is a different album and stays eligible for no_match.
        return tagged.Contains(target, StringComparison.Ordinal)
            || target.Contains(tagged, StringComparison.Ordinal);
    }

    private static DecisionRecord BaseDecision(ImportJob job, ManualImportFile row, int rowId) => new()
    {
        App = "Lidarr",
        DownloadId = job.DownloadId,
        QueueId = job.QueueId,
        FilePath = row.Path,
        FileName = row.Name,
        RowId = rowId,
        Resolver = "SystemOne"
    };

    private static DecisionRecord SkipDecision(ImportJob job, ManualImportFile row, int rowId) => new()
    {
        App = "Lidarr",
        DownloadId = job.DownloadId,
        QueueId = job.QueueId,
        FilePath = row.Path,
        FileName = row.Name,
        RowId = rowId,
        Resolver = "System",
        Action = "Skip",
        Status = "Applied",
        Error = "additional file"
    };

    /// <summary>
    /// Lidarr manual-import rows are transient and may arrive with <c>id = 0</c>. Fall back to a
    /// deterministic path hash so the per-row re-decision guard still works and survives restarts.
    /// </summary>
    private static int StableRowId(ManualImportFile row)
    {
        if (row.Id > 0)
            return row.Id;

        var text = row.Path ?? row.Name ?? string.Empty;
        uint hash = 2166136261; // FNV-1a
        foreach (var c in text)
        {
            hash ^= c;
            hash *= 16777619;
        }

        return (int)(hash & 0x7FFFFFFF);
    }

    private static string? Serialize(object? value) =>
        value is null ? null : JsonSerializer.Serialize(value, Json);

    private void PublishProgress() =>
        progress.ReportPending(jobs.PendingSnapshot());

    private sealed record DecisionPlan(DecisionRecord Record, PlanKind Kind);

    private enum PlanKind
    {
        Skip,
        Import,
        Reject,
        LeftToHuman
    }
}
