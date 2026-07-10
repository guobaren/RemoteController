using System.Text.Json.Serialization;

namespace Rc.Contracts;

public enum ShellKind
{
    PowerShell,
    Cmd,
}

public sealed record ShellLaunch(ShellKind Kind, string Command);

public sealed class ExecRequest
{
    private readonly IReadOnlyList<string>? directArgv;
    private readonly IReadOnlyDictionary<string, string>? environment;

    [JsonConstructor]
    public ExecRequest(
        IReadOnlyList<string>? directArgv,
        ShellLaunch? shell,
        string? workingDirectory,
        IReadOnlyDictionary<string, string>? environment)
    {
        if ((directArgv is null) == (shell is null))
        {
            throw new ArgumentException("Exactly one launch mode must be specified.");
        }

        if (directArgv is { Count: 0 })
        {
            throw new ArgumentException("Direct argv must contain an executable.", nameof(directArgv));
        }

        this.directArgv = directArgv is null ? null : Array.AsReadOnly(directArgv.ToArray());
        Shell = shell;
        WorkingDirectory = workingDirectory;
        this.environment = environment is null
            ? null
            : new Dictionary<string, string>(environment, StringComparer.Ordinal);
    }

    [JsonPropertyName("directArgv")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? DirectArgv => directArgv;

    [JsonPropertyName("shell")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ShellLaunch? Shell { get; }

    public string? WorkingDirectory { get; }

    public IReadOnlyDictionary<string, string>? Environment => environment;

    public static ExecRequest ForDirectArgv(
        IEnumerable<string> directArgv,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null) =>
        new(directArgv.ToArray(), null, workingDirectory, environment);

    public static ExecRequest ForShell(
        ShellKind kind,
        string command,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null) =>
        new(null, new ShellLaunch(kind, command), workingDirectory, environment);
}

public sealed record ExecResponse(JobSnapshot Job);
