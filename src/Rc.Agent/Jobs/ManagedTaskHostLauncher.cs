using System.Diagnostics;
using System.Text.Json;
using Rc.Contracts;
using Rc.TaskHost;

namespace Rc.Agent.Jobs;

public interface IManagedTaskHostLauncher
{
    Task<ManagedTaskHostHandle> LaunchAsync(TaskLaunchRequest request, CancellationToken cancellationToken = default);
}

public sealed class ManagedTaskHostHandle : IAsyncDisposable
{
    private readonly IAsyncDisposable owner;

    public ManagedTaskHostHandle(string controlPipeName, int? processId, Task<TaskRuntimeStatus> completion, IAsyncDisposable owner, bool survivesAgentShutdown, TaskRuntimeStatus? initialStatus = null)
    {
        ControlPipeName = controlPipeName;
        ProcessId = processId;
        Completion = completion;
        this.owner = owner;
        SurvivesAgentShutdown = survivesAgentShutdown;
        InitialStatus = initialStatus;
    }

    public string ControlPipeName { get; }
    public int? ProcessId { get; }
    public Task<TaskRuntimeStatus> Completion { get; }
    public bool SurvivesAgentShutdown { get; }
    public TaskRuntimeStatus? InitialStatus { get; }

    public ValueTask DisposeAsync() => owner.DisposeAsync();
}

public sealed class UnavailableElevatedTaskHostLauncher : IManagedTaskHostLauncher
{
    public Task<ManagedTaskHostHandle> LaunchAsync(TaskLaunchRequest request, CancellationToken cancellationToken = default) =>
        Task.FromException<ManagedTaskHostHandle>(new InvalidOperationException("The privileged broker is not configured or available."));
}
public sealed class InProcessTaskHostLauncher : IManagedTaskHostLauncher
{
    public Task<ManagedTaskHostHandle> LaunchAsync(TaskLaunchRequest request, CancellationToken cancellationToken = default)
    {
        var runner = new TaskHostRunner(request);
        var completion = runner.RunAsync(cancellationToken);
        return Task.FromResult(new ManagedTaskHostHandle(request.ControlPipeName, null, completion, runner, survivesAgentShutdown: false));
    }
}

public sealed class ExternalTaskHostLauncher : IManagedTaskHostLauncher
{
    private readonly string executablePath;

    public ExternalTaskHostLauncher(string? executablePath = null)
    {
        this.executablePath = executablePath ?? ResolveExecutablePath();
    }

    public async Task<ManagedTaskHostHandle> LaunchAsync(TaskLaunchRequest request, CancellationToken cancellationToken = default)
    {
        var requestDirectory = Path.Combine(request.DataRoot, "task-requests");
        Directory.CreateDirectory(requestDirectory);
        var requestPath = Path.Combine(requestDirectory, request.JobId + ".json");
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request, ContractJson.Options), cancellationToken).ConfigureAwait(false);

        var startInfo = CreateStartInfo(executablePath, requestPath);
        Process? process = null;
        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("The task-host process could not be started.");
            var owner = new ExternalProcessOwner(process, requestPath);
            return new ManagedTaskHostHandle(request.ControlPipeName, process.Id, owner.Completion, owner, survivesAgentShutdown: true);
        }
        catch
        {
            process?.Dispose();
            File.Delete(requestPath);
            throw;
        }
    }

    private static ProcessStartInfo CreateStartInfo(string path, string requestPath)
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (string.Equals(Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
            startInfo.ArgumentList.Add(path);
        }
        else
        {
            startInfo.FileName = path;
        }
        startInfo.ArgumentList.Add("--request");
        startInfo.ArgumentList.Add(requestPath);
        return startInfo;
    }

    public static string ResolveExecutablePath()
    {
        var configured = Environment.GetEnvironmentVariable("RC_TASKHOST_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Rc.TaskHost.exe"),
            Path.Combine(AppContext.BaseDirectory, "Rc.TaskHost.dll"),
        };
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("Rc.TaskHost was not found beside Rc.Agent. Set RC_TASKHOST_PATH to its executable or DLL path.");
    }

    private sealed class ExternalProcessOwner : IAsyncDisposable
    {
        private static readonly char[] LineSeparators = [Convert.ToChar(13), Convert.ToChar(10)];
        private readonly Process process;
        private readonly string requestPath;

        public ExternalProcessOwner(Process process, string requestPath)
        {
            this.process = process;
            this.requestPath = requestPath;
            Completion = ObserveAsync();
        }

        public Task<TaskRuntimeStatus> Completion { get; }

        private async Task<TaskRuntimeStatus> ObserveAsync()
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().ConfigureAwait(false);
            var output = await stdout.ConfigureAwait(false);
            var error = await stderr.ConfigureAwait(false);
            var lastLine = output.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (lastLine is not null)
            {
                var status = JsonSerializer.Deserialize<TaskRuntimeStatus>(lastLine, ContractJson.Options);
                if (status is not null)
                {
                    return status;
                }
            }

            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? $"TaskHost exited with code {process.ExitCode} without a terminal status."
                : $"TaskHost exited with code {process.ExitCode}: {error.Trim()}");
        }

        public ValueTask DisposeAsync()
        {
            process.Dispose();
            try
            {
                File.Delete(requestPath);
            }
            catch (IOException)
            {
            }
            return ValueTask.CompletedTask;
        }
    }
}