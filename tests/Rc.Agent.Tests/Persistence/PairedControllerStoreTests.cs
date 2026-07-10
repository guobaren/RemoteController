using Microsoft.Data.Sqlite;
using Rc.Agent.Persistence;
using Xunit;

namespace Rc.Agent.Tests.Persistence;

public sealed class PairedControllerStoreTests
{
    [Fact]
    public async Task GetPairedControllerAsyncReturnsNullBeforeAnyControllerIsPaired()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(directory.Path);
        await store.InitializeAsync();

        var pairedController = await store.GetPairedControllerAsync();

        Assert.Null(pairedController);
    }

    [Fact]
    public async Task SavePairedControllerAsyncRoundTripsAndProtectsCertificateInDatabase()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(directory.Path);
        await store.InitializeAsync();
        var certificate = new byte[] { 2, 7, 1, 8, 2, 8, 1, 8 };
        var pairedAtUtc = new DateTimeOffset(2026, 7, 10, 6, 0, 0, TimeSpan.Zero);
        var pairedController = new PairedController("controller-17", certificate, pairedAtUtc);

        await store.SavePairedControllerAsync(pairedController);
        var restored = await store.GetPairedControllerAsync();

        Assert.NotNull(restored);
        Assert.Equal(pairedController.ControllerId, restored.ControllerId);
        Assert.Equal(pairedController.Certificate, restored.Certificate);
        Assert.Equal(pairedController.PairedAtUtc, restored.PairedAtUtc);

        await using var connection = new SqliteConnection($"Data Source={store.DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT certificate_protected FROM paired_controller WHERE id = 1;";
        var storedCertificate = (byte[])(await command.ExecuteScalarAsync())!;
        Assert.False(certificate.SequenceEqual(storedCertificate));
    }

    [Fact]
    public async Task SavePairedControllerAsyncReplacesTheExistingSingleController()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(directory.Path);
        await store.InitializeAsync();
        var first = new PairedController(
            "controller-first",
            new byte[] { 1, 2, 3 },
            new DateTimeOffset(2026, 7, 10, 6, 1, 0, TimeSpan.Zero));
        var replacement = new PairedController(
            "controller-second",
            new byte[] { 4, 5, 6 },
            new DateTimeOffset(2026, 7, 10, 6, 2, 0, TimeSpan.Zero));

        await store.SavePairedControllerAsync(first);
        await store.SavePairedControllerAsync(replacement);
        var restored = await store.GetPairedControllerAsync();

        Assert.NotNull(restored);
        Assert.Equal(replacement.ControllerId, restored.ControllerId);
        Assert.Equal(replacement.Certificate, restored.Certificate);
        Assert.Equal(replacement.PairedAtUtc, restored.PairedAtUtc);

        await using var connection = new SqliteConnection($"Data Source={store.DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM paired_controller;";
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task TrySavePairedControllerIfNoneAsyncKeepsTheFirstControllerPin()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(directory.Path);
        await store.InitializeAsync();
        var first = new PairedController(
            "controller-first",
            new byte[] { 1, 2, 3 },
            new DateTimeOffset(2026, 7, 10, 6, 3, 0, TimeSpan.Zero));
        var second = new PairedController(
            "controller-second",
            new byte[] { 5, 8, 13 },
            new DateTimeOffset(2026, 7, 10, 6, 4, 0, TimeSpan.Zero));

        var firstSaved = await store.TrySavePairedControllerIfNoneAsync(first);
        var secondSaved = await store.TrySavePairedControllerIfNoneAsync(second);
        var restored = await store.GetPairedControllerAsync();

        Assert.True(firstSaved);
        Assert.False(secondSaved);
        Assert.NotNull(restored);
        Assert.Equal(first.ControllerId, restored.ControllerId);
        Assert.Equal(first.Certificate, restored.Certificate);
        Assert.Equal(first.PairedAtUtc, restored.PairedAtUtc);
    }
    [Fact]
    public async Task RemovePairedControllerAsyncRemovesThePairedController()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(directory.Path);
        await store.InitializeAsync();
        await store.SavePairedControllerAsync(new PairedController(
            "controller-17",
            new byte[] { 1, 1, 2, 3, 5, 8 },
            new DateTimeOffset(2026, 7, 10, 6, 3, 0, TimeSpan.Zero)));

        await store.RemovePairedControllerAsync();
        var restored = await store.GetPairedControllerAsync();

        Assert.Null(restored);
    }

    [Fact]
    public void PairedControllerDefensivelyCopiesCertificateBytes()
    {
        var source = new byte[] { 1, 2, 3, 4 };
        var pairedController = new PairedController(
            "controller-17",
            source,
            new DateTimeOffset(2026, 7, 10, 6, 4, 0, TimeSpan.Zero));

        source[0] = 9;
        var firstRead = pairedController.Certificate;
        firstRead[1] = 9;
        var secondRead = pairedController.Certificate;

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, secondRead);
    }
}
