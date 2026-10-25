# SystemOne — Mica decision backend for ArrImportSolver

Status: design / migration plan
Scope: replace the generic LLM with **Mica** (`POST /v1/systemone`) and split the pipeline into
**discovery → queue → worker**. The Lidarr client stays unchanged.

---

## 1. Goal

Keep the existing behaviour (resolve Lidarr manual-import conflicts, import / reject+blocklist /
leave to human, persist everything, show it in the UI) but replace the LLM call with Mica's
calibrated decision endpoint, and stop deciding inside the poll loop.

Non-goals (YAGNI, see §12): multi-app support, model training, streaming reasoning, retries around
free-text generation, candidate splitting across multiple Mica calls.

---

## 2. What Mica gives us

Mica is a 4B **decision** model. It returns probabilities, never text.

- Endpoint: `POST {Mica:Url}/v1/systemone` (default port `8010`)
- Health: `GET /health`
- Auth: none (plain HTTP sidecar)
- Request: one `state` object (shared by all questions) + a `questions` map
- Question types:
  - `noul` — yes/no probability; optional `criteria: { "false": "...", "true": "..." }`
  - `choice` — `criteria: { "<key>": "<description>", ... }`, **2–255 options**
  - `score` — ordered `criteria: ["lowest", ..., "highest"]`, 2–255 levels
- Optional per question: `multi`, `allow_cannot_grade`, `abstain_threshold` (0..1)
- Response: `answers.<id>.choice` + `probabilities` + `confidence` for `choice`; `answers.<id>.noul`
  for `noul`; plus `usage` and `latency_ms`
- Hard limits: **255 options/questions**, **32,768 input tokens**, malformed input or oversize ->
  HTTP 400 with a message (nothing is truncated server-side)

Example request/response for our mapping question:

```json
POST /v1/systemone
{
  "state": {
    "file_name": "01 - Song.flac",
    "relative_path": "Album/01 - Song.flac",
    "artist": "Artist",
    "album": "Album",
    "album_release_id": 10,
    "target_release": "Album",
    "quality": "FLAC",
    "rejections": [],
    "file_tags": {
      "title": "Song", "artist": "Artist", "album": "Album",
      "track_numbers": [1], "disc_number": 1, "duration": "3:20"
    },
    "tracks": [{ "title": "Song", "number": "1", "duration": "3:20", "has_file": false }],
    "download_name": "Artist - Album",
    "download_title": "Artist - Album",
    "queue_status": "completed"
  },
  "questions": {
    "mapping": {
      "type": "choice",
      "instructions": "Task: match the downloaded audio file to exactly one offered track, or choose the outcome that says why it cannot be matched. ...",
      "criteria": {
        "option_0": "release: \"Album\" | title: \"Song\" | length: 3:20 | track: 1",
        "option_1": "release: \"Album\" | title: \"Other\" | length: 4:01 | track: 2",
        "no_match": "different album or artist, not an edition of the target album",
        "album_mismatch": "different edition/re-recording, ambiguous album, or unoffered song"
      }
    }
  }
}
```

```json
{
  "model": "mica-v0.5-4b",
  "answers": {
    "mapping": {
      "type": "choice",
      "choice": "option_0",
      "probabilities": { "option_0": 0.94, "option_1": 0.03, "no_match": 0.03 },
      "confidence": 0.94
    }
  },
  "usage": { "input_tokens": 168, "output_tokens": 0 },
  "latency_ms": 41.7
}
```

---

## 3. Architecture

