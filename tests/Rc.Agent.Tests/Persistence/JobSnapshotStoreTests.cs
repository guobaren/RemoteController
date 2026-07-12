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
            new RemoteError(ErrorCode.FailedPrecondition, "executable is unavailable", Retryable: false),
            ExecutionIdentity.ElevatedBroker,
            OutputTruncated: true);

        await store.SaveJobSnapshotAsync(snapshot);
        var loaded = await store.GetJobSnapshotAsync(snapshot.JobId);

        Assert.Equal(snapshot, loaded);
    }

    [Fact]
    public async Task FirstTerminalStateWinsAgainstLateCompetingTerminalWrites()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(directory.Path);
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var exited = new JobSnapshot("job-race", JobState.Exited, 0, now, now, now, null);
        var cancelled = exited with { State = JobState.Cancelled, ExitCode = 1 };

        await store.SaveJobSnapshotAsync(exited);
        await store.SaveJobSnapshotAsync(cancelled);

        Assert.Equal(JobState.Exited, (await store.GetJobSnapshotAsync("job-race"))!.State);
    }
    [Fact]
    public async Task ConcurrentCompetingTerminalWritesKeepOneAtomicWinner()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(directory.Path);
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        await store.SaveJobSnapshotAsync(new JobSnapshot("job-concurrent-race", JobState.Running, null, now, now, null, null));
        var exited = new JobSnapshot("job-concurrent-race", JobState.Exited, 0, now, now, now, null);
        var cancelled = exited with { State = JobState.Cancelled, ExitCode = 1 };

        await Task.WhenAll(
            Enumerable.Range(0, 16).Select(index =>
                store.SaveJobSnapshotAsync(index % 2 == 0 ? exited : cancelled)));

        var loaded = await store.GetJobSnapshotAsync("job-concurrent-race");
        Assert.Contains(loaded!.State, new[] { JobState.Exited, JobState.Cancelled });
        Assert.Equal(loaded.State == JobState.Exited ? 0 : 1, loaded.ExitCode);
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
