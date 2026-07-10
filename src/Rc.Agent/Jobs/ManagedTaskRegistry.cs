using System.Collections.Concurrent;
using Rc.Agent.Persistence;
using Rc.Contracts;
using Rc.TaskHost;

namespace Rc.Agent.Jobs;

/// <summary>
/// Owns TaskHost instances launched through the control endpoint. A registry entry
/// remains available while its child process runs, so callers can obtain live
/// status through TaskHost's named-pipe control channel; terminal state is always
/// written to the durable Agent store.
/// </summary>
public sealed class ManagedTaskRegistry : IAsyncDisposable
{
    private static readonly TimeSpan ControlTimeout = TimeSpan.FromSeconds(2);
    private readonly AgentStateStore stateStore;
    private readonly TaskHostRegistration registration;
    private readonly ConcurrentDictionary<string, RunningTask> running = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource shutdown = new();

    public ManagedTaskRegistry(AgentStateStore stateStore)
    {
        this.stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        registration = new TaskHostRegistration(stateStore);
    }

    public async Task<TaskRuntimeStatus> StartAsync(ExecRequest execution, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(execution);
        var jobId = "job-" + Guid.NewGuid().ToString("N");
        var pipeName = "rc-job-" + Guid.NewGuid().ToString("N");
        var launch = new TaskLaunchRequest(
            jobId,
            execution,
            ExecutionIdentity.CurrentUser,
            stateStore.DataRoot,
            pipeName,
            TimeSpan.FromSeconds(5));
        var runner = new TaskHostRunner(launch);
        var completion = runner.RunAsync(shutdown.Token);
        var item = new RunningTask(runner, pipeName, completion);
        if (!running.TryAdd(jobId, item))
        {
            await runner.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException("The generated job ID is already active.");
        }

        _ = ObserveCompletionAsync(jobId, item);
        try
        {
            await runner.Started.WaitAsync(cancellationToken).ConfigureAwait(false);
            var status = runner.GetStatus();
            await PersistAsync(status, cancellationToken).ConfigureAwait(false);
            return status;
        }
        catch
        {
            // The task remains registered and will persist its final status. A client
            // disconnect after launch must not implicitly stop remote execution.
            throw;
        }
    }

    public async Task<(TaskRuntimeStatus Status, bool IsActive)> GetStatusAsync(string jobId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        if (running.TryGetValue(jobId, out var item))
        {
            try
            {
                var status = await registration.RefreshAsync(item.ControlPipeName, ControlTimeout, cancellationToken).ConfigureAwait(false);
                return (status, !IsTerminal(status.Job.State));
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                // The process can exit while a named-pipe status request is in flight.
                // Runner state is still authoritative for that short race window.
                var status = item.Runner.GetStatus();
                await PersistAsync(status, cancellationToken).ConfigureAwait(false);
                return (status, !IsTerminal(status.Job.State));
            }
        }

        var snapshot = await stateStore.GetJobSnapshotAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            throw new KeyNotFoundException($"No job exists with ID '{jobId}'.");
        }

        return (ToRuntimeStatus(snapshot), false);
    }

    public async Task<IReadOnlyList<JobSnapshot>> ListAsync(JobState? state, CancellationToken cancellationToken = default)
    {
        foreach (var jobId in running.Keys)
        {
            try
            {
                await GetStatusAsync(jobId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // A terminal completion observer will persist the final snapshot.
            }
        }

        return await stateStore.ListJobSnapshotsAsync(state, cancellationToken).ConfigureAwait(false);
    }

    private async Task ObserveCompletionAsync(string jobId, RunningTask item)
    {
        try
        {
            var status = await item.Completion.ConfigureAwait(false);
            await PersistAsync(status, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var fallback = item.Runner.GetStatus();
            var error = fallback.Job.Error ?? new RemoteError(ErrorCode.Internal, $"Task host completion failed: {exception.Message}", false);
            var failed = fallback with { Job = fallback.Job with { State = JobState.FailedToStart, Error = error, FinishedAtUtc = DateTimeOffset.UtcNow } };
            await PersistAsync(failed, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            running.TryRemove(jobId, out _);
            await item.Runner.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task PersistAsync(TaskRuntimeStatus status, CancellationToken cancellationToken)
    {
        await stateStore.SaveJobSnapshotAsync(status.Job, cancellationToken).ConfigureAwait(false);
        await stateStore.RegisterTaskHostOutputSegmentsAsync(status.Job.JobId, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsTerminal(JobState state) => state is JobState.Exited or JobState.FailedToStart or JobState.Cancelled or JobState.InterruptedByReboot;

    private static TaskRuntimeStatus ToRuntimeStatus(JobSnapshot snapshot) =>
        new(snapshot, null, null, null, null, 0, 0, null);

    public async ValueTask DisposeAsync()
    {
        shutdown.Cancel();
        var active = running.Values.Select(item => item.Completion).ToArray();
        try
        {
            await Task.WhenAll(active).ConfigureAwait(false);
        }
        catch
        {
            // Each runner turns launch and cancellation failures into its terminal status.
        }

        shutdown.Dispose();
    }

    private sealed record RunningTask(TaskHostRunner Runner, string ControlPipeName, Task<TaskRuntimeStatus> Completion);
}