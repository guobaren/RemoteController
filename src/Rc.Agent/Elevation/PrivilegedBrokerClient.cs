using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rc.Contracts;
using Rc.TaskHost;

namespace Rc.Agent.Elevation;

public sealed class PrivilegedBrokerClient
{
    private readonly string pipeName;
    private readonly string secretPath;

    public PrivilegedBrokerClient(string pipeName, string secretPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretPath);
        this.pipeName = pipeName;
        this.secretPath = Path.GetFullPath(secretPath);
    }

    public async Task<TaskRuntimeStatus> LaunchAsync(TaskLaunchRequest launch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launch);
        var secret = await File.ReadAllBytesAsync(secretPath, cancellationToken).ConfigureAwait(false);
        try
        {
            var requestId = Guid.NewGuid().ToString("N");
            var issuedAt = DateTimeOffset.UtcNow;
            var nonce = RandomNumberGenerator.GetBytes(32);
            var tag = BrokerRequestAuthentication.SignLaunch(requestId, issuedAt, nonce, launch, secret);
            var request = new BrokerLaunchRequest(BrokerRequestAuthentication.ProtocolVersion, requestId, issuedAt, nonce, launch, tag);

            using var pipe = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000, cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, ContractJson.Options)).ConfigureAwait(false);
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new IOException("The privileged broker closed the pipe without a response.");
            var response = JsonSerializer.Deserialize<BrokerLaunchResponse>(line, ContractJson.Options)
                ?? throw new InvalidDataException("The privileged broker returned an empty response.");
            if (!response.Accepted || response.Status is null)
            {
                throw new InvalidOperationException(response.Error?.Message ?? "The privileged broker rejected the launch.");
            }
            return response.Status;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }
}

public sealed class PrivilegedBrokerTaskHostLauncher : Jobs.IManagedTaskHostLauncher
{
    private readonly PrivilegedBrokerClient client;

    public PrivilegedBrokerTaskHostLauncher(string pipeName, string secretPath)
    {
        client = new PrivilegedBrokerClient(pipeName, secretPath);
    }

    public async Task<Jobs.ManagedTaskHostHandle> LaunchAsync(TaskLaunchRequest request, CancellationToken cancellationToken = default)
    {
        var status = await client.LaunchAsync(request, cancellationToken).ConfigureAwait(false);
        var owner = new BrokerTaskMonitor(request);
        return new Jobs.ManagedTaskHostHandle(
            request.ControlPipeName,
            status.ProcessId,
            owner.Completion,
            owner,
            survivesAgentShutdown: true,
            initialStatus: status);
    }

    private sealed class BrokerTaskMonitor : IAsyncDisposable
    {
        private readonly TaskLaunchRequest request;
        private readonly CancellationTokenSource stopping = new();

        public BrokerTaskMonitor(TaskLaunchRequest request)
        {
            this.request = request;
            Completion = MonitorAsync();
        }

        public Task<TaskRuntimeStatus> Completion { get; }

        private async Task<TaskRuntimeStatus> MonitorAsync()
        {
            while (true)
            {
                stopping.Token.ThrowIfCancellationRequested();
                try
                {
                    var response = await TaskHostControlClient.SendAsync(
                        request.ControlPipeName,
                        new TaskControlMessage(TaskControlKind.GetStatus),
                        TimeSpan.FromSeconds(2),
                        stopping.Token).ConfigureAwait(false);
                    if (response.Status.Job.State is JobState.Exited or JobState.FailedToStart or JobState.Cancelled or JobState.InterruptedByReboot or JobState.HostCrashed)
                    {
                        return response.Status;
                    }
                }
                catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException or OperationCanceledException && !stopping.IsCancellationRequested)
                {
                    var terminal = await TryReadTerminalStatusAsync(stopping.Token).ConfigureAwait(false);
                    if (terminal is not null)
                    {
                        return terminal;
                    }
                }

                await Task.Delay(250, stopping.Token).ConfigureAwait(false);
            }
        }

        private async Task<TaskRuntimeStatus?> TryReadTerminalStatusAsync(CancellationToken cancellationToken)
        {
            var path = Path.Combine(request.DataRoot, "task-status", request.JobId + ".json");
            if (!File.Exists(path))
            {
                return null;
            }

            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<TaskRuntimeStatus>(json, ContractJson.Options);
        }

        public ValueTask DisposeAsync()
        {
            stopping.Cancel();
            stopping.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

