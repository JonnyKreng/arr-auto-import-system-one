using System.Net;
using System.Text.Json;
using ArrImportSystemOne.Lidarr;
using ArrImportSystemOne.Options;
using ArrImportSystemOne.Queue;
using ArrImportSystemOne.Solver;
using ArrImportSystemOne.Store;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArrImportSystemOne.Tests;

public sealed class JobWorkerTests
{
    [Fact]
    public async Task ImportSuccess_WritesDoneDecisionAndDoneMarker()
    {
        using var harness = new Harness();

        await harness.Worker.ProcessJobAsync(Job(), CancellationToken.None);

        var decision = Assert.Single(harness.Temp.Repository.GetDecisions(10));
        Assert.Equal("SystemOne", decision.Resolver);
        Assert.Equal("Import", decision.Action);
        Assert.Equal("Done", decision.Status);
        Assert.Equal("Song", decision.Chosen);
        Assert.Equal(0.94, decision.Confidence, 3);
        Assert.NotNull(decision.State);
        Assert.NotNull(decision.Questions);
        Assert.NotNull(decision.ModelAnswer);

        Assert.True(harness.Temp.Repository.IsDownloadDone("dl1"));

        var job = Assert.Single(harness.Temp.Repository.GetJobs(10));
        Assert.Equal("Finished", job.Status);
        Assert.Equal("Imported", job.Outcome);
        Assert.Equal(1, job.FileCount);

        var command = Assert.Single(harness.LidarrHandler.Requests, r => r.Uri.EndsWith("/api/v1/command"));
        Assert.Contains("\"trackIds\":[101]", command.Body);
        Assert.Single(harness.Broadcaster.Decisions);
        Assert.Single(harness.Broadcaster.Jobs);
    }

    [Fact]
    public async Task CandidateOptions_ComeFromAllAlbumReleases_NotOnlyTheMatchedTrack()
    {
        const string manualImport = """
            [
              {
                "id": 1,
                "path": "/music/Artist/Album/01 - Song.flac",
                "name": "01 - Song.flac",
                "downloadId": "dl1",
                "additionalFile": false,
                "artist": { "id": 5, "artistName": "Artist" },
                "album": {
                  "id": 7,
                  "title": "Album",
                  "releases": [
                    { "id": 10, "title": "Album (Target Edition)", "trackCount": 3 },
                    { "id": 11, "title": "Album (Other Edition)", "trackCount": 2 }
                  ]
                },
                "albumReleaseId": 10,
                "quality": {
                  "quality": { "id": 7, "name": "FLAC", "source": "Lossless", "modifier": "None" },
                  "revision": { "version": 1, "real": 0, "isRepack": false }
                },
                "rejections": [],
                "tracks": [
                  { "id": 101, "title": "Song", "trackNumber": "1", "duration": 200000, "hasFile": false }
                ]
              }
            ]
            """;

        var releaseTracks = new Dictionary<int, string>
        {
            [10] = """
                [
                  { "id": 101, "title": "Song", "trackNumber": "1", "duration": 200000, "hasFile": false },
                  { "id": 102, "title": "Second", "trackNumber": "2", "duration": 180000, "hasFile": false },
                  { "id": 103, "title": "Third", "trackNumber": "3", "duration": 240000, "hasFile": false }
                ]
                """,
            [11] = """
                [
                  { "id": 201, "title": "Song", "trackNumber": "7", "duration": 200000, "hasFile": false },
                  { "id": 202, "title": "Bonus", "trackNumber": "3", "duration": 150000, "hasFile": false }
                ]
                """
        };

        using var harness = new Harness(
            manualImportJson: manualImport,
            decisionJson: TestData.DecisionJson("option_3", 0.94),
            releaseTracksJson: releaseTracks);

        await harness.Worker.ProcessJobAsync(Job(), CancellationToken.None);

        var decision = Assert.Single(harness.Temp.Repository.GetDecisions(10));
        Assert.Equal("Import", decision.Action);
        Assert.Equal("Bonus", decision.Chosen);

        // State keeps Lidarr's matched row track; all four offerable tracks live in the question.
        using var state = JsonDocument.Parse(decision.State!);
        Assert.Equal(1, state.RootElement.GetProperty("tracks").GetArrayLength());

        using var questions = JsonDocument.Parse(decision.Questions!);
        var criteria = questions.RootElement.GetProperty("mapping").GetProperty("criteria");
        Assert.Equal(6, criteria.EnumerateObject().Count());
        Assert.True(criteria.TryGetProperty("album_mismatch", out _));

        var command = Assert.Single(harness.LidarrHandler.Requests, r => r.Uri.EndsWith("/api/v1/command"));
        Assert.Contains("\"trackIds\":[202]", command.Body);
        Assert.Contains("\"albumReleaseId\":11", command.Body);
    }

