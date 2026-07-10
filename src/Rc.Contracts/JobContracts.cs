namespace Rc.Contracts;

public sealed record JobSnapshot(
    string JobId,
    JobState State,
    int? ExitCode,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    RemoteError? Error);

public sealed record JobRequest(string JobId);

public sealed record JobResponse(JobSnapshot Job);
