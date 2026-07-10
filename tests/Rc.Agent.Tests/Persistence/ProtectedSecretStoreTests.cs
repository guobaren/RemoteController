using Microsoft.Data.Sqlite;
using Rc.Agent.Persistence;
using Xunit;

namespace Rc.Agent.Tests.Persistence;

public sealed class ProtectedSecretStoreTests
{
    [Fact]
    public async Task DeviceIdentityRoundTripsAndDatabaseDoesNotContainRawCertificateOrPrivateKey()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(directory.Path);
        await store.InitializeAsync();
        var certificate = new byte[] { 1, 3, 3, 7, 9, 9, 2 };
        var privateKey = new byte[] { 7, 3, 1, 4, 1, 5, 9 };
        var identity = new DeviceIdentity(
            "agent-identity",
            certificate,
            privateKey,
            new DateTimeOffset(2026, 7, 10, 5, 0, 0, TimeSpan.Zero));

        await store.SaveDeviceIdentityAsync(identity);
        var restored = await store.GetDeviceIdentityAsync();

        Assert.NotNull(restored);
        Assert.Equal(identity.DeviceId, restored.DeviceId);
        Assert.Equal(identity.Certificate, restored.Certificate);
        Assert.Equal(identity.PrivateKey, restored.PrivateKey);
        Assert.Equal(identity.CreatedAtUtc, restored.CreatedAtUtc);

        await using var connection = new SqliteConnection($"Data Source={store.DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT certificate_protected, private_key_protected FROM device_identity WHERE id = 1;";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.False(certificate.SequenceEqual(reader.GetFieldValue<byte[]>(0)));
        Assert.False(privateKey.SequenceEqual(reader.GetFieldValue<byte[]>(1)));
    }

    [Fact]
    public async Task ExecutionAccountSecretRoundTripsAndDatabaseDoesNotContainRawSecret()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(directory.Path);
        await store.InitializeAsync();
        var secret = new byte[] { 2, 7, 1, 8, 2, 8, 1, 8 };
        var executionAccountSecret = new ExecutionAccountSecret(
            "CONTOSO\\agent-runner",
            secret,
            new DateTimeOffset(2026, 7, 10, 5, 1, 0, TimeSpan.Zero));

        await store.SaveExecutionAccountSecretAsync(executionAccountSecret);
        var restored = await store.GetExecutionAccountSecretAsync();

        Assert.NotNull(restored);
        Assert.Equal(executionAccountSecret.AccountName, restored.AccountName);
        Assert.Equal(executionAccountSecret.Secret, restored.Secret);
        Assert.Equal(executionAccountSecret.UpdatedAtUtc, restored.UpdatedAtUtc);

        await using var connection = new SqliteConnection($"Data Source={store.DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT secret_protected FROM execution_account_secret WHERE id = 1;";
        var storedSecret = (byte[])(await command.ExecuteScalarAsync())!;
        Assert.False(secret.SequenceEqual(storedSecret));
    }
}