```
┌──────────────────────── C# host ─────────────────────────┐
│                                                          │
│  QueuePoller (BackgroundService)                         │
│    Lidarr.GetQueueAsync                                  │
│    Lidarr.GetManualImportAsync(downloadId)               │
│      ├─ additionalFile  → DecisionRecord Skip (finished)  │
│      └─ importable row  → ImportJob → Channel<ImportJob>  │
│                                                          │
│  JobWorker (BackgroundService)   ← single reader          │
│    for each ImportJob:                                   │
│      BuildCandidates / BuildQuestions / BuildState        │
│      MicaClient.DecideAsync  (choice "mapping")          │
│      gate on confidence                                  │
│      batch per download → Import / Reject+Block / Skip    │
│      write DecisionRecord(s) + JobRecord + done marker    │
│                                                          │
│  LiteDB  decisions / jobs / done                          │
│  SignalR DecisionHub ──push──► index.html                 │
│    queue.updated / job.finished / decision.resolved       │
└──────────────────────────────────────────────────────────┘
                          │ HTTP
                 ┌────────▼─────────┐
                 │ Mica sidecar      │
                 │ :8010 /v1/systemone│
                 └──────────────────┘
```

Two background services, one durable store:

- **QueuePoller** only discovers work. It never calls Mica and never imports. It skips downloads
  already marked done and does not enqueue the same download twice while it is in flight.
- **JobWorker** is the only component that calls Mica and Lidarr's mutating endpoints
  (`POST /command`, `DELETE /queue/{id}`). It drains the channel one job at a time, so a single GPU
  is never oversubscribed.

Why a job is a **download**, not a file: Lidarr's import is a single `ManualImport` command carrying
all chosen files of a download, and reject+blocklist is a single queue delete. Per-download jobs keep
those actions atomic and match the existing worker flow.

---

## 4. Job lifecycle

```
            enqueue (in-memory)              resolve (Mica + Lidarr)
  discovered ─────────────────► Pending ─────────────────────────────► Finished
        │                          │                                   │  ├─ Imported
        │                          │                                   │  ├─ Rejected
        │                          └─ process crash / restart          │  ├─ Skipped
        │                             → re-discovered next poll         │  └─ LeftToHuman
        └─ additional file / stale / no files ────────────────────────► Finished (Skip)
                                                                        │
                                                                        └─ Failed (error)
```

- **Pending** jobs live in an in-memory `Channel<ImportJob>` (bounded to a small number, e.g. 256).
  Nothing pending is written to the DB: on restart the poller simply re-discovers the Lidarr queue.
- **Finished** jobs are written to the DB immediately, together with their per-file
  `DecisionRecord`s and the download-level `done` marker. `Finished` includes `LeftToHuman` and
  `Failed` — "finished" means "the worker is done with it", not "it succeeded".
- Dedupe:
  - in-flight downloads: `ConcurrentDictionary<string downloadId, byte>` in the poller
  - already decided: `done` collection (`DecisionRepository.IsDownloadDone`)
  - per-row re-decision guard: `FindLatest(downloadId, rowId)` still applies
- The hub method `RestartAlbum(downloadId)` (§9.1) clears the done marker and resets decisions; the
  poller re-enqueues on the next pass. No separate job table rewrite is needed.

---

## 5. Data model

### 5.1 In-memory job

```csharp
public sealed record ImportJob(
    string DownloadId,
    int QueueId,
    string? Title,
    DateTime DiscoveredUtc);
```

Rows are re-read in the worker (`GetManualImportAsync`) so the decision uses fresh Lidarr data, not
a stale snapshot taken by the poller.

### 5.2 Finished job record (LiteDB collection `jobs`)

```csharp
public sealed class JobRecord
{
    [BsonId] public int Id { get; set; }
    public string DownloadId { get; set; } = "";
    public int QueueId { get; set; }
    public string? Title { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime FinishedUtc { get; set; }
    public string Status { get; set; } = "";   // Finished | Failed
    public string Outcome { get; set; } = "";  // Imported | Rejected | Skip | LeftToHuman | None
    public int FileCount { get; set; }
    public string? Error { get; set; }
    public long LatencyMs { get; set; }
}
```

Only written when the worker finishes a download. There is no Pending row in `jobs`.

### 5.3 Decision record (existing `decisions`, keep)

