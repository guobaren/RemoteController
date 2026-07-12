using Rc.Agent.Persistence;
using Rc.Contracts;
using Rc.TaskHost;

namespace Rc.Agent.Jobs;

public sealed class TaskHostRegistration
{
    private readonly AgentStateStore stateStore;

    public TaskHostRegistration(AgentStateStore stateStore)
    {
        this.stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
    }

    public async Task<TaskRuntimeStatus> RefreshAsync(string controlPipeName, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        return await SendAndPersistAsync(controlPipeName, new TaskControlMessage(TaskControlKind.GetStatus), timeout, cancellationToken).ConfigureAwait(false);
    }

    public Task<TaskRuntimeStatus> WriteStandardInputAsync(string controlPipeName, byte[] data, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        return SendAndPersistAsync(controlPipeName, new TaskControlMessage(TaskControlKind.StandardInput, data), timeout, cancellationToken);
    }

    public Task<TaskRuntimeStatus> CloseStandardInputAsync(string controlPipeName, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        SendAndPersistAsync(controlPipeName, new TaskControlMessage(TaskControlKind.CloseStandardInput), timeout, cancellationToken);

    public Task<TaskRuntimeStatus> ResizeTerminalAsync(string controlPipeName, int columns, int rows, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        SendAndPersistAsync(controlPipeName, new TaskControlMessage(TaskControlKind.ResizeTerminal, columns: columns, rows: rows), timeout, cancellationToken);
    public Task<TaskRuntimeStatus> CancelAsync(string controlPipeName, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        SendAndPersistAsync(controlPipeName, new TaskControlMessage(TaskControlKind.Cancel), timeout, cancellationToken);

    private async Task<TaskRuntimeStatus> SendAndPersistAsync(string controlPipeName, TaskControlMessage message, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(controlPipeName);
        var response = await TaskHostControlClient.SendAsync(controlPipeName, message, timeout, cancellationToken).ConfigureAwait(false);
        if (response.Error is not null)
        {
            throw new InvalidOperationException(response.Error.Message);
        }

        await stateStore.SaveJobSnapshotAsync(response.Status.Job, cancellationToken).ConfigureAwait(false);
        if (response.OutputSegment is not null)
        {
            await stateStore.RegisterOutputSegmentAsync(response.OutputSegment, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await stateStore.RegisterTaskHostOutputSegmentsAsync(response.Status.Job.JobId, cancellationToken).ConfigureAwait(false);
        }

        return response.Status;
    }
}
