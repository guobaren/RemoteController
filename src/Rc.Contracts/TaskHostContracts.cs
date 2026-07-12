using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace Rc.Contracts;

public enum ExecutionIdentity
{
    CurrentUser,
    ElevatedBroker,
}

public sealed class TaskLaunchRequest
{
    private readonly ReadOnlyDictionary<string, string>? environment;

    [JsonConstructor]
    public TaskLaunchRequest(
        string jobId,
        ExecRequest execution,
        ExecutionIdentity executionIdentity,
        string dataRoot,
        string controlPipeName,
        TimeSpan cancellationGracePeriod,
        IReadOnlyDictionary<string, string>? environment = null,
        long maximumOutputBytes = 200L * 1024 * 1024,
        string? controlClientSid = null)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            throw new ArgumentException("Job ID is required.", nameof(jobId));
        }

        ArgumentNullException.ThrowIfNull(execution);
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            throw new ArgumentException("Data root is required.", nameof(dataRoot));
        }

        if (string.IsNullOrWhiteSpace(controlPipeName))
        {
            throw new ArgumentException("Control pipe name is required.", nameof(controlPipeName));
        }

        if (cancellationGracePeriod < TimeSpan.Zero || cancellationGracePeriod > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(cancellationGracePeriod));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumOutputBytes);

        JobId = jobId;
        Execution = execution;
        ExecutionIdentity = executionIdentity;
        DataRoot = dataRoot;
        ControlPipeName = controlPipeName;
        CancellationGracePeriod = cancellationGracePeriod;
        MaximumOutputBytes = maximumOutputBytes;
        ControlClientSid = string.IsNullOrWhiteSpace(controlClientSid) ? null : controlClientSid;
        this.environment = environment is null
            ? null
            : new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(environment, StringComparer.Ordinal));
    }

    public string JobId { get; }

    public ExecRequest Execution { get; }

    public ExecutionIdentity ExecutionIdentity { get; }

    public string DataRoot { get; }

    public string ControlPipeName { get; }

    public TimeSpan CancellationGracePeriod { get; }

    public long MaximumOutputBytes { get; }

    public string? ControlClientSid { get; }

    public IReadOnlyDictionary<string, string>? Environment => environment;
}

public enum TaskControlKind
{
    StandardInput,
    CloseStandardInput,
    GetStatus,
    Cancel,
    ResizeTerminal,
}

public sealed class TaskControlMessage
{
    private readonly byte[]? data;

    [JsonConstructor]
    public TaskControlMessage(TaskControlKind kind, byte[]? data = null, int? columns = null, int? rows = null)
    {
        if (kind == TaskControlKind.StandardInput && data is null)
        {
            throw new ArgumentException("Standard input data is required.", nameof(data));
        }
        if (kind != TaskControlKind.StandardInput && data is not null)
        {
            throw new ArgumentException("Only standard input messages may contain data.", nameof(data));
        }
        if (kind == TaskControlKind.ResizeTerminal)
        {
            if (columns is not >= 1 or > 1000 || rows is not >= 1 or > 1000)
            {
                throw new ArgumentOutOfRangeException(nameof(columns), "Terminal columns and rows must be between 1 and 1000.");
            }
        }
        else if (columns is not null || rows is not null)
        {
            throw new ArgumentException("Only terminal resize messages may contain dimensions.");
        }

        Kind = kind;
        this.data = data?.ToArray();
        Columns = columns;
        Rows = rows;
    }

    public TaskControlKind Kind { get; }

    public byte[]? Data => data?.ToArray();

    public int? Columns { get; }

    public int? Rows { get; }
}

public sealed record TaskOutputSegment(
    string JobId,
    JobOutputKind Stream,
    string RelativePath,
    long StartOffset,
    long ByteLength,
    DateTimeOffset CreatedAtUtc);

public sealed record TaskRuntimeStatus(
    JobSnapshot Job,
    int? ProcessId,
    TimeSpan? ProcessorTime,
    long? WorkingSetBytes,
    long? PeakWorkingSetBytes,
    long StdoutLength,
    long StderrLength,
    DateTimeOffset? LastOutputAtUtc,
    bool OutputTruncated = false);

public sealed record TaskControlResponse(
    TaskRuntimeStatus Status,
    TaskOutputSegment? OutputSegment,
    RemoteError? Error = null);
