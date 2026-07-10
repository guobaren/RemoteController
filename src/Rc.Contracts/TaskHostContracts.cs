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
        IReadOnlyDictionary<string, string>? environment = null)
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

        JobId = jobId;
        Execution = execution;
        ExecutionIdentity = executionIdentity;
        DataRoot = dataRoot;
        ControlPipeName = controlPipeName;
        CancellationGracePeriod = cancellationGracePeriod;
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

    public IReadOnlyDictionary<string, string>? Environment => environment;
}

public enum TaskControlKind
{
    StandardInput,
    CloseStandardInput,
    GetStatus,
    Cancel,
}

public sealed class TaskControlMessage
{
    private readonly byte[]? data;

    [JsonConstructor]
    public TaskControlMessage(TaskControlKind kind, byte[]? data = null)
    {
        if (kind == TaskControlKind.StandardInput && data is null)
        {
            throw new ArgumentException("Standard input data is required.", nameof(data));
        }

        if (kind != TaskControlKind.StandardInput && data is not null)
        {
            throw new ArgumentException("Only standard input messages may contain data.", nameof(data));
        }

        Kind = kind;
        this.data = data?.ToArray();
    }

    public TaskControlKind Kind { get; }

    public byte[]? Data => data?.ToArray();
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
    DateTimeOffset? LastOutputAtUtc);

public sealed record TaskControlResponse(
    TaskRuntimeStatus Status,
    TaskOutputSegment? OutputSegment,
    RemoteError? Error = null);
