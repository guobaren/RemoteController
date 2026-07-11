using Rc.Contracts;

namespace Rc.Agent.Persistence;

public sealed record TaskHostRegistrationInfo(string JobId, string ControlPipeName, int? ProcessId, DateTimeOffset CreatedAtUtc);

public sealed partial class AgentStateStore
{
    public async Task SaveTaskHostRegistrationAsync(TaskHostRegistrationInfo registration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentException.ThrowIfNullOrWhiteSpace(registration.JobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(registration.ControlPipeName);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO task_host_registrations (job_id, control_pipe_name, process_id, created_at_utc)
            VALUES ($jobId, $controlPipeName, $processId, $createdAtUtc)
            ON CONFLICT(job_id) DO UPDATE SET
                control_pipe_name = excluded.control_pipe_name,
                process_id = excluded.process_id,
                created_at_utc = excluded.created_at_utc;
            """;
        command.Parameters.AddWithValue("$jobId", registration.JobId);
        command.Parameters.AddWithValue("$controlPipeName", registration.ControlPipeName);
        command.Parameters.AddWithValue("$processId", (object?)registration.ProcessId ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAtUtc", registration.CreatedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TaskHostRegistrationInfo>> ListTaskHostRegistrationsAsync(CancellationToken cancellationToken = default)
    {
        var registrations = new List<TaskHostRegistrationInfo>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT job_id, control_pipe_name, process_id, created_at_utc FROM task_host_registrations ORDER BY created_at_utc, job_id;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            registrations.Add(new TaskHostRegistrationInfo(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetInt32(2),
                DateTimeOffset.Parse(reader.GetString(3), System.Globalization.CultureInfo.InvariantCulture)));
        }
        return registrations;
    }

    public async Task DeleteTaskHostRegistrationAsync(string jobId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM task_host_registrations WHERE job_id = $jobId;";
        command.Parameters.AddWithValue("$jobId", jobId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkJobInterruptedByRebootAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var snapshot = await GetJobSnapshotAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null || snapshot.State is JobState.Exited or JobState.FailedToStart or JobState.Cancelled or JobState.InterruptedByReboot)
        {
            return;
        }

        var interrupted = snapshot with
        {
            State = JobState.InterruptedByReboot,
            FinishedAtUtc = DateTimeOffset.UtcNow,
            Error = new RemoteError(ErrorCode.Unavailable, "The task host was not available after the agent restarted; the command was not replayed.", true),
        };
        await SaveJobSnapshotAsync(interrupted, cancellationToken).ConfigureAwait(false);
        await DeleteTaskHostRegistrationAsync(jobId, cancellationToken).ConfigureAwait(false);
    }
}