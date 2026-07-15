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

public sealed class PairedController
{
    private readonly byte[] certificate;

    public PairedController(string controllerId, byte[] certificate, DateTimeOffset pairedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(controllerId);
        ArgumentNullException.ThrowIfNull(certificate);

        ControllerId = controllerId;
        this.certificate = certificate.ToArray();
        PairedAtUtc = pairedAtUtc;
    }

    public string ControllerId { get; }

    public byte[] Certificate => certificate.ToArray();

    public DateTimeOffset PairedAtUtc { get; }
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

    public async Task DeleteDeviceIdentityAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM device_identity WHERE id = 1;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> HasPairedControllerAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM paired_controller WHERE id = 1);";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) != 0;
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

    public async Task SavePairedControllerAsync(PairedController pairedController, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pairedController);
        var protector = new DpapiSecretProtector();
        var protectedCertificate = protector.Protect(pairedController.Certificate);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO paired_controller (id, controller_id, certificate_protected, paired_at_utc)
            VALUES (1, $controllerId, $certificateProtected, $pairedAtUtc)
            ON CONFLICT(id) DO UPDATE SET
                controller_id = excluded.controller_id,
                certificate_protected = excluded.certificate_protected,
                paired_at_utc = excluded.paired_at_utc;
            """;
        command.Parameters.AddWithValue("$controllerId", pairedController.ControllerId);
        command.Parameters.AddWithValue("$certificateProtected", protectedCertificate);
        command.Parameters.AddWithValue("$pairedAtUtc", pairedController.PairedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Stores the first paired controller without ever replacing an existing pin.
    /// This is the persistence boundary enforcing the single-controller policy.
    /// </summary>
    public async Task<bool> TrySavePairedControllerIfNoneAsync(
        PairedController pairedController,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pairedController);
        var protector = new DpapiSecretProtector();
        var protectedCertificate = protector.Protect(pairedController.Certificate);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO paired_controller (id, controller_id, certificate_protected, paired_at_utc)
            VALUES (1, $controllerId, $certificateProtected, $pairedAtUtc)
            ON CONFLICT(id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$controllerId", pairedController.ControllerId);
        command.Parameters.AddWithValue("$certificateProtected", protectedCertificate);
        command.Parameters.AddWithValue("$pairedAtUtc", pairedController.PairedAtUtc.ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<PairedController?> GetPairedControllerAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT controller_id, certificate_protected, paired_at_utc
            FROM paired_controller
            WHERE id = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var protector = new DpapiSecretProtector();
        return new PairedController(
            reader.GetString(0),
            protector.Unprotect(reader.GetFieldValue<byte[]>(1)),
            DateTimeOffset.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Reads only the paired controller identifier. This is safe for local
    /// administrative commands running under a different Windows account from
    /// the Agent service, because it never attempts to DPAPI-unprotect the
    /// controller certificate.
    /// </summary>
    public async Task<string?> GetPairedControllerIdAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT controller_id FROM paired_controller WHERE id = 1;";
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    public async Task RemovePairedControllerAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        string? controllerId;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT controller_id FROM paired_controller WHERE id = 1;";
            controllerId = (string?)await read.ExecuteScalarAsync(cancellationToken);
        }
        await using (var remove = connection.CreateCommand())
        {
            remove.Transaction = transaction;
            remove.CommandText = "DELETE FROM paired_controller WHERE id = 1;";
            await remove.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var revoke = connection.CreateCommand())
        {
            revoke.Transaction = transaction;
            revoke.CommandText = "UPDATE pairing_security_state SET generation = generation + 1 WHERE id = 1;";
            await revoke.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var audit = connection.CreateCommand())
        {
            audit.Transaction = transaction;
            audit.CommandText = "INSERT INTO audit_events (event_id, occurred_at_utc, event_type, detail_json) VALUES ($eventId, $occurredAtUtc, 'pairing.unpaired', $detailJson);";
            audit.Parameters.AddWithValue("$eventId", Guid.NewGuid().ToString("N"));
            audit.Parameters.AddWithValue("$occurredAtUtc", DateTimeOffset.UtcNow.ToString("O"));
            audit.Parameters.AddWithValue("$detailJson", System.Text.Json.JsonSerializer.Serialize(new
            {
                ControllerId = controllerId,
                TargetId = controllerId,
                Succeeded = true,
                ErrorCode = (string?)null,
                Details = new Dictionary<string, string> { ["source"] = "local" },
            }, Rc.Contracts.ContractJson.Options));
            await audit.ExecuteNonQueryAsync(cancellationToken);
        }
        transaction.Commit();
    }
}
