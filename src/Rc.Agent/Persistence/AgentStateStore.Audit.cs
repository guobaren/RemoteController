using System.Text.Json;
using Rc.Contracts;

namespace Rc.Agent.Persistence;

public sealed record AgentAuditEvent(
    string EventId,
    DateTimeOffset OccurredAtUtc,
    string EventType,
    string? ControllerId,
    string? TargetId,
    bool Succeeded,
    ErrorCode? ErrorCode,
    IReadOnlyDictionary<string, string>? Details = null);

public sealed record PairingSecurityState(
    int FailureCount,
    DateTimeOffset? WindowStartedAtUtc,
    DateTimeOffset? BlockedUntilUtc,
    long Generation);

public sealed partial class AgentStateStore
{
    public async Task AppendAuditEventAsync(AgentAuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        ArgumentException.ThrowIfNullOrWhiteSpace(auditEvent.EventId);
        ArgumentException.ThrowIfNullOrWhiteSpace(auditEvent.EventType);

        var detailJson = JsonSerializer.Serialize(new
        {
            auditEvent.ControllerId,
            auditEvent.TargetId,
            auditEvent.Succeeded,
            auditEvent.ErrorCode,
            auditEvent.Details,
        }, ContractJson.Options);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO audit_events (event_id, occurred_at_utc, event_type, detail_json)
            VALUES ($eventId, $occurredAtUtc, $eventType, $detailJson);
            """;
        command.Parameters.AddWithValue("$eventId", auditEvent.EventId);
        command.Parameters.AddWithValue("$occurredAtUtc", auditEvent.OccurredAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$eventType", auditEvent.EventType);
        command.Parameters.AddWithValue("$detailJson", detailJson);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AgentAuditEvent>> ListAuditEventsAsync(
        int maximumCount = 1000,
        CancellationToken cancellationToken = default)
    {
        if (maximumCount is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_id, occurred_at_utc, event_type, detail_json
            FROM audit_events
            ORDER BY occurred_at_utc DESC, event_id DESC
            LIMIT $maximumCount;
            """;
        command.Parameters.AddWithValue("$maximumCount", maximumCount);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var events = new List<AgentAuditEvent>();
        while (await reader.ReadAsync(cancellationToken))
        {
            using var detail = JsonDocument.Parse(reader.GetString(3));
            var root = detail.RootElement;
            var details = root.TryGetProperty("details", out var detailsElement) && detailsElement.ValueKind == JsonValueKind.Object
                ? detailsElement.EnumerateObject().ToDictionary(item => item.Name, item => item.Value.GetString() ?? string.Empty, StringComparer.Ordinal)
                : null;
            events.Add(new AgentAuditEvent(
                reader.GetString(0),
                DateTimeOffset.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture),
                reader.GetString(2),
                GetOptionalString(root, "controllerId"),
                GetOptionalString(root, "targetId"),
                root.GetProperty("succeeded").GetBoolean(),
                root.TryGetProperty("errorCode", out var errorElement) && errorElement.ValueKind == JsonValueKind.String
                    ? errorElement.Deserialize<ErrorCode>(ContractJson.Options)
                    : null,
                details));
        }

        return events;
    }

    public async Task<long> EnforceAuditQuotaAsync(long quotaBytes, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quotaBytes);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        long totalBytes;
        await using (var size = connection.CreateCommand())
        {
            size.CommandText = "SELECT COALESCE(SUM(length(event_id) + length(occurred_at_utc) + length(event_type) + length(detail_json)), 0) FROM audit_events;";
            totalBytes = Convert.ToInt64(await size.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        }

        while (totalBytes > quotaBytes)
        {
            await using var remove = connection.CreateCommand();
            remove.CommandText = """
                DELETE FROM audit_events
                WHERE event_id = (
                    SELECT event_id FROM audit_events
                    ORDER BY occurred_at_utc, event_id
                    LIMIT 1
                )
                RETURNING length(event_id) + length(occurred_at_utc) + length(event_type) + length(detail_json);
                """;
            var removed = await remove.ExecuteScalarAsync(cancellationToken);
            if (removed is null || removed is DBNull)
            {
                break;
            }
            totalBytes -= Convert.ToInt64(removed, System.Globalization.CultureInfo.InvariantCulture);
        }

        return Math.Max(0, totalBytes);
    }

    public async Task<PairingSecurityState> GetPairingSecurityStateAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT failure_count, window_started_at_utc, blocked_until_utc, generation FROM pairing_security_state WHERE id = 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new PairingSecurityState(0, null, null, 0);
        }
        return new PairingSecurityState(
            reader.GetInt32(0),
            reader.IsDBNull(1) ? null : DateTimeOffset.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture),
            reader.IsDBNull(2) ? null : DateTimeOffset.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture),
            reader.GetInt64(3));
    }

    public async Task<PairingSecurityState> RecordPairingFailureAsync(
        DateTimeOffset now,
        TimeSpan failureWindow,
        int maximumFailures,
        TimeSpan cooldown,
        CancellationToken cancellationToken = default)
    {
        if (failureWindow <= TimeSpan.Zero || maximumFailures < 1 || cooldown <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumFailures));
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        PairingSecurityState current;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT failure_count, window_started_at_utc, blocked_until_utc, generation FROM pairing_security_state WHERE id = 1;";
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            current = await reader.ReadAsync(cancellationToken)
                ? new PairingSecurityState(
                    reader.GetInt32(0),
                    reader.IsDBNull(1) ? null : DateTimeOffset.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture),
                    reader.IsDBNull(2) ? null : DateTimeOffset.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture),
                    reader.GetInt64(3))
                : new PairingSecurityState(0, null, null, 0);
        }

        var windowStart = current.WindowStartedAtUtc;
        var failures = current.FailureCount;
        if (windowStart is null || now - windowStart.Value >= failureWindow)
        {
            windowStart = now;
            failures = 0;
        }
        failures++;
        var blockedUntil = failures >= maximumFailures ? now.Add(cooldown) : current.BlockedUntilUtc;
        var updated = new PairingSecurityState(failures, windowStart, blockedUntil, current.Generation);

        await using var write = connection.CreateCommand();
        write.Transaction = transaction;
        write.CommandText = """
            INSERT INTO pairing_security_state (id, failure_count, window_started_at_utc, blocked_until_utc, generation)
            VALUES (1, $failureCount, $windowStartedAtUtc, $blockedUntilUtc, $generation)
            ON CONFLICT(id) DO UPDATE SET
                failure_count = excluded.failure_count,
                window_started_at_utc = excluded.window_started_at_utc,
                blocked_until_utc = excluded.blocked_until_utc,
                generation = excluded.generation;
            """;
        write.Parameters.AddWithValue("$failureCount", updated.FailureCount);
        write.Parameters.AddWithValue("$windowStartedAtUtc", updated.WindowStartedAtUtc?.ToString("O") ?? (object)DBNull.Value);
        write.Parameters.AddWithValue("$blockedUntilUtc", updated.BlockedUntilUtc?.ToString("O") ?? (object)DBNull.Value);
        write.Parameters.AddWithValue("$generation", updated.Generation);
        await write.ExecuteNonQueryAsync(cancellationToken);
        transaction.Commit();
        return updated;
    }

    public async Task ResetPairingFailuresAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE pairing_security_state SET failure_count = 0, window_started_at_utc = NULL, blocked_until_utc = NULL WHERE id = 1;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string? GetOptionalString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
