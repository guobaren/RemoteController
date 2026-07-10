using Rc.Agent.Security;

namespace Rc.Agent.Persistence;

public sealed class DeviceIdentity
{
    private readonly byte[] certificate;
    private readonly byte[] privateKey;

    public DeviceIdentity(string deviceId, byte[] certificate, byte[] privateKey, DateTimeOffset createdAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(privateKey);

        DeviceId = deviceId;
        this.certificate = certificate.ToArray();
        this.privateKey = privateKey.ToArray();
        CreatedAtUtc = createdAtUtc;
    }

    public string DeviceId { get; }

    public byte[] Certificate => certificate.ToArray();

    public byte[] PrivateKey => privateKey.ToArray();

    public DateTimeOffset CreatedAtUtc { get; }
}

public sealed class ExecutionAccountSecret
{
    private readonly byte[] secret;

    public ExecutionAccountSecret(string accountName, byte[] secret, DateTimeOffset updatedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        ArgumentNullException.ThrowIfNull(secret);

        AccountName = accountName;
        this.secret = secret.ToArray();
        UpdatedAtUtc = updatedAtUtc;
    }

    public string AccountName { get; }

    public byte[] Secret => secret.ToArray();

    public DateTimeOffset UpdatedAtUtc { get; }
}

public sealed partial class AgentStateStore
{
    public async Task SaveDeviceIdentityAsync(DeviceIdentity identity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var protector = new DpapiSecretProtector();
        var protectedCertificate = protector.Protect(identity.Certificate);
        var protectedPrivateKey = protector.Protect(identity.PrivateKey);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO device_identity (
                id, device_id, certificate_protected, private_key_protected, created_at_utc)
            VALUES (1, $deviceId, $certificateProtected, $privateKeyProtected, $createdAtUtc)
            ON CONFLICT(id) DO UPDATE SET
                device_id = excluded.device_id,
                certificate_protected = excluded.certificate_protected,
                private_key_protected = excluded.private_key_protected,
                created_at_utc = excluded.created_at_utc;
            """;
        command.Parameters.AddWithValue("$deviceId", identity.DeviceId);
        command.Parameters.AddWithValue("$certificateProtected", protectedCertificate);
        command.Parameters.AddWithValue("$privateKeyProtected", protectedPrivateKey);
        command.Parameters.AddWithValue("$createdAtUtc", identity.CreatedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<DeviceIdentity?> GetDeviceIdentityAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT device_id, certificate_protected, private_key_protected, created_at_utc
            FROM device_identity
            WHERE id = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var protector = new DpapiSecretProtector();
        return new DeviceIdentity(
            reader.GetString(0),
            protector.Unprotect(reader.GetFieldValue<byte[]>(1)),
            protector.Unprotect(reader.GetFieldValue<byte[]>(2)),
            DateTimeOffset.Parse(reader.GetString(3), System.Globalization.CultureInfo.InvariantCulture));
    }

    public async Task SaveExecutionAccountSecretAsync(
        ExecutionAccountSecret executionAccountSecret,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executionAccountSecret);
        var protector = new DpapiSecretProtector();
        var protectedSecret = protector.Protect(executionAccountSecret.Secret);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO execution_account_secret (id, account_name, secret_protected, updated_at_utc)
            VALUES (1, $accountName, $secretProtected, $updatedAtUtc)
            ON CONFLICT(id) DO UPDATE SET
                account_name = excluded.account_name,
                secret_protected = excluded.secret_protected,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$accountName", executionAccountSecret.AccountName);
        command.Parameters.AddWithValue("$secretProtected", protectedSecret);
        command.Parameters.AddWithValue("$updatedAtUtc", executionAccountSecret.UpdatedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ExecutionAccountSecret?> GetExecutionAccountSecretAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT account_name, secret_protected, updated_at_utc
            FROM execution_account_secret
            WHERE id = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var protector = new DpapiSecretProtector();
        return new ExecutionAccountSecret(
            reader.GetString(0),
            protector.Unprotect(reader.GetFieldValue<byte[]>(1)),
            DateTimeOffset.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture));
    }
}
