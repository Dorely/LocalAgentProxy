namespace LocalAgentProxy.Tests;

public class SchedulerTests
{
    [Fact]
    public async Task RoundRobinAndCancellationReleaseCapacity()
    {
        var scheduler = new FairScheduler(8);
        var first = await scheduler.AcquireAsync("A", default);
        var a1 = scheduler.AcquireAsync("A", default);
        var a2 = scheduler.AcquireAsync("A", default);
        var b = scheduler.AcquireAsync("B", default);
        first.Dispose();
        var second = await a1;
        Assert.False(b.IsCompleted);
        second.Dispose();
        var third = await b;
        Assert.False(a2.IsCompleted);
        third.Dispose(); (await a2).Dispose();
        Assert.False(scheduler.Active);
        using var active = await scheduler.AcquireAsync("A", default);
        using var cancel = new CancellationTokenSource();
        var canceled = scheduler.AcquireAsync("B", cancel.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        active.Dispose();
        using var after = await scheduler.AcquireAsync("C", default);
        Assert.Equal(0, scheduler.Waiting);
    }
    [Fact]
    public async Task QueueIsBounded()
    {
        var scheduler = new FairScheduler(1);
        using var first = await scheduler.AcquireAsync("A", default);
        var queued = scheduler.AcquireAsync("B", default);
        await Assert.ThrowsAsync<ProxyException>(() => scheduler.AcquireAsync("C", default));
        first.Dispose(); (await queued).Dispose();
    }
}