    [Fact]
    public async Task LowConfidence_LeavesToHuman_AndDoesNotMarkDone()
    {
        using var harness = new Harness(decisionJson: TestData.DecisionJson("option_0", 0.2));

        await harness.Worker.ProcessJobAsync(Job(), CancellationToken.None);

        var decision = Assert.Single(harness.Temp.Repository.GetDecisions(10));
        Assert.Equal("LeaveToHuman", decision.Action);
        Assert.Equal("Failed", decision.Status);
        Assert.Contains("below minimum", decision.Error);

        Assert.False(harness.Temp.Repository.IsDownloadDone("dl1"));
        var job = Assert.Single(harness.Temp.Repository.GetJobs(10));
        Assert.Equal("Finished", job.Status);
        Assert.Equal("LeftToHuman", job.Outcome);
        Assert.DoesNotContain(harness.LidarrHandler.Requests, r => r.Method == "POST" && r.Uri.EndsWith("/command"));
    }

    [Fact]
    public async Task NoMatch_RejectsWithBlocklist_AndMarksDone()
    {
        using var harness = new Harness(decisionJson: TestData.DecisionJson("no_match", 0.97));

        await harness.Worker.ProcessJobAsync(Job(), CancellationToken.None);

        var decision = Assert.Single(harness.Temp.Repository.GetDecisions(10));
        Assert.Equal("RejectBlock", decision.Action);
        Assert.Equal("Applied", decision.Status);
        Assert.Equal("no_match", decision.Chosen);

        Assert.True(harness.Temp.Repository.IsDownloadDone("dl1"));
        var job = Assert.Single(harness.Temp.Repository.GetJobs(10));
        Assert.Equal("Rejected", job.Outcome);

        var delete = Assert.Single(harness.LidarrHandler.Requests, r => r.Method == "DELETE");
        Assert.Contains("removeFromClient=true", delete.Uri);
        Assert.Contains("blocklist=true", delete.Uri);
    }

    [Fact]
    public async Task AlbumMismatch_LeavesToHuman_AndDoesNotMarkDone()
    {
        using var harness = new Harness(decisionJson: TestData.DecisionJson("album_mismatch", 0.97));

        await harness.Worker.ProcessJobAsync(Job(), CancellationToken.None);

        var decision = Assert.Single(harness.Temp.Repository.GetDecisions(10));
        Assert.Equal("LeaveToHuman", decision.Action);
        Assert.Equal("Failed", decision.Status);
        Assert.Equal("album_mismatch", decision.Chosen);
        Assert.Contains("mismatch", decision.Error);

        Assert.False(harness.Temp.Repository.IsDownloadDone("dl1"));
        Assert.DoesNotContain(harness.LidarrHandler.Requests, r => r.Method == "DELETE");
        Assert.DoesNotContain(harness.LidarrHandler.Requests, r => r.Method == "POST" && r.Uri.EndsWith("/command"));
    }

    [Fact]
    public async Task NoMatch_WithAlbumMatchRejection_LeavesToHuman_InsteadOfBlocklisting()
    {
        const string manualImport = """
            [
              {
                "id": 1,
                "path": "/downloads/Taylor Swift-Speak Now (Taylor's Version)/22-Taylor_Swift-Timeless_(Taylors_Version)_(From_The_Vault).flac",
                "name": "22-Taylor_Swift-Timeless_(Taylors_Version)_(From_The_Vault)",
                "downloadId": "dl1",
                "additionalFile": false,
                "artist": { "id": 5, "artistName": "Taylor Swift" },
                "album": { "id": 7, "title": "Speak Now" },
                "albumReleaseId": 36385,
                "quality": {
                  "quality": { "id": 7, "name": "FLAC", "source": "Lossless", "modifier": "None" },
                  "revision": { "version": 1, "real": 0, "isRepack": false }
                },
                "rejections": [
                  { "reason": "Album match is not close enough: 78.9 % vs 80 %", "type": "Album" }
                ],
                "audioTags": {
                  "title": "Timeless (Taylor's Version) (From The Vault)",
                  "artistTitle": "Taylor Swift",
                  "albumTitle": "Speak Now (Taylor's Version)",
                  "releaseGroup": "Speak Now (Taylor's Version)",
                  "trackNumbers": [22],
                  "discNumber": 1,
                  "duration": "00:06:15.0000000",
                  "year": 2023
                },
                "tracks": [
                  { "id": 101, "title": "Mine", "trackNumber": "1", "duration": 230000, "hasFile": false }
                ]
              }
            ]
            """;

        using var harness = new Harness(
            manualImportJson: manualImport,
            decisionJson: TestData.DecisionJson("no_match", 0.97));

        await harness.Worker.ProcessJobAsync(Job(), CancellationToken.None);

        var decision = Assert.Single(harness.Temp.Repository.GetDecisions(10));
        Assert.Equal("LeaveToHuman", decision.Action);
        Assert.Equal("Failed", decision.Status);
        Assert.Equal("no_match", decision.Chosen);
        Assert.Contains("uncertain", decision.Error);

        // The model saw Lidarr's own tags, not just the file name.
        using var state = JsonDocument.Parse(decision.State!);
        Assert.Equal("Speak Now (Taylor's Version)",
            state.RootElement.GetProperty("file_tags").GetProperty("album").GetString());
        Assert.Equal("6:15",
            state.RootElement.GetProperty("file_tags").GetProperty("duration").GetString());

        Assert.False(harness.Temp.Repository.IsDownloadDone("dl1"));
        Assert.DoesNotContain(harness.LidarrHandler.Requests, r => r.Method == "DELETE");
        Assert.DoesNotContain(harness.LidarrHandler.Requests, r => r.Method == "POST" && r.Uri.EndsWith("/command"));
    }

