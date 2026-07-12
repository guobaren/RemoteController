using System.Collections.Concurrent;
using Rc.Agent.Jobs;
using Rc.Contracts;
using Xunit;

namespace Rc.Agent.Tests.Jobs;

public sealed class JobSchedulerTests
{
    [Fact]
    public async Task NormalAndElevatedQueuesUseIndependentConcurrencyLimits()
    {
        await using var scheduler = new JobScheduler(normalConcurrency: 1, elevatedConcurrency: 1);
        var releaseNormal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var normalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var elevatedStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstNormal = scheduler.EnqueueAsync(ExecutionIdentity.CurrentUser, async _ =>
        {
            normalStarted.TrySetResult();
            await releaseNormal.Task;
        });
        await normalStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var secondNormalStarted = false;
        var secondNormal = scheduler.EnqueueAsync(ExecutionIdentity.CurrentUser, _ =>
        {
            secondNormalStarted = true;
            return Task.CompletedTask;
        });
        var elevated = scheduler.EnqueueAsync(ExecutionIdentity.ElevatedBroker, _ =>
        {
            elevatedStarted.TrySetResult();
            return Task.CompletedTask;
        });

        await elevatedStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(secondNormalStarted);
        releaseNormal.TrySetResult();
        await Task.WhenAll(firstNormal, secondNormal, elevated);
        Assert.True(secondNormalStarted);
    }

    [Fact]
    public async Task SingleWorkerStartsQueuedJobsInFifoOrder()
    {
        await using var scheduler = new JobScheduler(normalConcurrency: 1, elevatedConcurrency: 1);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new ConcurrentQueue<int>();

        var first = scheduler.EnqueueAsync(ExecutionIdentity.CurrentUser, async _ =>
        {
            order.Enqueue(1);
            firstStarted.TrySetResult();
            await releaseFirst.Task;
        });
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = scheduler.EnqueueAsync(ExecutionIdentity.CurrentUser, _ =>
        {
            order.Enqueue(2);
            return Task.CompletedTask;
        });
        var third = scheduler.EnqueueAsync(ExecutionIdentity.CurrentUser, _ =>
        {
            order.Enqueue(3);
            return Task.CompletedTask;
        });

        releaseFirst.TrySetResult();
        await Task.WhenAll(first, second, third).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal([1, 2, 3], order.ToArray());
    }

    [Fact]
    public async Task CancellationPropagatesToWorkThatAlreadyStarted()
    {
        await using var scheduler = new JobScheduler(normalConcurrency: 1, elevatedConcurrency: 1);
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var work = scheduler.EnqueueAsync(ExecutionIdentity.CurrentUser, async token =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
    }
}
