using ArrImportSystemOne.Queue;

namespace ArrImportSystemOne.Tests;

public sealed class JobQueueTests
{
    private static ImportJob Job(string id) => new(id, 1, $"title {id}", DateTime.UtcNow);

    [Fact]
    public async Task TryEnqueue_DedupesInFlightDownload()
    {
        var queue = new JobQueue();

        Assert.True(queue.TryEnqueue(Job("dl1")));
        Assert.False(queue.TryEnqueue(Job("dl1")));
        Assert.True(queue.TryEnqueue(Job("dl2")));

        Assert.Equal(2, queue.InFlightCount);
        Assert.Equal("dl1", (await queue.DequeueAsync(CancellationToken.None)).DownloadId);
    }

    [Fact]
    public async Task Complete_ReleasesInFlightAndAllowsReEnqueue()
    {
        var queue = new JobQueue();
        queue.TryEnqueue(Job("dl1"));
        var job = await queue.DequeueAsync(CancellationToken.None);

        queue.Complete(job.DownloadId);

        Assert.False(queue.IsInFlight("dl1"));
        Assert.True(queue.TryEnqueue(Job("dl1")));
    }

    [Fact]
    public void Backoff_IsSetAndCleared()
    {
        var queue = new JobQueue();

        queue.ScheduleRetry("dl1", TimeSpan.FromSeconds(60));
        Assert.True(queue.IsBackedOff("dl1"));

        queue.ClearRetry("dl1");
        Assert.False(queue.IsBackedOff("dl1"));
    }

    [Fact]
    public void PendingSnapshot_ContainsQueuedJobs()
    {
        var queue = new JobQueue();
        queue.TryEnqueue(Job("dl1"));
        queue.TryEnqueue(Job("dl2"));

        var pending = queue.PendingSnapshot();

        Assert.Equal(2, pending.Count);
        Assert.Equal(new[] { "dl1", "dl2" }, pending.Select(j => j.DownloadId));
    }
}