One row per manual-import file, as today. Fields that remain meaningful:

`Id, UtcTimestamp, App, DownloadId, QueueId, FilePath, FileName, RowId, Candidates, Questions,
State, ModelAnswer, Action, Status, Error, InputSize, LatencyMs, Confidence, Chosen`.

`State` + `Questions` are the persisted **Mica request**; `ModelAnswer` is the persisted **Mica
result**. Together they back the UI's info button (§9). They are written for every Mica decision,
including `LeaveToHuman`.

Field changes:

| Field | Change |
|---|---|
| `Resolver` | `"Mica"` replaces `"Llm"`; `"System"` replaces `"Deterministic"` for additional-file/stale skips |
| `ModelAnswer` | keep; now the full Mica response (`choice`, `confidence`, `probabilities`, `latency_ms`) |
| `ModelRaw` | remove (Mica returns no text) |
| `LlmChatRecord` / `llm_chats` | remove entirely (no prompts/thinking/content) |
| `Status` | `Pending` is now only a transient in-memory state; persisted rows are `Applied`, `SkippedDryRun`, `Failed`, `Done`, `Restarted` |

`done` collection and its semantics are unchanged: written after a download is fully handled so the
same queue item is not re-decided on every poll.

---

## 6. Decision pipeline in the JobWorker

For one job (download):

1. `rows = lidarr.GetManualImportAsync(downloadId)`
   - empty + still downloading → drop job (it will be re-discovered later)
   - empty + no longer downloading → stale cleanup (existing `RemoveStaleQueueItemAsync` behaviour)
   - download already done → drop job
2. For each row:
   - `AdditionalFile == true` → `Skip`, status `Applied`, resolver `System`. No Mica call.
   - Otherwise → resolve through Mica (below). **No deterministic title/duration matching, no
     "already fully parsed" fast path.** Every non-additional row is a Mica decision.
3. Batch actions per download:
   - any mapped file → one `ImportAsync(files)`
   - any `no_match` above threshold **without an album-level uncertainty** →
     `DeleteQueueItemAsync(removeFromClient: true, blocklist: true)`
   - `album_mismatch` above threshold, or `no_match` when Lidarr rejected the album match
     (`Rejection.Type == "Album"` / “Album match…”) or the embedded album is an edition of the
     target → `LeaveToHuman` (no Lidarr action)
   - failed import (Lidarr left rows) → clear the queue item without blocklist (existing behaviour)
4. Write `DecisionRecord`s + `JobRecord` + `Done` marker. A left-to-human result never writes the
   `Done` marker, so it can be re-decided after the human fixes the album association.

### 6.1 Resolve one row with Mica

```
candidates = BuildCandidatesAsync(row)        // target release first, same-album editions only
if candidates == 0:
    no Mica call → LeaveToHuman ("No candidate tracks")

options = candidates capped to 253, keyed option_0..option_N
options["no_match"]       = "different album or artist, not an edition of the target album"
options["album_mismatch"] = "different edition/re-recording, ambiguous album, or unoffered song; leave for human review"

request = {
  state: BuildModelState(queueItem, row),     // includes file_tags + target_release
  questions: { mapping: { type: "choice", instructions: <mapping rules>, criteria: options } }
}

response = MicaClient.DecideAsync(request, ct)

if response.Error != null:                  → LeaveToHuman, Failed, Error
choice     = response.Answers["mapping"].Choice
confidence = response.Answers["mapping"].Confidence

if confidence < Model.MinConfidence:        → LeaveToHuman
if choice == "album_mismatch":              → LeaveToHuman
if choice == "no_match":
    HasAlbumMismatch(row) ? LeaveToHuman : RejectBlock
else:
    candidate = candidatesByKey[choice]
    candidate.HasFile ? RejectBlock : Import
```

