using System.Net;
using ArrImportSystemOne.Lidarr;
using ArrImportSystemOne.Options;
using ArrImportSystemOne.Queue;
using ArrImportSystemOne.Solver;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArrImportSystemOne.Tests;

public sealed class QueuePollerTests
{
    private const string QueueJson = """
        {
          "page": 1,
          "pageSize": 200,
          "totalRecords": 1,
          "records": [
            {
              "id": 1,
              "title": "Artist - Album",
              "downloadId": "dl1",
              "status": "completed",
              "trackedDownloadState": "importPending"
            }
          ]
        }
        """;

    [Fact]
    public async Task ImportableRow_EnqueuesJob_AndDoesNotMarkDone()
    {
        using var harness = new Harness(QueueJson, TestData.ManualImportJson);

        await harness.Poller.PollOnceAsync(CancellationToken.None);

        Assert.True(harness.Jobs.IsInFlight("dl1"));
        Assert.Equal(1, harness.Jobs.PendingCount);
        Assert.False(harness.Repository.IsDownloadDone("dl1"));
        Assert.Empty(harness.Repository.GetDecisions(10));
        Assert.Equal(1, harness.QueueRequests);
    }

    [Fact]
    public async Task AdditionalFileOnly_WritesSkipDecision_AndMarksDone()
    {
        const string additionalOnly = """
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
        using var harness = new Harness(QueueJson, additionalOnly);

        await harness.Poller.PollOnceAsync(CancellationToken.None);

        Assert.False(harness.Jobs.IsInFlight("dl1"));
        Assert.True(harness.Repository.IsDownloadDone("dl1"));

        var decision = Assert.Single(harness.Repository.GetDecisions(10));
        Assert.Equal("System", decision.Resolver);
        Assert.Equal("Skip", decision.Action);
        Assert.Single(harness.Repository.GetJobs(10));
    }

    [Fact]
    public async Task DoneDownload_IsNotLookedUpOrEnqueued()
    {
        using var harness = new Harness(QueueJson, TestData.ManualImportJson);
        harness.Repository.MarkDone("dl1");

        await harness.Poller.PollOnceAsync(CancellationToken.None);

        Assert.False(harness.Jobs.IsInFlight("dl1"));
        Assert.Equal(0, harness.ManualImportRequests);
    }

    [Fact]
    public async Task EmptyRows_WhileDownloading_AreLeftForLater()
    {
        using var harness = new Harness(QueueJson.Replace("importPending", "downloading"), "[]");

        await harness.Poller.PollOnceAsync(CancellationToken.None);

        Assert.False(harness.Repository.IsDownloadDone("dl1"));
        Assert.Equal(0, harness.DeleteRequests);
    }

    [Fact]
    public async Task EmptyRows_WhenStale_AreCleanedUpAndMarkedDone()
    {
        using var harness = new Harness(QueueJson.Replace("importPending", "failed"), "[]");

        await harness.Poller.PollOnceAsync(CancellationToken.None);

        Assert.True(harness.Repository.IsDownloadDone("dl1"));
        Assert.Equal(1, harness.DeleteRequests);
    }

    private sealed class Harness : IDisposable
    {
        private readonly TempRepository _temp = new();

        public Harness(string queueJson, string manualImportJson)
        {
            var options = new TestOptionsMonitor<LidarrOptions>(TestData.Options());
            Handler = new StubHandler((request, _) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (request.Method == HttpMethod.Get && path.EndsWith("/queue"))
                    return StubHandler.Json(queueJson);
                if (request.Method == HttpMethod.Get && path.EndsWith("/manualimport"))
                    return StubHandler.Json(manualImportJson);
                if (request.Method == HttpMethod.Delete)
                    return new HttpResponseMessage(HttpStatusCode.OK);
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            var lidarr = new LidarrClient(
                new HttpClient(Handler) { BaseAddress = new Uri("http://lidarr/") },
                NullLogger<LidarrClient>.Instance);

            Broadcaster = new RecordingBroadcaster();
            Jobs = new JobQueue();
            var progress = new WorkerProgress(new SystemOneAvailability());
            var writer = new DecisionWriter(_temp.Repository, Broadcaster);

            Poller = new QueuePoller(
                lidarr, _temp.Repository, Jobs, writer, progress, Broadcaster,
                new PollNotifier(), options, NullLogger<QueuePoller>.Instance);
        }

        public StubHandler Handler { get; }
        public RecordingBroadcaster Broadcaster { get; }
        public JobQueue Jobs { get; }
        public TempRepository Temp => _temp;
        public Store.DecisionRepository Repository => _temp.Repository;
        public QueuePoller Poller { get; }

        public int QueueRequests => Handler.Requests.Count(r => r.Uri.Contains("/api/v1/queue?"));
        public int ManualImportRequests => Handler.Requests.Count(r => r.Uri.Contains("/manualimport"));
        public int DeleteRequests => Handler.Requests.Count(r => r.Method == "DELETE");

        public void Dispose() => _temp.Dispose();
    }
}
