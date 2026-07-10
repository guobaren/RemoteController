using System.Runtime.InteropServices;

namespace Rc.Agent.Configuration;

public sealed record AgentOptions
{
    public IReadOnlyList<string> SupportedOperatingSystems { get; init; } = ["Windows 10", "Windows 11"];

    public Architecture RequiredArchitecture { get; init; } = Architecture.X64;

    public int NormalTaskLimit { get; init; } = 8;

    public int ElevatedTaskLimit { get; init; } = 2;

    public long LogQuotaBytes { get; init; } = 200L * 1024 * 1024;

    public TimeSpan CancellationGrace { get; init; } = TimeSpan.FromSeconds(10);
}