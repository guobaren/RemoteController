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

    public string FileRoot { get; init; } = Environment.GetEnvironmentVariable("RC_AGENT_FILE_ROOT")
        ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public long TransferQuotaBytes { get; init; } = ReadLong("RC_TRANSFER_QUOTA_BYTES", 200L * 1024 * 1024);

    public int MaximumTransferChunkBytes { get; init; } = checked((int)ReadLong("RC_TRANSFER_MAX_CHUNK_BYTES", 1024 * 1024));

    public int MaximumAtomicWriteBytes { get; init; } = checked((int)ReadLong("RC_FILE_MAX_WRITE_BYTES", 16L * 1024 * 1024));

    public TimeSpan TransferSessionLifetime { get; init; } = TimeSpan.FromHours(24);

    private static long ReadLong(string name, long fallback) =>
        long.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;
}