`album_mismatch` and `confidence` replace the old LLM prose parsing. There is no JSON extraction,
no streaming, no correction prompt, no token-budget escalation, no `reasoning_effort`, `num_ctx`,
`max_tokens`, or retry loop.

### 6.2 Keep / adapt from the current code

| Current | Action |
|---|---|
| `Lidarr/LidarrClient.cs` + models | **keep as is** (user requirement) |
| `ImportDecisionEngine.BuildCandidatesAsync` | keep (candidate building) |
| `ImportDecisionEngine.BuildQuestions` | keep, but enforce option cap |
| `ImportDecisionEngine.BuildModelState` | keep |
| `ImportDecisionEngine.ToImportUpdate` | keep |
| `ImportDecisionEngine.Classify` | **remove** (deterministic decision) |
| `ImportDecisionEngine.TryResolveDeterministicMatch` | **remove** |
| `IImportDecisionEngine` | rename to `IImportContextBuilder` (candidates/questions/state/import update only) |
| `LlmDecisionClient.cs` (1096 lines) | replace with `MicaClient.cs` (~120 lines) |
| `Worker.cs` | split into `QueuePoller` + `JobWorker`; action helpers move to `JobWorker` |
| `DecisionRepository` | add `jobs` collection; remove chat methods |
| `LiveUpdateHub` / `/ws` | **remove**; replaced by SignalR `DecisionHub` (`/hub/decisions`) |
| `/api/decisions`, `/api/progress`, `/api/chats`, `/api/restart` | **remove**; all reads/writes go through the hub |
| `WorkerProgress` | keep (poller reports queue size; worker reports current job); pushed via hub |
| `PollNotifier` | keep (hub restart triggers immediate poll) |
| `Ui/index.html` | keep the decisions table, the **queue panel** and the **info button**; replace REST/WebSocket with SignalR |

---

## 7. The 255-option constraint

Mica accepts **2–255** options for a `choice` question (and up to 32,768 input tokens).

- Reserve one slot each for `no_match` and `album_mismatch`: **at most 253 candidates per request**.
- Candidates are already ordered with the target release first and deduped. Releases whose title
  clearly belongs to another album are skipped, so unrelated albums cannot pollute the option list.
  When `candidates.Count > 253`, take the first 253 and log a warning with the download/row and the
  number dropped.
- If `candidates.Count == 1`, options are `option_0` + `no_match` + `album_mismatch` = 3 → valid.
- If `candidates.Count == 0`, do not call Mica (a 2-option question has no track to choose).
- Truncation loses the correct answer when it falls beyond the cut. This is accepted for v1:
  albums with > 253 distinct tracks/releases are rare. Splitting a large candidate set into
  multiple Mica calls is explicitly out of scope (YAGNI).
- The input-token limit (32k) is separate. If Mica returns HTTP 400 for oversize input, mark the
  job `Failed` and log the 400 detail; do not retry with a different prompt (YAGNI).

---

## 8. Mica client

```csharp
public sealed class MicaClient(HttpClient http, IOptionsMonitor<LidarrOptions> options,
    ILogger<MicaClient> logger)
{
    // POST {Mica.Url}/v1/systemone
    public async Task<MicaDecideResponse?> DecideAsync(MicaDecideRequest request, CancellationToken ct);
}
```

Request/response DTOs (all `JsonSerializerDefaults.Web`):

```csharp
public sealed class MicaDecideRequest
{
    public object? State { get; set; }
    public Dictionary<string, MicaQuestion> Questions { get; set; } = new();
}

public sealed class MicaQuestion
{
    public string Type { get; set; } = "choice";
    public string? Instructions { get; set; }
    public Dictionary<string, string?>? Criteria { get; set; }   // choice
    // noul/score omitted until a second question is actually needed
}

public sealed class MicaDecideResponse
{
    public string? Model { get; set; }
    public Dictionary<string, MicaAnswer> Answers { get; set; } = new();
    public MicaUsage? Usage { get; set; }
    public long LatencyMs { get; set; }
    public string? Error { get; set; }   // client-side: transport / HTTP / parse
}

public sealed class MicaAnswer
{
    public string Type { get; set; } = "";
    public string? Choice { get; set; }
    public double Confidence { get; set; }
    public Dictionary<string, double>? Probabilities { get; set; }
}
```

