using Rc.Agent.Persistence;
using Rc.Contracts;
using Xunit;

namespace Rc.Agent.Tests.Persistence;

public sealed class JobSnapshotStoreTests
{
    [Fact]
    public async Task SaveJobSnapshotAsyncRoundTripsAllSnapshotFields()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(directory.Path);
        await store.InitializeAsync();
        var snapshot = new JobSnapshot(
            "job-17",
            JobState.FailedToStart,
            23,
            new DateTimeOffset(2026, 7, 10, 4, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 10, 4, 1, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 10, 4, 2, 0, TimeSpan.Zero),
            new RemoteError(ErrorCode.FailedPrecondition, "executable is unavailable", Retryable: false));

        await store.SaveJobSnapshotAsync(snapshot);
        var loaded = await store.GetJobSnapshotAsync(snapshot.JobId);

        Assert.Equal(snapshot, loaded);
    }

    [Fact]
    public async Task ListJobSnapshotsAsyncFiltersAndUsesStableCreationOrder()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(directory.Path);
        await store.InitializeAsync();
        var createdAt = DateTimeOffset.UnixEpoch;
        await store.SaveJobSnapshotAsync(new JobSnapshot("job-b", JobState.Running, null, createdAt, createdAt, null, null));
        await store.SaveJobSnapshotAsync(new JobSnapshot("job-a", JobState.Running, null, createdAt, createdAt, null, null));
        await store.SaveJobSnapshotAsync(new JobSnapshot("job-finished", JobState.Exited, 0, createdAt.AddSeconds(1), createdAt, createdAt.AddSeconds(2), null));

        var running = await store.ListJobSnapshotsAsync(JobState.Running);
        var all = await store.ListJobSnapshotsAsync();

        Assert.Equal(["job-a", "job-b"], running.Select(job => job.JobId));
        Assert.Equal(["job-a", "job-b", "job-finished"], all.Select(job => job.JobId));
    }
}
