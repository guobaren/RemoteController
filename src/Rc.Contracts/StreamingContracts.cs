namespace Rc.Contracts;

public enum JobOutputKind
{
    Stdout,
    Stderr,
}

public sealed class ByteChunk
{
    private readonly byte[] data;

    public ByteChunk(string jobId, JobOutputKind stream, long offset, byte[] data, bool isFinal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (!Enum.IsDefined(stream))
        {
            throw new ArgumentOutOfRangeException(nameof(stream));
        }

        ArgumentNullException.ThrowIfNull(data);
        JobId = jobId;
        Stream = stream;
        Offset = offset;
        this.data = data.ToArray();
        IsFinal = isFinal;
    }

    public string JobId { get; }

    public JobOutputKind Stream { get; }

    public long Offset { get; }

    public byte[] Data => data.ToArray();

    public bool IsFinal { get; }
}

public sealed class FileChunk
{
    private readonly byte[] data;

    public FileChunk(string transferSessionId, string relativePath, long offset, byte[] data, bool isFinal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transferSessionId);
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentNullException.ThrowIfNull(data);
        TransferSessionId = transferSessionId;
        RelativePath = relativePath;
        Offset = offset;
        this.data = data.ToArray();
        IsFinal = isFinal;
    }

    public string TransferSessionId { get; }

    public string RelativePath { get; }

    public long Offset { get; }

    public byte[] Data => data.ToArray();

    public bool IsFinal { get; }
}

public sealed class TransferChunk
{
    private readonly byte[] data;

    public TransferChunk(string transferSessionId, long offset, byte[] data, bool isFinal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transferSessionId);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentNullException.ThrowIfNull(data);
        TransferSessionId = transferSessionId;
        Offset = offset;
        this.data = data.ToArray();
        IsFinal = isFinal;
    }

    public string TransferSessionId { get; }

    public long Offset { get; }

    public byte[] Data => data.ToArray();

    public bool IsFinal { get; }
}

public enum JobState
{
    Queued,
    Running,
    Exited,
    FailedToStart,
    Cancelled,
    InterruptedByReboot,
}