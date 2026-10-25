# Changelog

All notable changes to **ArrImportSystemOne** are documented here.
This release implements the design in [`systemOne.md`](./systemOne.md).

## [Unreleased] — System One decision backend (Mica provider)

### Fixed

- **System One requests are sent with `Content-Length` instead of chunked encoding.** Mica's HTTP
  server reads only `Content-Length`; a chunked body was parsed as empty and rejected with
  `request.state is required` even though the request contained a valid state.
- **Choice questions omit unset optional flags.** `multi`, `allow_cannot_grade` and
  `abstain_threshold` are no longer serialized as `null`, which Mica rejects on a Choice question.
- **Candidates now cover the whole album, not just Lidarr's single matched track.** The worker
  loads every album release's tracks (target release first), skips releases clearly belonging to a
  different album, dedupes editions by normalized title, and keeps the 253-candidate cap. The
  persisted state keeps Lidarr's matched row tracks; the full option list lives in the question
  criteria, avoiding a duplicated prompt.
- **Confident `no_match` decisions no longer blocklist album/edition mismatches.** The mapping
  prompt now uses Lidarr's embedded `audioTags`, compares recording versions explicitly
  (Taylor's Version, live, acoustic, etc.), and offers two reserved outcomes: `no_match` (different
  album/artist → reject) and `album_mismatch` (different edition/re-recording, ambiguous album, or
  unoffered song → leave to human). As a safety net, a `no_match` on a row with an album-level
  Lidarr rejection is also left to human. This fixes the “Speak Now” vs “Speak Now (Taylor's
  Version)” case, where the target track was never among the offered options.
- **`docker compose up` no longer needs a hand-built `mica:latest` image.** The System One
  provider service is named `systemone` and now has a `build:` section pinned to a Mica commit,
  so Compose builds it on first use instead of trying to pull a non-existent `mica:latest` image.
  `SYSTEMONE_IMAGE` can still point at a prebuilt image (run without `--build`).
- **Compose variables now match the provider-neutral configuration.** The deployment template and
  local `.env` use `SYSTEMONE_*` for the service image/build context, ports, model, GPU count,
  timeout and health interval. Mica-specific values are mapped to the upstream `MICA_*` variables
  inside the `systemone` service, so the rest of the stack does not depend on the provider name.
- **App-to-provider wiring now uses the `systemone` hostname.** `Lidarr__SystemOne__Url` is
  `http://systemone:<port>`, matching the renamed Compose service.

### Summary

Replaced the ASP.NET Core weather-forecast scaffold with the complete SystemOne pipeline:

```
Lidarr queue → QueuePoller → JobQueue → JobWorker → SystemOneDecisionClient → Mica (/v1/systemone) → Lidarr actions
                                             ↓
                              LiteDB decisions / jobs / done
                                             ↓
                              SignalR DecisionHub → Ui/index.html
```

A job is a **download**, not a file: imports and reject+blocklist are submitted as one batched
action per download, and exactly one worker drains the queue so a single Mica GPU is never
oversubscribed.

### Added

#### System One decision client (provider-neutral)
- `Solver/ISystemOneDecisionClient.cs` — the boundary the worker depends on. A better model/provider
  can be swapped in later by registering another implementation; the pipeline does not change.
- `Solver/SystemOneDecisionClient.cs` — default HTTP implementation of the System One protocol:
  posts `POST {Lidarr:SystemOne:Url}/v1/systemone`, uses a per-request linked timeout
  (`TimeoutSeconds`), and rejects any choice that was not offered in the question criteria.
  Mica is the current provider, but its name is no longer baked into the client or DTOs.
- `Solver/SystemOneModels.cs` — `SystemOneDecisionRequest`, `SystemOneQuestion`,
  `SystemOneDecisionResponse`, `SystemOneAnswer`, `SystemOneUsage`, plus client-side
  `SystemOneErrorKind` and `SystemOneHealthResult` (`JsonSerializerDefaults.Web`, snake-case
  attributes where the protocol uses them).
