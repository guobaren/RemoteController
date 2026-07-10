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
}
