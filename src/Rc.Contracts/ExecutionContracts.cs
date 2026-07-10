namespace Rc.Contracts;

public sealed record ExecRequest(
    string[] Argv,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, string>? Environment);

public sealed record ExecResponse(JobSnapshot Job);