- Failure classification:
  - `Unavailable` — transport failure, timeout, 429, or any non-400/413/422 HTTP status.
  - `InvalidRequest` — 400/413/422 (for example Mica's 32k-token rejection).
  - `InvalidResponse` — parse failure, empty response, or an unknown/offered-outside choice.
- Only the `choice`/`mapping` question is used (no `noul`/`score`; see YAGNI in the design).

#### Availability gate and pause/resume
- `Solver/SystemOneAvailability.cs` — thread-safe gate that **starts closed** until the first
  successful health check. `WaitUntilAvailableAsync` blocks the worker while the provider is down.
- `Solver/SystemOneHealthMonitor.cs` — probes `GET /health` every `Lidarr:SystemOne:HealthCheckSeconds`
  (default 15 s), treats `warming: true` as unavailable, logs transitions, and broadcasts
  `queue.updated` so the UI shows or clears the paused banner.
- `Solver/SystemOneUnavailableException.cs` — internal signal used by the worker to retry an
  in-flight download after the provider recovers.
- `JobWorker` waits for the gate before dequeuing. If a decision call fails as `Unavailable`, it
  aborts the current download **before any Lidarr action or decision write**, reports the outage,
  and retries the same download once the gate reopens. It never marks the download done or writes a
  leave-to-human row because the model is down.
- The queue UI shows a paused banner/pill with the health reason. The poller keeps discovering
  downloads while paused, so pending work is visible and resumes automatically.

#### Context builder
- `Solver/IImportContextBuilder.cs` / `Solver/ImportContextBuilder.cs` — candidate building,
  question building (with the **253-candidate cap + `no_match` + `album_mismatch` = 255 options**),
  state building (including file tags and target release) and `ToImportUpdate`. Candidate order is
  preserved (target release first) so truncation keeps the most likely tracks.
- `Solver/DecisionWriter.cs` — single persistence + `decision.resolved` broadcast path shared by
  the poller and worker.

#### Queue pipeline
- `Queue/ImportJob.cs` — `(DownloadId, QueueId, Title, DiscoveredUtc)`.
- `Queue/JobQueue.cs` — bounded `Channel<ImportJob>` (capacity 256), in-flight dedupe,
  pending snapshot, and in-memory per-download retry/backoff (no retry table).
- `Queue/QueuePoller.cs` — discovery only: reads the Lidarr queue, skips done/in-flight/backed-off
  downloads, writes system skip decisions for additional-file-only downloads, cleans up stale
  empty queue items, and enqueues importable downloads. Never calls the decision provider or mutates
  files.
- `Queue/JobWorker.cs` — single reader; waits for the System One availability gate, re-reads
  `manualimport` rows, resolves each non-additional row through `ISystemOneDecisionClient`, gates
  on `Model:MinConfidence`, batches import/reject actions, persists decisions after the action is
  known, writes `JobRecord`s and the done marker. Provider outages pause and retry instead of
  failing the download.
- `Queue/WorkerProgress.cs`, `Queue/PollNotifier.cs` — kept from the existing design and now
  broadcast through SignalR.

#### Persistence
- `Store/Records.cs` — `DecisionRecord`, `JobRecord`, `DoneRecord`.
- `Store/DecisionRepository.cs` — LiteDB collections `decisions`, `jobs`, `done`; per-row
  `FindLatest`, download done markers, `FlagRestarted`, and job listing.
- Decision records persist the System One **request** (`State`, `Questions`) and **result**
  (`ModelAnswer`) so the UI info dialog survives reloads. Chat/LLM collections were not carried
  over.

#### Real-time UI
- `Hub/DecisionHub.cs` — `GetSnapshot`, `GetDecisionDetail`, `RestartAlbum`.
- `Hub/DecisionBroadcaster.cs` — `queue.updated`, `job.finished`, `decision.resolved`.
- `Hub/DecisionDtos.cs` — snapshot, decision row, and request/result detail payloads.
- `Ui/index.html` — build-free decisions table, live queue panel, automatic reconnect with
  snapshot reload, and an info dialog with **Request** / **Result** tabs.
- `Ui/vendor/signalr.min.js` — vendored SignalR browser client (`@microsoft/signalr` 8.0.7), so the
  UI works offline and needs no build step.

#### Lidarr
- `Lidarr/LidarrClient.cs` — typed `HttpClient` using `X-Api-Key`; queue pagination, manual-import
  lookup, `ManualImport` command, queue delete, and stale cleanup.
- `Lidarr/LidarrModels.cs` — queue, manual-import, track, quality and command models.

#### Configuration and deployment
- `Options/LidarrOptions.cs` — `Lidarr:SystemOne` (`Url`, `TimeoutSeconds`,
  `HealthCheckSeconds`) and `Lidarr:Model` (`MinConfidence`) under the existing `Lidarr` section.
  The old `Lidarr:Llm` and `Lidarr:Mica` blocks are gone.
- `appsettings.json` — dry-run defaults, 60 s poll interval, System One URL `http://localhost:8010`,
  15 s health interval, `MinConfidence` 0.75.
- `compose.yaml` — fully parameterized by `.env`; runs only `arrimportsystemone` and the
  `systemone` sidecar (the current Mica provider; Lidarr stays external). The `systemone` service
  builds from the pinned Mica Git context, passes `CUDA_ARCH`, includes app/provider ports, LiteDB
  and provider-weight volumes, Mica model/repo/revision env, GPU count, and a `/health` healthcheck.
  App config uses `Lidarr__SystemOne__*` and reaches the provider at `http://systemone:<port>`.
- `.env` / `.env.example` — every deployment constant in one place: app image/port/environment,
  existing Lidarr URL/API key/dry-run/poll interval, System One image/build context, provider
  ports, model/repo/revision/name, CUDA architectures, GPU count, timeout/health interval,
  Vulkan/Radeon settings, and `MinConfidence`. `.env` is gitignored; `LIDARR_API_KEY` is required
  so a missing key fails fast at `docker compose config/up`.
- `compose.vulkan.yaml` — standalone AMD Radeon/Vulkan stack. It includes `compose.yaml` (so the
  ArrImportSystemOne app and shared volumes are present) and replaces the `systemone` CUDA build
  with `docker/systemone-vulkan/Dockerfile`, removes the NVIDIA device reservation, passes
  `/dev/dri` into the container, and adds the host `video`/`render` groups. This is the deployment
  path for the target 12 GB Radeon; `COMPOSE_FILE=compose.vulkan.yaml` in `.env.example` selects it.
  `compose.local.yaml` uses the same `include` pattern, so the app is present in every deployment
  variant rather than only in the CUDA file.
- `docker/systemone-vulkan/Dockerfile` — builds Mica's upstream Vulkan runtime with
  `BACKEND=vulkan` and `LLAMA_TAG=b11700`, so the RDNA/Vulkan patches are applied exactly as
  upstream intends. Build dependencies include `libvulkan-dev`, `glslc`, `glslang-tools`, and
  `spirv-headers` (the latter two fix llama.cpp's `SPIRV-HeadersConfig.cmake` /
  `glslangValidator` configuration failure). The runtime image carries Mesa's Vulkan drivers and
  `vulkan-tools` for `vulkaninfo` diagnostics.
- `docker/systemone-vulkan/entrypoint.sh` — serves the provider with 12 GB-safe defaults:
  `--n-ctx 32768`, `--seqs 1`, `--state-cache off`, and `--n-ubatch 256`. Q8_0 weights plus one
  32k sequence fit the target card (the app has a single decision worker); increase `seqs` only on
  a larger GPU. `SYSTEMONE_MODEL=mica-v0.5-4b-IQ4_XS.gguf` is the documented fallback if Vulkan
  reports an out-of-memory error.
- `compose.local.yaml` + `tools/systemone-stub.py` — optional standalone no-GPU stack for macOS and
  other development hosts. It includes `compose.yaml` (app plus volumes) and replaces Mica with a
  tiny protocol-compatible stub that selects the first offered option, so the app, SignalR UI,
  pause/resume plumbing, and Lidarr dry-run path can be exercised without a model. It is explicitly
  **not** a decision model; use the Vulkan or CUDA service for real decisions.
- `compose.local-model.yaml` — optional standalone CPU-fallback stack for a real Mica smoke test.
  It includes `compose.yaml` (app plus volumes) and runs the Vulkan image without `/dev/dri`, so
  llama.cpp uses its CPU backend. It defaults to a 4k context to keep startup manageable; it is
  suitable for `/health` and protocol checks, not for processing a real queue.
- `.gitignore` — ignores `.env` and `.env.local`; `.env.example` remains committed.
- `Dockerfile` — creates `/app/data` with app-user ownership for the LiteDB file.
- `Program.cs` — full DI wiring, SignalR, static UI hosting, `/health`, and the three background
  services (health monitor, poller, worker). The decision client is registered through
  `ISystemOneDecisionClient`, so swapping providers is a one-line registration change. Removed the
  weather-forecast endpoint and OpenAPI registration.

#### Vulkan / Radeon deployment notes
- The Radeon host needs a working `amdgpu` kernel driver and Mesa RADV userspace; the container
  consumes the host's `/dev/dri` render node. Set `COMPOSE_FILE=compose.vulkan.yaml`
  (the `.env.example` default) and run `docker compose up -d --build`, or run
  `docker compose -f compose.vulkan.yaml up -d --build` directly.
- Verify GPU discovery inside the running provider container:
  `docker compose -f compose.vulkan.yaml exec systemone vulkaninfo --summary`. The app remains
  paused on `/health` until the provider is warm.
- The image can be built and started on macOS, but Docker Desktop cannot pass the Mac GPU into a
  Linux container, and macOS has no Vulkan driver for the Linux image. Local validation is limited
  to the image build, container startup, `/health`, and the protocol stub; real Vulkan inference
  must be validated on the Radeon host.
- Build the deployment image on the Radeon host (native `linux/amd64`). A Mac-built image is
  `linux/arm64` and will not run on a typical x86_64 Radeon host; use
  `docker buildx build --platform linux/amd64` only if cross-building deliberately.
- The default `compose.yaml` remains the CUDA path for NVIDIA hosts; the Vulkan override is
  additive and does not add Lidarr to Compose.

### Changed

- **Decisioning moved out of the poll loop.** `QueuePoller` discovers; `JobWorker` decides and acts.
- **Renamed the Mica client to the provider-neutral System One client.** `MicaClient`,
  `MicaDecideRequest`/`MicaQuestion`/`MicaDecideResponse`, `MicaOptions`, and `MicaHealthMonitor`
  became `ISystemOneDecisionClient`/`SystemOneDecisionClient`, `SystemOne*` DTOs, `SystemOneOptions`,
  and `SystemOneHealthMonitor`. Config moved from `Lidarr:Mica` to `Lidarr:SystemOne`; Compose uses
  `Lidarr__SystemOne__*`. Mica remains the first implementation/provider; its Compose service is
  named `systemone` and its deployment settings are exposed as `SYSTEMONE_*`.
- **Provider outages now pause the pipeline.** Previously a dead sidecar produced leave-to-human
  rows. Now the worker waits on `SystemOneAvailability`, retries the in-flight download after
  recovery, and never marks it done or writes a decision because the model was unavailable.
- **Mica replaces the generic LLM.** No streaming, prose parsing, JSON repair, correction prompts,
  token-budget escalation, or model sampling parameters.
- **No deterministic fast path.** Every non-additional row is a System One decision; confidence
  below `MinConfidence` is left to a human.
- Model decisions are recorded with `Resolver = "SystemOne"` (previously `"Mica"`). System skips
  keep `Resolver = "System"`.
- **SignalR replaces REST/WebSocket.** There are no `/api/*` endpoints, no `/ws`, and no 10 s UI
  polling. The only HTTP routes are `GET /`, static assets, `/health`, and the hub.
- `DryRun` still gates imports and reject+blocklist; dry-run decisions are recorded as
  `SkippedDryRun` (imports/rejects) or `Applied` (leave-to-human).
- `AdditionalFile` rows, stale queue items, and empty downloads are recorded with
  `Resolver = "System"` instead of `"Deterministic"`.

### Persistence rules applied

| Situation | Action | Status | Done marker |
|---|---|---|---|
| Additional file / stale / no files | `Skip` (`System`) | `Applied` | yes |
| Import in dry-run | `Import` | `SkippedDryRun` | yes |
| Import submitted | `Import` | `Done` | yes |
| Reject + blocklist | `RejectBlock` | `Applied` | yes |
| Low confidence / invalid request / invalid response / no candidates | `LeaveToHuman` | `Failed` (or `Applied` in dry-run) | **no** |
| Provider unavailable (transport / 5xx / 429 / timeout / warming) | no row written | — | **no** — download retried in place after recovery |
| Failed import | `Import` | `Failed` | **no** |
| Restart requested | — | decisions flagged `Restarted` | cleared |

A `JobRecord` is written for every completed decision attempt (`Imported`, `Rejected`, `Skip`,
`LeftToHuman`, `None`), but not for provider-outage pauses, which are retried. A failed-import path
clears the queue item without blocklisting.
Downloads that are not marked done are retried on the next poll with an in-memory exponential
backoff (capped at 10 minutes).

### Removed

- Weather-forecast sample endpoint/model and OpenAPI registration.
- The `Microsoft.AspNetCore.OpenApi` package reference.
- Any traces of the old `LlmDecisionClient`, `LiveUpdateHub`/`/ws`, `/api/decisions`,
  `/api/progress`, `/api/chats`, `/api/restart`, and chat/thinking persistence — this repository
  never contained them, and none were introduced.

### Tests

Added `ArrImportSystemOne.Tests` (xUnit + Moq) covering:

- `ImportContextBuilder` — candidate order/dedupe, same-album release filtering, 253-cap,
  reserved outcomes, state fields (file tags/target release), import update mapping, duration
  formatting.
- `SystemOneDecisionClient` — request shape/path, answer parsing, 400 → `InvalidRequest`,
  5xx/transport → `Unavailable`, unknown choice → `InvalidResponse`, and `/health` parsing
  (including `warming: true` and 503).
- `SystemOneAvailability` — starts paused, opens on `ReportAvailable`, closes on
  `ReportUnavailable`, and is reflected in `QueueUpdated.Paused`/`PauseReason`.
- `DecisionRepository` — decisions, `FindLatest`, done markers, restart flagging, jobs.
- `JobQueue` — dedupe, dequeue/complete, pending snapshot, retry backoff.
- `QueuePoller` — enqueue importable downloads, additional-file skip, done dedupe, stale cleanup,
  empty-while-downloading.
- `JobWorker` — successful import, dry-run, low confidence, invalid request, **provider outage
  pauses with no decisions/done marker**, no-match reject+blocklist, additional-file skip.
- `DecisionHub` — snapshot, detail round-trip, restart.

Result: `dotnet build` clean; `dotnet test` **47/47 passing**. A fake-Lidarr/fake-provider smoke
test also verified both pause paths: startup-unavailable (no decision call or Lidarr action until
health recovered) and mid-flight 503 (no decision/job record, same download retried and imported
after recovery).

Deployment validation:
- `docker compose config` succeeds for `compose.yaml` (CUDA), `compose.vulkan.yaml` (app +
  Vulkan provider) and `compose.local.yaml` (app + stub).
- The Vulkan image builds successfully from the pinned Mica source on macOS/arm64; the build
  includes llama.cpp's Vulkan backend and the `libggml-vulkan.so` runtime library.
- On macOS, `vulkaninfo` reports no physical Vulkan device (Docker Desktop cannot pass the Mac GPU
  into Linux). The Vulkan image still starts and serves `/health`; a 118-token decision request
  completed via the CPU fallback in about 17 s using a reduced 4k context. This validates the image
  and protocol, not Radeon Vulkan performance.
- The local `compose.local.yaml` stack starts both `arrimportsystemone` and the stub; `/health` is
  OK for both and the UI returns HTTP 200. The stub's choice response was verified directly.
- The CPU-fallback `compose.local-model.yaml` stack starts the real Mica model without `/dev/dri`;
  `/health` came up and a 118-token request completed in about 19 s with a 4k context.

### Notes / assumptions

- This repository started from the empty ASP.NET Core template, so the "keep as is" Lidarr client
  and `ImportDecisionEngine` referenced by the design were implemented from scratch to match the
  documented target state. They remain isolated so a real existing client can be dropped in.
- `ManualImportFile.Tracks` is treated as Lidarr's ordered candidate set for the matched release
  (target release first). No extra album-release lookup is performed; ordering is never changed.
- `ManualImportUpdate` sends one track id per file. Multi-track files are out of scope for v1
  (YAGNI in the design).
- `JobWorker` re-reads manual-import rows so decisions never use a poller snapshot. If rows
  disappear between discovery and processing, the job is dropped; the poller cleans up stale queue
  items on its next pass.
- `SYSTEMONE_IMAGE` defaults to `arrimportsystemone-systemone:local`; Compose builds it from the
  pinned Mica source by default. `SYSTEMONE_BUILD_CONTEXT` accepts another Git ref or a local
  checkout, and a prebuilt image can be supplied via `SYSTEMONE_IMAGE` (run without `--build`).
  Weights are downloaded on first start into the `systemone-weights` volume;
  `SYSTEMONE_GPU_COUNT=all` reserves every NVIDIA GPU, or set a number. The default image builds
  Mica's CUDA runtime, so use the Vulkan override on the target Radeon; the app itself still starts
  and shows the paused state when the provider is absent.
- Lidarr is intentionally **not** part of `compose.yaml`. Set `LIDARR_URL` to an instance reachable
  from the app container (`host.docker.internal` for a host install, or the container/LAN address).
- The design document still says `MicaClient`/`Lidarr:Mica`; that is the historical design. The
  implementation now uses the provider-neutral System One names and `Lidarr:SystemOne`, as
  documented above. Existing decisions written with `Resolver = "Mica"` remain readable.
- To swap providers later, implement `ISystemOneDecisionClient` and change the typed-client
  registration in `Program.cs`; the worker, availability gate, UI and persistence stay the same.
- Production authentication/TLS for the UI is out of scope per the design.
