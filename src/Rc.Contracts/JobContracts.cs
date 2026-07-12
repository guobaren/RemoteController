namespace Rc.Contracts;

public sealed record JobSnapshot(
    string JobId,
    JobState State,
    int? ExitCode,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    RemoteError? Error,
    ExecutionIdentity ExecutionIdentity = ExecutionIdentity.CurrentUser,
    bool OutputTruncated = false);

public sealed record JobRequest(string JobId);

public sealed record JobResponse(JobSnapshot Job);

public sealed record JobListRequest(JobState? State = null);

public sealed class JobListResponse
{
    public JobListResponse(IReadOnlyList<JobSnapshot> jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        Jobs = Array.AsReadOnly(jobs.ToArray());
    }

    public IReadOnlyList<JobSnapshot> Jobs { get; }
}

public sealed class JobLogReadRequest
{
    public JobLogReadRequest(string jobId, JobOutputKind stream, long afterOffset, int maximumBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentOutOfRangeException.ThrowIfNegative(afterOffset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        if (!Enum.IsDefined(stream))
        {
            throw new ArgumentOutOfRangeException(nameof(stream));
        }

        JobId = jobId;
        Stream = stream;
        AfterOffset = afterOffset;
        MaximumBytes = maximumBytes;
    }

    public string JobId { get; }

    public JobOutputKind Stream { get; }

    public long AfterOffset { get; }

    public int MaximumBytes { get; }
}

public sealed record JobLogReadResponse(ByteChunk Chunk, long NextOffset);

public sealed class JobLogFollowRequest
{
    public JobLogFollowRequest(string jobId, JobOutputKind stream, long afterOffset, int maximumBytes, TimeSpan waitTimeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentOutOfRangeException.ThrowIfNegative(afterOffset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        if (!Enum.IsDefined(stream))
        {
            throw new ArgumentOutOfRangeException(nameof(stream));
        }

        if (waitTimeout < TimeSpan.Zero || waitTimeout > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(waitTimeout));
        }

        JobId = jobId;
        Stream = stream;
        AfterOffset = afterOffset;
        MaximumBytes = maximumBytes;
        WaitTimeout = waitTimeout;
    }

    public string JobId { get; }

    public JobOutputKind Stream { get; }

    public long AfterOffset { get; }

    public int MaximumBytes { get; }

    public TimeSpan WaitTimeout { get; }
}

public sealed class JobInputRequest
{
    private readonly byte[] data;

    public JobInputRequest(string jobId, byte[] data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentNullException.ThrowIfNull(data);
        JobId = jobId;
        this.data = data.ToArray();
    }

    public string JobId { get; }

    public byte[] Data => data.ToArray();
}

public sealed record JobCloseInputRequest(string JobId);

public sealed class JobWaitRequest
{
    public JobWaitRequest(string jobId, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        if (timeout is { } value && (value < TimeSpan.Zero || value > TimeSpan.FromDays(1)))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        JobId = jobId;
        Timeout = timeout;
    }

    public string JobId { get; }

    public TimeSpan? Timeout { get; }
}

public sealed record JobCancelRequest(string JobId);