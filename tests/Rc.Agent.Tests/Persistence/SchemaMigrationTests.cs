using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Data.Sqlite;
using Rc.Agent.Persistence;
using Xunit;

namespace Rc.Agent.Tests.Persistence;

public sealed class SchemaMigrationTests
{
    private static readonly string[] RequiredTables =
    [
        "device_identity",
        "execution_account_secret",
        "paired_controller",
        "job_snapshots",
        "output_segments",
        "transfer_sessions",
        "audit_events",
    ];

    [Fact]
    public async Task InitializeAsyncMigratesAnEmptyDatabaseWithAllRequiredTables()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(directory.Path);

        await store.InitializeAsync();
        await store.InitializeAsync();

        await using var connection = new SqliteConnection($"Data Source={store.DatabasePath}");
        await connection.OpenAsync();
        var tables = new HashSet<string>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        Assert.True(tables.IsSupersetOf(RequiredTables));
    }

    [Fact]
    public async Task InitializeAsyncCreatesTheUniqueOutputSegmentPathIndex()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(directory.Path);
        await store.InitializeAsync();

        await using var connection = new SqliteConnection("Data Source=" + store.DatabasePath);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT [unique] FROM pragma_index_list('output_segments') WHERE name = 'ux_output_segments_relative_path';";

        var isUnique = await command.ExecuteScalarAsync();

        Assert.Equal(1L, isUnique);
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rc-agent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
        var currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current Windows user SID is unavailable.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            currentUser,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        new DirectoryInfo(Path).SetAccessControl(security);
    }

    public string Path { get; }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