    [Fact]
    public async Task DryRun_DoesNotSubmitImport_ButRecordsSkippedDryRun()
    {
        using var harness = new Harness(dryRun: true);

        await harness.Worker.ProcessJobAsync(Job(), CancellationToken.None);

        var decision = Assert.Single(harness.Temp.Repository.GetDecisions(10));
        Assert.Equal("Import", decision.Action);
        Assert.Equal("SkippedDryRun", decision.Status);

        Assert.DoesNotContain(harness.LidarrHandler.Requests, r => r.Method == "POST" && r.Uri.EndsWith("/command"));
        Assert.True(harness.Temp.Repository.IsDownloadDone("dl1"));
    }

    [Fact]
    public async Task AdditionalFile_IsSkippedBySystem_WithoutDecisionCall()
    {
        const string manualImport = """
            [
              {
                "id": 9,
                "path": "/music/Artist/Album/cover.jpg",
                "name": "cover.jpg",
                "downloadId": "dl1",
                "additionalFile": true,
                "tracks": []
              }
            ]
            """;
        using var harness = new Harness(manualImportJson: manualImport);

        await harness.Worker.ProcessJobAsync(Job(), CancellationToken.None);

        var decision = Assert.Single(harness.Temp.Repository.GetDecisions(10));
        Assert.Equal("System", decision.Resolver);
        Assert.Equal("Skip", decision.Action);
        Assert.Equal("Applied", decision.Status);
        Assert.Empty(harness.SystemOneHandler.Requests);
        Assert.True(harness.Temp.Repository.IsDownloadDone("dl1"));
    }

    [Fact]
    public async Task InvalidRequest_LeavesToHuman_AndDoesNotMarkDone()
    {
        using var harness = new Harness(
            decisionErrorStatus: HttpStatusCode.BadRequest,
            decisionErrorBody: """{"detail":"input exceeds 32768 tokens"}""");

        await harness.Worker.ProcessJobAsync(Job(), CancellationToken.None);

        var decision = Assert.Single(harness.Temp.Repository.GetDecisions(10));
        Assert.Equal("LeaveToHuman", decision.Action);
        Assert.Equal("Failed", decision.Status);
        Assert.Contains("HTTP 400", decision.Error);
        Assert.Contains("32768", decision.Error);

        var job = Assert.Single(harness.Temp.Repository.GetJobs(10));
        Assert.Equal("Failed", job.Status);
        Assert.Equal("LeftToHuman", job.Outcome);

        Assert.False(harness.Temp.Repository.IsDownloadDone("dl1"));
    }

    [Fact]
    public async Task ModelUnavailable_Pauses_WithoutWritingDecisionsOrDoneMarker()
    {
        using var harness = new Harness(
            decisionErrorStatus: HttpStatusCode.ServiceUnavailable,
            decisionErrorBody: """{"detail":"model is starting"}""");

        await Assert.ThrowsAsync<SystemOneUnavailableException>(
            () => harness.Worker.ProcessJobAsync(Job(), CancellationToken.None));

        Assert.Empty(harness.Temp.Repository.GetDecisions(10));
        Assert.Empty(harness.Temp.Repository.GetJobs(10));
        Assert.False(harness.Temp.Repository.IsDownloadDone("dl1"));
        Assert.DoesNotContain(harness.LidarrHandler.Requests, r => r.Method == "POST" && r.Uri.EndsWith("/command"));

        var (available, reason) = harness.Availability.Snapshot();
        Assert.False(available);
        Assert.Contains("503", reason);
    }

