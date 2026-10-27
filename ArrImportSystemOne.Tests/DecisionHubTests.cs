using ArrImportSystemOne.Hub;
using ArrImportSystemOne.Options;
using ArrImportSystemOne.Solver;
using ArrImportSystemOne.Store;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArrImportSystemOne.Tests;

public sealed class DecisionHubTests
{
    [Fact]
    public void GetSnapshot_ReturnsDecisionsJobsAndQueue()
    {
        using var temp = new TempRepository();
        var repo = temp.Repository;
        repo.InsertDecision(new DecisionRecord { DownloadId = "dl1", RowId = 1, FileName = "a.flac" });
        repo.InsertJob(new JobRecord { DownloadId = "dl1", Status = "Finished", Outcome = "Imported" });

        var hub = CreateHub(repo);
        var snapshot = hub.GetSnapshot(200);

        Assert.Single(snapshot.Decisions);
        Assert.Single(snapshot.Jobs);
        Assert.Equal(0, snapshot.Queue.PendingCount);
    }

    [Fact]
    public void GetDecisionDetail_ReturnsPersistedRequestAndResult()
    {
        using var temp = new TempRepository();
        var repo = temp.Repository;
        var id = repo.InsertDecision(new DecisionRecord
        {
            DownloadId = "dl1",
            RowId = 1,
            FileName = "a.flac",
            Action = "Import",
            Status = "Done",
            State = """{"file_name":"01 - Song.flac","artist":"Artist"}""",
            Questions = """{"mapping":{"type":"choice","criteria":{"option_0":"title: 'Song'","no_match":"none"}}}""",
            ModelAnswer = """{"choice":"option_0","confidence":0.94,"probabilities":{"option_0":0.94},"latencyMs":42}"""
        });

        var hub = CreateHub(repo);
        var detail = hub.GetDecisionDetail(id);

        Assert.NotNull(detail);
        Assert.Equal("a.flac", detail!.Decision.FileName);
        Assert.NotNull(detail.Request?.State);
        Assert.Equal("01 - Song.flac", detail.Request!.State!.Value.GetProperty("file_name").GetString());
        Assert.NotNull(detail.Request.Questions);
        Assert.True(detail.Request.Questions!.Value.GetProperty("mapping").GetProperty("criteria")
            .TryGetProperty("no_match", out _));
        Assert.NotNull(detail.Result);
        Assert.Equal("option_0", detail.Result!.Value.GetProperty("choice").GetString());
    }

    [Fact]
    public void GetDecisionDetail_UnknownId_ReturnsNull()
    {
        using var temp = new TempRepository();
        var hub = CreateHub(temp.Repository);

        Assert.Null(hub.GetDecisionDetail(404));
    }

    [Fact]
    public void RestartAlbum_ClearsDoneMarkerAndFlagsDecisions()
    {
        using var temp = new TempRepository();
        var repo = temp.Repository;
        repo.MarkDone("dl1");
        repo.InsertDecision(new DecisionRecord { DownloadId = "dl1", RowId = 1, Status = "Done", Action = "Import" });

        var hub = CreateHub(repo);

        Assert.True(hub.RestartAlbum("dl1"));
        Assert.False(repo.IsDownloadDone("dl1"));
        Assert.Equal("Restarted", repo.FindLatest("dl1", 1)!.Status);
        Assert.False(hub.RestartAlbum(""));
    }

    private static DecisionHub CreateHub(DecisionRepository repo) =>
        new(repo,
            new Queue.WorkerProgress(
                new SystemOneAvailability(),
                new TestOptionsMonitor<LidarrOptions>(TestData.Options())),
            new Queue.PollNotifier(),
            NullLogger<DecisionHub>.Instance);
}
