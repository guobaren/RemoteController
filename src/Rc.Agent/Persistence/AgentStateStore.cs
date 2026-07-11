using Microsoft.Data.Sqlite;
using Rc.Contracts;
using Rc.Agent.Security;

namespace Rc.Agent.Persistence;

public sealed partial class AgentStateStore : IAsyncDisposable
{
    private readonly string connectionString;
    private readonly SemaphoreSlim outputMutationGate = new(1, 1);

    public AgentStateStore(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        DataRoot = Path.GetFullPath(dataRoot);
        DatabasePath = Path.Combine(DataRoot, "agent-state.db");
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
    }

    public string DataRoot { get; }

    public string DatabasePath { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        AgentDataDirectoryAclValidator.EnsureSafe(DataRoot);

        await using var connection = await OpenConnectionAsync(cancellationToken);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    version INTEGER NOT NULL PRIMARY KEY,
                    applied_at_utc TEXT NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        using var transaction = connection.BeginTransaction();
        var migration = connection.CreateCommand();
        migration.Transaction = transaction;
        migration.CommandText = """
            CREATE TABLE IF NOT EXISTS device_identity (
                id INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
                device_id TEXT NOT NULL,
                certificate_protected BLOB NOT NULL,
                private_key_protected BLOB NOT NULL,
                created_at_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS paired_controller (
                id INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
                controller_id TEXT NOT NULL,
                certificate_protected BLOB NOT NULL,
                paired_at_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS job_snapshots (
                job_id TEXT NOT NULL PRIMARY KEY,
                state TEXT NOT NULL,
                exit_code INTEGER NULL,
                created_at_utc TEXT NOT NULL,
                started_at_utc TEXT NULL,
                finished_at_utc TEXT NULL,
                error_code TEXT NULL,
                error_message TEXT NULL,
                error_retryable INTEGER NULL
            );
            CREATE TABLE IF NOT EXISTS output_segments (
                segment_id TEXT NOT NULL PRIMARY KEY,
                job_id TEXT NOT NULL,
                stream TEXT NOT NULL,
                relative_path TEXT NOT NULL,
                start_offset INTEGER NOT NULL,
                byte_length INTEGER NOT NULL,
                created_at_utc TEXT NOT NULL,
                FOREIGN KEY (job_id) REFERENCES job_snapshots(job_id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS transfer_sessions (
                transfer_session_id TEXT NOT NULL PRIMARY KEY,
                direction TEXT NOT NULL,
                state TEXT NOT NULL,
                manifest_json TEXT NOT NULL,
                root_path TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS audit_events (
                event_id TEXT NOT NULL PRIMARY KEY,
                occurred_at_utc TEXT NOT NULL,
                event_type TEXT NOT NULL,
                detail_json TEXT NOT NULL
            );
            """;
        await migration.ExecuteNonQueryAsync(cancellationToken);

        var recordVersion = connection.CreateCommand();
        recordVersion.Transaction = transaction;
        recordVersion.CommandText = "INSERT OR IGNORE INTO schema_migrations (version, applied_at_utc) VALUES (1, $appliedAtUtc);";
        recordVersion.Parameters.AddWithValue("$appliedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
        await recordVersion.ExecuteNonQueryAsync(cancellationToken);
        transaction.Commit();

        using var secondTransaction = connection.BeginTransaction();
        var executionAccountMigration = connection.CreateCommand();
        executionAccountMigration.Transaction = secondTransaction;
        executionAccountMigration.CommandText = """
            CREATE TABLE IF NOT EXISTS execution_account_secret (
                id INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
                account_name TEXT NOT NULL,
                secret_protected BLOB NOT NULL,
                updated_at_utc TEXT NOT NULL
            );
            """;
        await executionAccountMigration.ExecuteNonQueryAsync(cancellationToken);

        var recordSecondVersion = connection.CreateCommand();
        recordSecondVersion.Transaction = secondTransaction;
        recordSecondVersion.CommandText = "INSERT OR IGNORE INTO schema_migrations (version, applied_at_utc) VALUES (2, $appliedAtUtc);";
        recordSecondVersion.Parameters.AddWithValue("$appliedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
        await recordSecondVersion.ExecuteNonQueryAsync(cancellationToken);
        secondTransaction.Commit();

        using var thirdTransaction = connection.BeginTransaction();
        var outputSegmentPathMigration = connection.CreateCommand();
        outputSegmentPathMigration.Transaction = thirdTransaction;
        outputSegmentPathMigration.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS ux_output_segments_relative_path ON output_segments(relative_path);";
        await outputSegmentPathMigration.ExecuteNonQueryAsync(cancellationToken);

        var recordThirdVersion = connection.CreateCommand();
        recordThirdVersion.Transaction = thirdTransaction;
        recordThirdVersion.CommandText = "INSERT OR IGNORE INTO schema_migrations (version, applied_at_utc) VALUES (3, $appliedAtUtc);";
        recordThirdVersion.Parameters.AddWithValue("$appliedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
        await recordThirdVersion.ExecuteNonQueryAsync(cancellationToken);
        thirdTransaction.Commit();
        using var fourthTransaction = connection.BeginTransaction();
        var taskHostRegistrationMigration = connection.CreateCommand();
        taskHostRegistrationMigration.Transaction = fourthTransaction;
        taskHostRegistrationMigration.CommandText = """
            CREATE TABLE IF NOT EXISTS task_host_registrations (
                job_id TEXT NOT NULL PRIMARY KEY,
                control_pipe_name TEXT NOT NULL UNIQUE,
                process_id INTEGER NULL,
                created_at_utc TEXT NOT NULL,
                FOREIGN KEY (job_id) REFERENCES job_snapshots(job_id) ON DELETE CASCADE
            );
            """;
        await taskHostRegistrationMigration.ExecuteNonQueryAsync(cancellationToken);

        var recordFourthVersion = connection.CreateCommand();
        recordFourthVersion.Transaction = fourthTransaction;
        recordFourthVersion.CommandText = "INSERT OR IGNORE INTO schema_migrations (version, applied_at_utc) VALUES (4, $appliedAtUtc);";
        recordFourthVersion.Parameters.AddWithValue("$appliedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
        await recordFourthVersion.ExecuteNonQueryAsync(cancellationToken);
        fourthTransaction.Commit();
    }

    public async Task SaveJobSnapshotAsync(JobSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO job_snapshots (
                job_id, state, exit_code, created_at_utc, started_at_utc, finished_at_utc,
                error_code, error_message, error_retryable)
            VALUES (
                $jobId, $state, $exitCode, $createdAtUtc, $startedAtUtc, $finishedAtUtc,
                $errorCode, $errorMessage, $errorRetryable)
            ON CONFLICT(job_id) DO UPDATE SET
                state = excluded.state,
                exit_code = excluded.exit_code,
                created_at_utc = excluded.created_at_utc,
                started_at_utc = excluded.started_at_utc,
                finished_at_utc = excluded.finished_at_utc,
                error_code = excluded.error_code,
                error_message = excluded.error_message,
                error_retryable = excluded.error_retryable;
            """;
        command.Parameters.AddWithValue("$jobId", snapshot.JobId);
        command.Parameters.AddWithValue("$state", snapshot.State.ToString());
        command.Parameters.AddWithValue("$exitCode", (object?)snapshot.ExitCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAtUtc", snapshot.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$startedAtUtc", snapshot.StartedAtUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$finishedAtUtc", snapshot.FinishedAtUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$errorCode", snapshot.Error?.Code.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$errorMessage", snapshot.Error?.Message ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$errorRetryable", snapshot.Error is null ? DBNull.Value : snapshot.Error.Retryable ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<JobSnapshot?> GetJobSnapshotAsync(string jobId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT state, exit_code, created_at_utc, started_at_utc, finished_at_utc,
                   error_code, error_message, error_retryable
            FROM job_snapshots
            WHERE job_id = $jobId;
            """;
        command.Parameters.AddWithValue("$jobId", jobId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var error = reader.IsDBNull(5)
            ? null
            : new RemoteError(
                Enum.Parse<ErrorCode>(reader.GetString(5), ignoreCase: false),
                reader.GetString(6),
                reader.GetInt64(7) != 0);
        return new JobSnapshot(
            jobId,
            Enum.Parse<JobState>(reader.GetString(0), ignoreCase: false),
            reader.IsDBNull(1) ? null : reader.GetInt32(1),
            DateTimeOffset.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture),
            reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3), System.Globalization.CultureInfo.InvariantCulture),
            reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4), System.Globalization.CultureInfo.InvariantCulture),
            error);
    }

    public async Task<IReadOnlyList<JobSnapshot>> ListJobSnapshotsAsync(JobState? state = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT job_id, state, exit_code, created_at_utc, started_at_utc, finished_at_utc, error_code, error_message, error_retryable FROM job_snapshots WHERE ($state IS NULL OR state = $state) ORDER BY created_at_utc, job_id;";
        command.Parameters.AddWithValue("$state", state?.ToString() ?? (object)DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var snapshots = new List<JobSnapshot>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var error = reader.IsDBNull(6) ? null : new RemoteError(Enum.Parse<ErrorCode>(reader.GetString(6), false), reader.GetString(7), reader.GetInt64(8) != 0);
            snapshots.Add(new JobSnapshot(reader.GetString(0), Enum.Parse<JobState>(reader.GetString(1), false), reader.IsDBNull(2) ? null : reader.GetInt32(2), DateTimeOffset.Parse(reader.GetString(3), System.Globalization.CultureInfo.InvariantCulture), reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4), System.Globalization.CultureInfo.InvariantCulture), reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture), error));
        }

        return snapshots;
    }
    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys = ON;";
            await command.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        outputMutationGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
