using Rc.Agent.Jobs;
using Rc.Agent.Persistence;
using Rc.Agent.Tests.Persistence;
using Rc.Contracts;
using Xunit;

namespace Rc.Agent.Tests.Jobs;

public sealed class ManagedTaskRegistryTests
{
    [Fact]
    public async Task StartedJobCanBeRefreshedAndThenReadFromItsPersistedTerminalSnapshot()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(directory.Path);
        await store.InitializeAsync();
        await using var registry = new ManagedTaskRegistry(store);

        var started = await registry.StartAsync(ExecRequest.ForShell(
            ShellKind.PowerShell,
            "Start-Sleep -Milliseconds 500; Write-Output done"));

        Assert.StartsWith("job-", started.Job.JobId, StringComparison.Ordinal);
        Assert.True(started.Job.State is JobState.Running or JobState.Exited);

        (TaskRuntimeStatus Status, bool IsActive) terminalResult = (started, true);
        TaskRuntimeStatus terminal = started;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var current = await registry.GetStatusAsync(started.Job.JobId);
            terminalResult = current;
            terminal = current.Status;
            if (terminal.Job.State is JobState.Exited or JobState.FailedToStart or JobState.Cancelled)
            {
                break;
            }

            await Task.Delay(50);
        }

        Assert.Equal(JobState.Exited, terminal.Job.State);
        Assert.False(terminalResult.IsActive);
        Assert.Equal(0, terminal.Job.ExitCode);
        var persisted = await store.GetJobSnapshotAsync(started.Job.JobId);
        Assert.NotNull(persisted);
        Assert.Equal(JobState.Exited, persisted!.State);
    }
}