Client behaviour:

- `HttpClient.Timeout = Timeout.InfiniteTimeSpan`; per-request linked `CancelAfter(Mica.TimeoutSeconds)`.
- Non-2xx → read body (Mica puts the reason in `detail`) and return `Error`.
- A choice not present in `Criteria` → `Error` (never map to an arbitrary candidate).
- No retries inside the client. A transport failure fails the job; the next poll re-enqueues it
  (because no done marker was written).

---

## 9. UI and real-time communication (SignalR)

All UI communication uses **SignalR**; there are no `/api/*` endpoints and no hand-rolled WebSocket.

- One hub: `DecisionHub : Hub`, mapped at `/hub/decisions` (`AddSignalR()` + `MapHub`). ASP.NET Core
  SignalR ships with the existing `Microsoft.AspNetCore.App` framework reference — no new NuGet
  package for the server.
- The JS client is vendored as `Ui/vendor/signalr.min.js` (copied from the `@microsoft/signalr`
  package) so the UI stays build-free and works offline. The only HTTP route left is `GET /` serving
  `index.html`.
- Connection uses `withAutomaticReconnect`. On every (re)connect the client calls `GetSnapshot` and
  rebuilds the table/queue from scratch, then applies pushed events — messages missed while
  disconnected are therefore harmless.

### 9.1 Hub methods (client → server)

| Method | Returns | Purpose |
|---|---|---|
| `GetSnapshot(int decisionLimit = 200)` | `{ decisions, jobs, queue }` | initial/reconnect load |
| `GetDecisionDetail(int decisionId)` | `{ decision, request, result }` | **info button** |
| `RestartAlbum(string downloadId)` | `bool` | clears done marker, flags decisions `Restarted`, signals poller |

`GetDecisionDetail` payload:

```json
{
  "decision": { "id": 42, "fileName": "01 - Song.flac", "action": "Import", "status": "Done", "chosen": "..." },
  "request": {
    "state": { "file_name": "01 - Song.flac", "artist": "Artist", "...": "..." },
    "questions": { "mapping": { "type": "choice", "instructions": "...", "criteria": { "option_0": "...", "no_match": "...", "album_mismatch": "..." } } }
  },
  "result": { "choice": "option_0", "confidence": 0.94, "probabilities": { "option_0": 0.94 }, "latencyMs": 42 }
}
```

`request` comes from the decision's persisted `State`/`Questions`, `result` from `ModelAnswer`.

### 9.2 Broadcasts (server → all clients)

Broadcast through `IHubContext<DecisionHub>`; no groups, no per-user filtering (YAGNI).

| Event | Payload | When |
|---|---|---|
| `queue.updated` | `{ queueCount, processedCount, pendingCount, pending[], currentItem, currentDownloadId, currentFile, inProgress }` | poller enqueues / worker starts or finishes a job |
| `job.finished` | `JobRecord` | a download job is finished (success, skipped, left-to-human, failed) |
| `decision.resolved` | `{ id, downloadId, queueId, fileName, action, status, chosen, confidence, latencyMs }` | after each `DecisionRecord` is written |

### 9.3 UI layout

- **Queue panel** (kept): shows pending jobs in order (title + downloadId), the job being resolved,
  the current file, and processed/total. Updated by `queue.updated`; **no 10 s polling**.
- **Decisions table** (kept): columns as today; rows arrive via `GetSnapshot` + `decision.resolved` /
  `job.finished`.
