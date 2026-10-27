using ArrImportSystemOne.Options;
using ArrImportSystemOne.Queue;
using ArrImportSystemOne.Solver;

namespace ArrImportSystemOne.Tests;

public sealed class SystemOneAvailabilityTests
{
    [Fact]
    public void StartsPaused_UntilFirstHealthCheck()
    {
        var availability = new SystemOneAvailability();

        var (available, reason) = availability.Snapshot();

        Assert.False(available);
        Assert.Contains("health check", reason);
    }

    [Fact]
    public async Task ReportAvailable_ReleasesWaiter()
    {
        var availability = new SystemOneAvailability();
        var waiter = availability.WaitUntilAvailableAsync(CancellationToken.None);

        Assert.False(waiter.IsCompleted);

        availability.ReportAvailable();

        await waiter.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(availability.Snapshot().Available);
    }

    [Fact]
    public async Task ReportUnavailable_ClosesGate()
    {
        var availability = new SystemOneAvailability();
        availability.ReportAvailable();
        availability.ReportUnavailable("provider offline");

        var (available, reason) = availability.Snapshot();
        Assert.False(available);
        Assert.Equal("provider offline", reason);

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => availability.WaitUntilAvailableAsync(timeout.Token));
    }

    [Fact]
    public void QueueUpdated_ReflectsAvailability()
    {
        var availability = new SystemOneAvailability();
        var options = new TestOptionsMonitor<LidarrOptions>(TestData.Options(dryRun: true));
        var progress = new WorkerProgress(availability, options);

        var paused = progress.BuildQueueUpdated();
        Assert.True(paused.Paused);
        Assert.True(paused.DryRun);
        Assert.Contains("health check", paused.PauseReason);

        availability.ReportAvailable();
        Assert.False(progress.BuildQueueUpdated().Paused);

        availability.ReportUnavailable("model warming up");
        var unavailable = progress.BuildQueueUpdated();
        Assert.True(unavailable.Paused);
        Assert.Equal("model warming up", unavailable.PauseReason);
    }
}
