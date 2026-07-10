using Microsoft.Data.Sqlite;
using Rc.Agent.Persistence;
using Rc.Contracts;
using Xunit;

namespace Rc.Agent.Tests.Persistence;

public sealed class OutputSegmentForeignKeyTests
{
    [Fact]
    public async Task AppendOutputSegmentAsyncRejectsAnUnknownJobAfterConnectionPoolsAreCleared()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(directory.Path);
        await store.InitializeAsync();
        SqliteConnection.ClearAllPools();

        await Assert.ThrowsAsync<SqliteException>(() =>
            store.AppendOutputSegmentAsync("missing-job", JobOutputKind.Stdout, 0, new byte[] { 1, 2, 3 }));
    }
}