- **Info button** (kept): opens a dialog with two tabs:
  - **Request** — pretty-printed `state` + `questions`, including the offered `option_*` /
    `no_match` / `album_mismatch` criteria (so the 253-candidate cap is visible)
  - **Result** — Mica `choice`, `confidence`, `probabilities`, `latencyMs`, plus the action/status
  - Data comes from `GetDecisionDetail`; it survives page reloads because request and result are
    persisted in the decision record.
- The old LLM chat/thinking panel is removed. The info dialog replaces it with the decision
  request/result only.

---

## 10. Configuration

Replace the `Lidarr:Llm` block with `Lidarr:Mica` (section name kept under `Lidarr` to avoid moving
options around; `Lidarr` client options themselves are untouched).

```jsonc
"Lidarr": {
  "Url": "...",
  "Key": "...",
  "DryRun": true,
  "PollIntervalSeconds": 60,
  "Mica": {
    "Url": "http://mica:8010",
    "TimeoutSeconds": 120
  },
  "Model": {
    "MinConfidence": 0.75
  }
}
```

```csharp
public class MicaOptions
{
    public string Url { get; set; } = "http://localhost:8010";
    public int TimeoutSeconds { get; set; } = 120;
}
```

`compose.yaml`: add a `mica` service (image built from the Mica repo, `-p 8010:8010`,
`--gpus all`, a `mica-weights` volume) and point `Lidarr__Mica__Url` at it. Remove all
`Lidarr__Llm__*` environment variables.

---

## 11. Persistence rules (final)

1. Every file the worker acts on gets a `DecisionRecord` written **after** the action is known:
   - additional file / stale / no files → `Skip` / `Applied` / `System`
   - import in dry-run → `Import` / `SkippedDryRun`
   - import submitted → `Import` / `Done`
   - reject+blocklist → `RejectBlock` / `Applied`
   - low confidence / Mica error → `LeaveToHuman` / `Failed` (or `Applied` when dry-run)
2. A `JobRecord` is written for every finished download (success, failure, left-to-human).
3. The `done` marker is written **only** when the download needs no further work: successful import,
   applied reject, or queue cleanup. A `LeaveToHuman` or Mica failure must **not** mark it done, so
   the poller re-enqueues it on the next pass. To avoid a hot loop, an in-memory per-download retry
   counter (or a short backoff) is enough; do not add a retry table (YAGNI).
4. The hub method `RestartAlbum(downloadId)` deletes the done marker and flags the download's
   decisions `Restarted`; the next poll enqueues it again.

---

## 12. YAGNI — explicitly cut

- Generic OpenAI-compatible client, streaming (`text/event-stream`), `thinking`/`reasoning`
  capture, `response_format`, JSON repair, correction prompts, token-budget escalation
- Chat/`LlmChatRecord` persistence and the old chat/thinking panel (the info button keeps only the
  Mica request/result)
- Manual WebSocket fan-out (`LiveUpdateHub`, `/ws`) and REST polling endpoints (`/api/*`); SignalR
  is the single UI transport
- SignalR groups, per-user filtering, server-side UI state, and a second hub
- Deterministic classification and exact-match resolution (`Classify`, `TryResolveDeterministicMatch`)
- A persistent pending-job table, retry/backoff tables, job attempts history
- Multiple Mica question types (`noul` reject question, `score` search term); only `mapping`
  (`choice`) is needed
- Splitting candidate sets > 253 into multiple requests
- Multi-app (Sonarr/Radarr), authentication, dashboards with mutations
- Model training/calibration; Mica's stock checkpoint is used as-is

---

## 13. File layout after migration

