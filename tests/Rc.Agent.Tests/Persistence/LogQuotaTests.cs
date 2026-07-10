using Rc.Agent.Persistence;
using Rc.Contracts;
using Xunit;

namespace Rc.Agent.Tests.Persistence;

public sealed class LogQuotaTests
{
    [Fact]
    public async Task EnforceLogQuotaAsyncEvictsOldestCompletedLogsAndKeepsRunningTaskTails()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(directory.Path);
        await store.InitializeAsync();
        var baseTime = new DateTimeOffset(2026, 7, 10, 4, 0, 0, TimeSpan.Zero);
        await store.SaveJobSnapshotAsync(new JobSnapshot("completed-old", JobState.Exited, 0, baseTime, baseTime, baseTime, null));
        await store.SaveJobSnapshotAsync(new JobSnapshot("running", JobState.Running, null, baseTime.AddMinutes(1), baseTime.AddMinutes(1), null, null));
        await store.SaveJobSnapshotAsync(new JobSnapshot("completed-new", JobState.Exited, 0, baseTime.AddMinutes(2), baseTime.AddMinutes(2), baseTime.AddMinutes(2), null));

        var oldSegment = await store.AppendOutputSegmentAsync("completed-old", JobOutputKind.Stdout, 0, new byte[80]);
        var runningTail = await store.AppendOutputSegmentAsync("running", JobOutputKind.Stdout, 0, new byte[80]);
        var newSegment = await store.AppendOutputSegmentAsync("completed-new", JobOutputKind.Stdout, 0, new byte[40]);

        var result = await store.EnforceLogQuotaAsync(150);

        Assert.Equal(80, result.RemovedBytes);
        Assert.Equal(120, result.RetainedBytes);
        Assert.False(File.Exists(Path.Combine(directory.Path, oldSegment.RelativePath)));
        Assert.True(File.Exists(Path.Combine(directory.Path, runningTail.RelativePath)));
        Assert.True(File.Exists(Path.Combine(directory.Path, newSegment.RelativePath)));
        Assert.Equal(80, new FileInfo(Path.Combine(directory.Path, runningTail.RelativePath)).Length);
    }
    [Fact]
    public async Task EnforceLogQuotaAsyncTrimsOldestRunningSegmentsAndKeepsTheMostRecentTail()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(directory.Path);
        await store.InitializeAsync();
        var createdAt = new DateTimeOffset(2026, 7, 10, 4, 0, 0, TimeSpan.Zero);
        await store.SaveJobSnapshotAsync(new JobSnapshot("running", JobState.Running, null, createdAt, createdAt, null, null));

        var oldest = await store.AppendOutputSegmentAsync("running", JobOutputKind.Stdout, 0, new byte[80]);
        await Task.Delay(2);
        var newest = await store.AppendOutputSegmentAsync("running", JobOutputKind.Stdout, 80, new byte[40]);

        var result = await store.EnforceLogQuotaAsync(100);

        Assert.Equal(80, result.RemovedBytes);
        Assert.Equal(40, result.RetainedBytes);
        Assert.Empty(result.EvictedJobIds);
        Assert.False(File.Exists(Path.Combine(directory.Path, oldest.RelativePath)));
        Assert.True(File.Exists(Path.Combine(directory.Path, newest.RelativePath)));
    }

}