    [Fact]
    public async Task ExistingDoneImport_IsNotReimported()
    {
        using var harness = new Harness();
        harness.Temp.Repository.InsertDecision(new DecisionRecord
        {
            DownloadId = "dl1", RowId = 1, Action = "Import", Status = "Done"
        });

        await harness.Worker.ProcessJobAsync(Job(), CancellationToken.None);

        Assert.Empty(harness.SystemOneHandler.Requests);
        Assert.DoesNotContain(harness.LidarrHandler.Requests, r => r.Method == "POST" && r.Uri.EndsWith("/command"));
        Assert.True(harness.Temp.Repository.IsDownloadDone("dl1"));
    }

    [Fact]
    public async Task ExistingLeaveToHuman_IsRetried()
    {
        using var harness = new Harness(dryRun: true);
        harness.Temp.Repository.InsertDecision(new DecisionRecord
        {
            DownloadId = "dl1", RowId = 1, Action = "LeaveToHuman", Status = "Applied"
        });

        await harness.Worker.ProcessJobAsync(Job(), CancellationToken.None);

        Assert.Single(harness.SystemOneHandler.Requests);
        var latest = harness.Temp.Repository.FindLatest("dl1", 1);
        Assert.NotNull(latest);
        Assert.Equal("Import", latest!.Action);
    }

    private static ImportJob Job() => new("dl1", 1, "Artist - Album", DateTime.UtcNow);

    private sealed class Harness : IDisposable
    {
        private readonly TempRepository _temp = new();

        public Harness(
            bool dryRun = false,
            string? decisionJson = null,
            HttpStatusCode? decisionErrorStatus = null,
            string? decisionErrorBody = null,
            string? manualImportJson = null,
            IReadOnlyDictionary<int, string>? releaseTracksJson = null)
        {
            var options = new TestOptionsMonitor<LidarrOptions>(TestData.Options(dryRun: dryRun));

            LidarrHandler = new StubHandler((request, _) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (request.Method == HttpMethod.Get && path.EndsWith("/manualimport"))
                    return StubHandler.Json(manualImportJson ?? TestData.ManualImportJson);
                if (request.Method == HttpMethod.Get && path.EndsWith("/track"))
                {
                    var query = request.RequestUri.Query;
                    var value = query.Split("albumReleaseId=", StringSplitOptions.RemoveEmptyEntries)
                        .ElementAtOrDefault(1)?.Split('&')[0];
                    if (int.TryParse(value, out var releaseId)
                        && releaseTracksJson?.TryGetValue(releaseId, out var json) == true)
                        return StubHandler.Json(json);
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }
                if (request.Method == HttpMethod.Post && path.EndsWith("/command"))
                    return StubHandler.Json("{}", HttpStatusCode.Created);
                if (request.Method == HttpMethod.Delete && path.Contains("/queue/"))
                    return new HttpResponseMessage(HttpStatusCode.OK);
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            SystemOneHandler = new StubHandler((_, _) => decisionErrorStatus is { } status
                ? StubHandler.Json(decisionErrorBody ?? """{"detail":"decision failed"}""", status)
                : StubHandler.Json(decisionJson ?? TestData.DecisionJson()));

            var lidarr = new LidarrClient(
                new HttpClient(LidarrHandler) { BaseAddress = new Uri("http://lidarr/") },
                NullLogger<LidarrClient>.Instance);
            var systemOne = new SystemOneDecisionClient(
                new HttpClient(SystemOneHandler) { BaseAddress = new Uri("http://mica:8010/") },
                options,
                NullLogger<SystemOneDecisionClient>.Instance);

            Broadcaster = new RecordingBroadcaster();
            Jobs = new JobQueue();
            Availability = new SystemOneAvailability();
            Availability.ReportAvailable();

            var progress = new WorkerProgress(Availability);
            var writer = new DecisionWriter(_temp.Repository, Broadcaster);

            Worker = new JobWorker(
                lidarr, systemOne, new ImportContextBuilder(NullLogger<ImportContextBuilder>.Instance),
                _temp.Repository, Jobs, writer, progress, Availability, Broadcaster, options,
                NullLogger<JobWorker>.Instance);
        }

        public TempRepository Temp => _temp;
        public StubHandler LidarrHandler { get; }
        public StubHandler SystemOneHandler { get; }
        public RecordingBroadcaster Broadcaster { get; }
        public JobQueue Jobs { get; }
        public SystemOneAvailability Availability { get; }
        public JobWorker Worker { get; }

        public void Dispose() => _temp.Dispose();
    }
}
