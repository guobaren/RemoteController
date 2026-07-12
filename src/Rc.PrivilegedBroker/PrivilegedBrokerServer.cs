using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Rc.Contracts;
using Rc.TaskHost;

namespace Rc.PrivilegedBroker;

public sealed class PrivilegedBrokerServer : IAsyncDisposable
{
    private const int MaximumRequestCharacters = 1024 * 1024;
    private readonly BrokerOptions options;
    private readonly byte[] secret;
    private readonly CancellationTokenSource stopping = new();
    private readonly Channel<LaunchWorkItem> queue = Channel.CreateUnbounded<LaunchWorkItem>(
        new UnboundedChannelOptions { SingleWriter = false, SingleReader = false });
    private readonly ConcurrentDictionary<string, TaskHostRunner> runners = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> usedNonces = new(StringComparer.Ordinal);
    private readonly Task[] workers;

    public PrivilegedBrokerServer(BrokerOptions options, byte[] secret)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.secret = secret?.ToArray() ?? throw new ArgumentNullException(nameof(secret));
        if (this.secret.Length < 32)
        {
            throw new ArgumentException("The broker secret must contain at least 32 bytes.", nameof(secret));
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.Concurrency);
        workers = Enumerable.Range(0, options.Concurrency).Select(_ => RunWorkerAsync()).ToArray();
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping.Token);
        while (!linked.IsCancellationRequested)
        {
            var server = new NamedPipeServerStream(
                options.PipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.WriteThrough);
            try
            {
                await server.WaitForConnectionAsync(linked.Token).ConfigureAwait(false);
                _ = HandleConnectionAndDisposeAsync(server, linked.Token);
            }
            catch
            {
                server.Dispose();
                throw;
            }
        }
    }

    private async Task HandleConnectionAndDisposeAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        using (server)
        using (var reader = new StreamReader(server, new UTF8Encoding(false), false, 4096, leaveOpen: true))
        using (var writer = new StreamWriter(server, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true })
        {
            BrokerLaunchResponse response;
            try
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null || line.Length > MaximumRequestCharacters)
                {
                    throw new InvalidDataException("The broker request was empty or too large.");
                }

                var request = JsonSerializer.Deserialize<BrokerLaunchRequest>(line, ContractJson.Options)
                    ?? throw new InvalidDataException("The broker request was empty.");
                response = await ValidateAndQueueAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                response = new BrokerLaunchResponse(false, null, new RemoteError(ErrorCode.InvalidRequest, exception.Message, false));
            }

            await writer.WriteLineAsync(JsonSerializer.Serialize(response, ContractJson.Options)).ConfigureAwait(false);
        }
    }

    private async Task<BrokerLaunchResponse> ValidateAndQueueAsync(BrokerLaunchRequest request, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (!BrokerRequestAuthentication.VerifyLaunch(request, secret, now))
        {
            return new BrokerLaunchResponse(false, null, new RemoteError(ErrorCode.Unauthenticated, "Broker request authentication failed.", false));
        }

        var nonce = Convert.ToHexString(request.Nonce);
        RemoveExpiredNonces(now);
        if (!usedNonces.TryAdd(nonce, request.IssuedAtUtc))
        {
            return new BrokerLaunchResponse(false, null, new RemoteError(ErrorCode.Conflict, "The broker request nonce was already used.", false));
        }

        var launch = request.Launch;
        if (launch.ExecutionIdentity != ExecutionIdentity.ElevatedBroker ||
            launch.Execution.ExecutionIdentity != ExecutionIdentity.ElevatedBroker)
        {
            return new BrokerLaunchResponse(false, null, new RemoteError(ErrorCode.FailedPrecondition, "The broker accepts only explicitly elevated launches.", false));
        }

        if (!IsWithinAllowedDataRoot(launch.DataRoot))
        {
            return new BrokerLaunchResponse(false, null, new RemoteError(ErrorCode.Unauthorized, "The task data root is outside the broker allowlist.", false));
        }

        if (runners.ContainsKey(launch.JobId))
        {
            return new BrokerLaunchResponse(false, null, new RemoteError(ErrorCode.Conflict, "The broker job ID already exists.", false));
        }

        var completion = new TaskCompletionSource<BrokerLaunchResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        await queue.Writer.WriteAsync(new LaunchWorkItem(launch, completion), cancellationToken).ConfigureAwait(false);
        return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RunWorkerAsync()
    {
        await foreach (var item in queue.Reader.ReadAllAsync(stopping.Token).ConfigureAwait(false))
        {
            TaskHostRunner? runner = null;
            try
            {
                runner = new TaskHostRunner(item.Launch, allowElevatedExecution: true);
                if (!runners.TryAdd(item.Launch.JobId, runner))
                {
                    throw new InvalidOperationException("The broker job ID already exists.");
                }

                var lifetime = runner.RunAsync(stopping.Token);
                await runner.Started.WaitAsync(TimeSpan.FromSeconds(10), stopping.Token).ConfigureAwait(false);
                var status = runner.GetStatus();
                item.Completion.TrySetResult(new BrokerLaunchResponse(true, status));
                _ = ObserveRunnerAsync(item.Launch, runner, lifetime);
            }
            catch (Exception exception)
            {
                if (runner is not null)
                {
                    runners.TryRemove(item.Launch.JobId, out _);
                    await runner.DisposeAsync().ConfigureAwait(false);
                }
                item.Completion.TrySetResult(new BrokerLaunchResponse(false, null, new RemoteError(ErrorCode.Internal, exception.Message, false)));
            }
        }
    }

    private async Task ObserveRunnerAsync(TaskLaunchRequest launch, TaskHostRunner runner, Task<TaskRuntimeStatus> lifetime)
    {
        try
        {
            var terminal = await lifetime.ConfigureAwait(false);
            await PersistTerminalStatusAsync(launch.DataRoot, terminal, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            runners.TryRemove(launch.JobId, out _);
            await runner.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task PersistTerminalStatusAsync(string dataRoot, TaskRuntimeStatus status, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(dataRoot, "task-status");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, status.Job.JobId + ".json");
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(status, ContractJson.Options), cancellationToken).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }
    private bool IsWithinAllowedDataRoot(string dataRoot)
    {
        var allowed = Path.GetFullPath(options.AllowedDataRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(dataRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(allowed, StringComparison.OrdinalIgnoreCase);
    }

    private void RemoveExpiredNonces(DateTimeOffset now)
    {
        foreach (var pair in usedNonces)
        {
            if ((now - pair.Value).Duration() > BrokerRequestAuthentication.MaximumClockSkew)
            {
                usedNonces.TryRemove(pair.Key, out _);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        queue.Writer.TryComplete();
        stopping.Cancel();
        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }

        foreach (var runner in runners.Values)
        {
            await runner.DisposeAsync().ConfigureAwait(false);
        }
        runners.Clear();
        CryptographicOperations.ZeroMemory(secret);
        stopping.Dispose();
    }

    private sealed record LaunchWorkItem(TaskLaunchRequest Launch, TaskCompletionSource<BrokerLaunchResponse> Completion);
}
