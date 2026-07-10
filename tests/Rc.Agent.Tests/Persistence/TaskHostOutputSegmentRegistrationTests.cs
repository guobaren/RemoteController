using Microsoft.Data.Sqlite;
using Rc.Agent.Persistence;
using Rc.Contracts;
using Rc.TaskHost;
using Xunit;

namespace Rc.Agent.Tests.Persistence;

public sealed class TaskHostOutputSegmentRegistrationTests
{
    [Fact]
    public async Task RegisterTaskHostOutputSegmentsAsyncScansBothStreamsAndIsIdempotent()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(directory.Path);
        await store.InitializeAsync();
        var createdAt = new DateTimeOffset(2026, 7, 10, 8, 0, 0, TimeSpan.Zero);
        await store.SaveJobSnapshotAsync(new JobSnapshot("job-1", JobState.Running, null, createdAt, createdAt, null, null));

        WriteTaskHostSegment(directory.Path, "job-1", "stdout", 12, [1, 2, 3]);
        WriteTaskHostSegment(directory.Path, "job-1", "stdout", 0, [4, 5]);
        WriteTaskHostSegment(directory.Path, "job-1", "stderr", 0, [6]);
        WriteTaskHostSegment(directory.Path, "job-1", "stdout", 99, [7], "not-a-valid-file-name.seg");

        var firstScan = await store.RegisterTaskHostOutputSegmentsAsync("job-1");
        var secondScan = await store.RegisterTaskHostOutputSegmentsAsync("job-1");
        var allSegments = await store.ListOutputSegmentsAsync("job-1");
        var stdoutSegments = await store.ListOutputSegmentsAsync("job-1", JobOutputKind.Stdout);

        Assert.Collection(
            firstScan,
            segment => AssertSegment(segment, JobOutputKind.Stdout, 0, 2),
            segment => AssertSegment(segment, JobOutputKind.Stdout, 12, 3),
            segment => AssertSegment(segment, JobOutputKind.Stderr, 0, 1));
        Assert.Equal(firstScan.Select(segment => segment.SegmentId), secondScan.Select(segment => segment.SegmentId));
        Assert.Equal(firstScan.Select(segment => segment.SegmentId), allSegments.Select(segment => segment.SegmentId));
        Assert.Collection(
            stdoutSegments,
            segment => AssertSegment(segment, JobOutputKind.Stdout, 0, 2),
            segment => AssertSegment(segment, JobOutputKind.Stdout, 12, 3));
    }

    [Fact]
    public async Task RegisterTaskHostOutputSegmentsAsyncMatchesAnAlreadyRegisteredTaskHostSegment()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(directory.Path);
        await store.InitializeAsync();
        var createdAt = new DateTimeOffset(2026, 7, 10, 8, 0, 0, TimeSpan.Zero);
        await store.SaveJobSnapshotAsync(new JobSnapshot("job-1", JobState.Running, null, createdAt, createdAt, null, null));

        var writer = new TaskHostSegmentWriter(directory.Path);
        var written = await writer.WriteAsync("job-1", JobOutputKind.Stdout, 0, new byte[] { 1, 2, 3 });
        var registeredLive = await store.RegisterOutputSegmentAsync(written);

        var registeredAfterRestart = Assert.Single(await store.RegisterTaskHostOutputSegmentsAsync("job-1"));

        Assert.Equal(registeredLive.SegmentId, registeredAfterRestart.SegmentId);
        Assert.Equal(registeredLive.CreatedAtUtc, registeredAfterRestart.CreatedAtUtc);
    }

    [Fact]
    public async Task RegisterOutputSegmentAsyncRejectsPathEscapesAndLengthMismatches()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(directory.Path);
        await store.InitializeAsync();
        var createdAt = new DateTimeOffset(2026, 7, 10, 8, 0, 0, TimeSpan.Zero);
        await store.SaveJobSnapshotAsync(new JobSnapshot("job-1", JobState.Running, null, createdAt, createdAt, null, null));

        var segmentPath = WriteTaskHostSegment(directory.Path, "job-1", "stdout", 0, [1, 2, 3]);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.RegisterOutputSegmentAsync(
            new TaskOutputSegment("job-1", JobOutputKind.Stdout, Path.GetRelativePath(directory.Path, segmentPath), 0, 4, createdAt)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RegisterOutputSegmentAsync(
            new TaskOutputSegment("job-1", JobOutputKind.Stdout, "../outside.seg", 0, 0, createdAt)));

        var registered = await store.RegisterOutputSegmentAsync(
            new TaskOutputSegment("job-1", JobOutputKind.Stdout, Path.GetRelativePath(directory.Path, segmentPath), 0, 3, createdAt));
        var duplicate = await store.RegisterOutputSegmentAsync(
            new TaskOutputSegment("job-1", JobOutputKind.Stdout, Path.GetRelativePath(directory.Path, segmentPath), 0, 3, createdAt));

        Assert.Equal(registered.SegmentId, duplicate.SegmentId);
        var slashSeparated = await store.RegisterOutputSegmentAsync(
            new TaskOutputSegment("job-1", JobOutputKind.Stdout, Path.GetRelativePath(directory.Path, segmentPath).Replace('\\', '/'), 0, 3, createdAt));
        Assert.Equal(registered.SegmentId, slashSeparated.SegmentId);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.RegisterOutputSegmentAsync(
            new TaskOutputSegment("job-1", JobOutputKind.Stdout, Path.GetRelativePath(directory.Path, segmentPath), 0, 3, createdAt.AddSeconds(1))));
    }

    [Fact]
    public async Task RegisterTaskHostOutputSegmentsAsyncRejectsUnsafeJobIdsAndUnknownJobs()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(directory.Path);
        await store.InitializeAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => store.RegisterTaskHostOutputSegmentsAsync("nested/job"));

        WriteTaskHostSegment(directory.Path, "missing-job", "stdout", 0, [1]);
        await Assert.ThrowsAsync<SqliteException>(() => store.RegisterTaskHostOutputSegmentsAsync("missing-job"));
    }

    private static string WriteTaskHostSegment(
        string dataRoot,
        string jobId,
        string stream,
        long startOffset,
        byte[] content,
        string? fileName = null)
    {
        var path = Path.Combine(
            dataRoot,
            "segments",
            jobId,
            stream,
            fileName ?? $"{startOffset:D20}-{Guid.NewGuid():N}.seg");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static void AssertSegment(OutputSegmentInfo segment, JobOutputKind stream, long startOffset, long byteLength)
    {
        Assert.Equal(stream, segment.Stream);
        Assert.Equal(startOffset, segment.StartOffset);
        Assert.Equal(byteLength, segment.ByteLength);
    }
}
