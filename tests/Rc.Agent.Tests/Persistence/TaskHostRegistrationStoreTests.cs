using Rc.Agent.Persistence;
using Rc.Agent.Tests.Persistence;
using Rc.Contracts;
using Xunit;

namespace Rc.Agent.Tests.Persistence;

public sealed class TaskHostRegistrationStoreTests
{
    [Fact]
    public async Task RegistrationRoundTripsUpdatesAndDeletes()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(directory.Path);
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        await store.SaveJobSnapshotAsync(new JobSnapshot("job-1", JobState.Queued, null, now, null, null, null));
        await store.SaveTaskHostRegistrationAsync(new TaskHostRegistrationInfo("job-1", "pipe-1", null, now));
        await store.SaveTaskHostRegistrationAsync(new TaskHostRegistrationInfo("job-1", "pipe-1", 42, now.AddSeconds(1)));

        var registration = Assert.Single(await store.ListTaskHostRegistrationsAsync());
        Assert.Equal(42, registration.ProcessId);
        await store.DeleteTaskHostRegistrationAsync("job-1");
        Assert.Empty(await store.ListTaskHostRegistrationsAsync());
    }
}