```
ArrImportSolver/
  Program.cs                      → DI: MicaClient, QueuePoller, JobWorker, repository, UI
  Options/LidarrOptions.cs        → MicaOptions replaces LlmOptions
  Lidarr/…                        → UNCHANGED
  Solver/
    IImportContextBuilder.cs      → candidates / questions / state / import-update
    ImportContextBuilder.cs       → former ImportDecisionEngine minus deterministic parts
    MicaClient.cs                 → POST /v1/systemone
  Store/
    DecisionRepository.cs         → decisions + jobs + done; chats removed
  Queue/
    ImportJob.cs
    JobQueue.cs                   → Channel<ImportJob> + in-flight dedupe
    QueuePoller.cs                → Lidarr discovery → enqueue
    JobWorker.cs                  → resolve + act + persist
  Hub/
    DecisionHub.cs                → SignalR: snapshot, detail, restart, broadcasts
  Ui/index.html                   → decisions table + queue panel + info dialog
  Ui/vendor/signalr.min.js        → vendored SignalR JS client
compose.yaml                      → arrimportsolver + mica
systemOne.md                      → this document
```

---

## 14. Migration checklist

1. Add `MicaClient` + DTOs; wire `Lidarr:Mica` options and health check on startup (optional).
2. Strip `ImportDecisionEngine` down to `ImportContextBuilder`: delete `Classify`,
   `TryResolveDeterministicMatch`, `RowKind`, `RowClassification`; add the 253-candidate cap in
   `BuildQuestions`.
3. Introduce `ImportJob`, `JobQueue`, `QueuePoller`, `JobWorker`; port the action helpers
   (`ApplyImport`, `SubmitImportsAsync`, `ApplyRejectsAsync`, `RemoveStaleQueueItemAsync`,
   `ClearUnimportableQueueItemAsync`) from `Worker`.
4. Add `jobs` collection + `JobRecord`; remove chat methods/records.
5. Add SignalR: `AddSignalR()`, `MapHub<DecisionHub>("/hub/decisions")`, `IHubContext` broadcasts;
   vendor `signalr.min.js` into `Ui/vendor/`.
6. Replace `index.html` transport: keep the queue panel, decisions table and info button; remove
   polling/WebSocket and the chat panel; use `GetSnapshot` + reconnect + pushed events, and
   `GetDecisionDetail` for the info dialog.
7. Delete `LlmDecisionClient`, `LiveUpdateHub`, `/ws`, `/api/*`.
8. Update `compose.yaml` and `appsettings*.json`.
9. Delete/replace tests that target removed code:
   - `ImportDecisionEngineTests` → keep candidate/question/state/import-update tests, drop
     `Classify*` and `TryResolveDeterministicMatch*`
   - `LlmDecisionClientTests` → replace with `MicaClientTests` (request shape, 255 cap, error and
     unknown-choice handling)
   - `DecisionRepositoryTests` → drop chat tests, add `jobs` tests
   - add `JobQueue`/`JobWorker` lifecycle tests
   - add `DecisionHub` tests (snapshot, detail round-trip, restart)
10. Run `dotnet build` and `dotnet test`; then a dry-run against a real Lidarr + Mica sidecar.

---

## 15. Risks / notes

- **GPU serialization:** one `JobWorker` reader is deliberate; concurrent Mica calls would thrash
  a single GPU. Channel capacity bounds memory, not throughput.
- **No Mica availability means no decisions:** the worker leaves download jobs pending/unprocessed;
  the poller keeps discovering them. Add `/health` logging so a dead sidecar is obvious.
- **Ordering:** candidates must remain target-release-first so truncation at 253 keeps the most
  likely tracks. Do not reorder.
- **Idempotency:** never write the `done` marker on `LeaveToHuman`/failure, otherwise a restart
  cannot retry. The in-memory dedupe set prevents duplicate enqueues while a job is in flight.
- **Reject+blocklist is destructive** and still gated by `MinConfidence` and `DryRun`. A
  `no_match` is downgraded to `LeaveToHuman` when Lidarr reported an album-level rejection or the
  embedded album is an edition of the target album, so an edition/re-recording mismatch never
  blocklists a valid download.
- **SignalR reconnects:** pushes are fire-and-forget and not replayed. The client always reloads
  `GetSnapshot` after a reconnect; do not rely on an event stream alone to rebuild the table.
