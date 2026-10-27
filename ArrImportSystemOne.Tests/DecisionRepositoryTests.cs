using ArrImportSystemOne.Store;

namespace ArrImportSystemOne.Tests;

public sealed class DecisionRepositoryTests
{
    [Fact]
    public void InsertDecision_FindLatest_ReturnsMostRecentRow()
    {
        using var temp = new TempRepository();
        var repo = temp.Repository;

        var first = repo.InsertDecision(new DecisionRecord
        {
            DownloadId = "dl1", RowId = 1, FileName = "a.flac", Action = "Import", Status = "Done"
        });
        var second = repo.InsertDecision(new DecisionRecord
        {
            DownloadId = "dl1", RowId = 1, FileName = "a.flac", Action = "Import", Status = "Restarted"
        });

        var latest = repo.FindLatest("dl1", 1);

        Assert.NotNull(latest);
        Assert.Equal(second, latest!.Id);
        Assert.NotEqual(first, second);
        Assert.Equal("Restarted", latest.Status);
    }

    [Fact]
    public void DoneMarker_CanBeSetClearedAndQueried()
    {
        using var temp = new TempRepository();
        var repo = temp.Repository;

        repo.MarkDone("dl1");
        Assert.True(repo.IsDownloadDone("dl1"));

        repo.ClearDone("dl1");
        Assert.False(repo.IsDownloadDone("dl1"));
    }

    [Fact]
    public void DryRunDoneMarker_OnlyBlocksWhileDryRunIsEnabled()
    {
        using var temp = new TempRepository();
        var repo = temp.Repository;

        repo.MarkDone("dl1", dryRun: true);

        Assert.True(repo.IsDownloadDone("dl1", dryRun: true));
        Assert.False(repo.IsDownloadDone("dl1", dryRun: false));

        // A real completion upgrades the marker so it stays done in either mode.
        repo.MarkDone("dl1", dryRun: false);
        Assert.True(repo.IsDownloadDone("dl1", dryRun: true));
        Assert.True(repo.IsDownloadDone("dl1", dryRun: false));
    }

    [Fact]
    public void LegacyDryRunMarker_IsMigratedOnStartup()
    {
        using var temp = new TempRepository();
        temp.Repository.InsertDecision(new DecisionRecord
        {
            DownloadId = "dl1", RowId = 1, Action = "Import", Status = "SkippedDryRun"
        });

        // Simulate a marker written before DoneRecord knew about dry-run.
        temp.Repository.MarkDone("dl1", dryRun: false);

        using var reopened = temp.OpenRepository();

        Assert.True(reopened.IsDownloadDone("dl1", dryRun: true));
        Assert.False(reopened.IsDownloadDone("dl1", dryRun: false));
    }

    [Fact]
    public void FlagRestarted_UpdatesEveryDecisionForDownload()
    {
        using var temp = new TempRepository();
        var repo = temp.Repository;

        repo.InsertDecision(new DecisionRecord { DownloadId = "dl1", RowId = 1, Status = "Done" });
        repo.InsertDecision(new DecisionRecord { DownloadId = "dl1", RowId = 2, Status = "Applied" });
        repo.InsertDecision(new DecisionRecord { DownloadId = "other", RowId = 3, Status = "Done" });

        var flagged = repo.FlagRestarted("dl1");

        Assert.Equal(2, flagged);
        Assert.Equal("Restarted", repo.FindLatest("dl1", 1)!.Status);
        Assert.Equal("Restarted", repo.FindLatest("dl1", 2)!.Status);
        Assert.Equal("Done", repo.FindLatest("other", 3)!.Status);
    }

    [Fact]
    public void Jobs_AreStoredAndReturnedNewestFirst()
    {
        using var temp = new TempRepository();
        var repo = temp.Repository;

        repo.InsertJob(new JobRecord { DownloadId = "dl1", Status = "Finished", Outcome = "Imported" });
        repo.InsertJob(new JobRecord { DownloadId = "dl2", Status = "Failed", Outcome = "None" });

        var jobs = repo.GetJobs(10);

        Assert.Equal(2, jobs.Count);
        Assert.Equal("dl2", jobs[0].DownloadId);
        Assert.Equal("dl1", jobs[1].DownloadId);
    }

    [Fact]
    public void GetDecisions_RespectsLimitAndNewestFirst()
    {
        using var temp = new TempRepository();
        var repo = temp.Repository;

        for (var i = 0; i < 5; i++)
            repo.InsertDecision(new DecisionRecord { DownloadId = "dl1", RowId = i });

        var decisions = repo.GetDecisions(3);

        Assert.Equal(3, decisions.Count);
        Assert.True(decisions[0].Id > decisions[1].Id);
        Assert.True(decisions[1].Id > decisions[2].Id);
    }
}
