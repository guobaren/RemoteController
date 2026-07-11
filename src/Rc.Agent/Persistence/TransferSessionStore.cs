using System.Text.Json;
using Rc.Contracts;

namespace Rc.Agent.Persistence;

public sealed partial class AgentStateStore
{
    public async Task SaveTransferSessionAsync(TransferSessionSnapshot session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var now = DateTimeOffset.UtcNow;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO transfer_sessions (transfer_session_id, direction, state, manifest_json, root_path, created_at_utc, updated_at_utc)
            VALUES ($id, $direction, $state, $json, $root, $created, $updated)
            ON CONFLICT(transfer_session_id) DO UPDATE SET
                direction = excluded.direction,
                state = excluded.state,
                manifest_json = excluded.manifest_json,
                root_path = excluded.root_path,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$id", session.SessionId);
        command.Parameters.AddWithValue("$direction", session.Direction.ToString());
        command.Parameters.AddWithValue("$state", session.State.ToString());
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(session, ContractJson.Options));
        command.Parameters.AddWithValue("$root", session.Direction == TransferDirection.Upload ? session.DestinationPath : session.SourcePath);
        command.Parameters.AddWithValue("$created", session.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updated", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<TransferSessionSnapshot?> GetTransferSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT manifest_json FROM transfer_sessions WHERE transfer_session_id = $id;";
        command.Parameters.AddWithValue("$id", sessionId);
        var json = await command.ExecuteScalarAsync(cancellationToken) as string;
        return json is null ? null : JsonSerializer.Deserialize<TransferSessionSnapshot>(json, ContractJson.Options);
    }

    public async Task DeleteTransferSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM transfer_sessions WHERE transfer_session_id = $id;";
        command.Parameters.AddWithValue("$id", sessionId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}