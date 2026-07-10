using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Rc.Agent.Jobs;
using Rc.Agent.Persistence;
using Rc.Agent.Security;
using Rc.Contracts;
using Rc.TaskHost;

namespace Rc.Agent.Control;

/// <summary>
/// TLS endpoint for public identity probing and the pre-mTLS pairing exchange.
/// Each connection accepts exactly one bounded JSON request and returns one result.
/// </summary>
public sealed class TlsControlListener : IAsyncDisposable
{
    private const int MaximumLineLength = 1024 * 1024;
    private const int MaximumReturnedOutputBytesPerStream = 256 * 1024;
    private readonly TcpListener listener;
    private readonly AgentTlsIdentity identity;
    private readonly AgentStateStore stateStore;
    private readonly PairingCoordinator pairingCoordinator;
    private readonly CancellationTokenSource shutdown = new();
    private readonly SemaphoreSlim executeOnceGate = new(1, 1);
    private readonly ManagedTaskRegistry taskRegistry;
    private bool started;

    public TlsControlListener(
        AgentTlsIdentity identity,
        AgentStateStore stateStore,
        PairingCoordinator pairingCoordinator,
        int port)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(pairingCoordinator);
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        this.identity = identity;
        this.stateStore = stateStore;
        this.pairingCoordinator = pairingCoordinator;
        taskRegistry = new ManagedTaskRegistry(stateStore);
        listener = new TcpListener(IPAddress.Any, port);
    }

    public void Start()
    {
        if (started)
        {
            throw new InvalidOperationException("The TLS control listener is already started.");
        }

        listener.Start();
        started = true;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (!started)
        {
            throw new InvalidOperationException("Start the TLS control listener before serving requests.");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
        try
        {
            while (!linked.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(linked.Token);
                _ = ServeClientAsync(client, linked.Token);
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (linked.IsCancellationRequested)
        {
        }
    }

    private async Task ServeClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        await using (var network = client.GetStream())
        await using (var tls = new SslStream(network, leaveInnerStreamOpen: false))
        {
            try
            {
                var certificateContext = SslStreamCertificateContext.Create(
                    identity.Certificate,
                    additionalCertificates: null,
                    offline: true);
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificateContext = certificateContext,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                }, cancellationToken);

                using var reader = new StreamReader(tls, new UTF8Encoding(false), false, MaximumLineLength, leaveOpen: true);
                await using var writer = new StreamWriter(tls, new UTF8Encoding(false), MaximumLineLength, leaveOpen: true)
                {
                    AutoFlush = true,
                };
                var requestLine = await reader.ReadLineAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(requestLine) || requestLine.Length > MaximumLineLength)
                {
                    return;
                }

                using var document = JsonDocument.Parse(requestLine);
                if (!document.RootElement.TryGetProperty("kind", out var kindElement)
                    || kindElement.ValueKind != JsonValueKind.String)
                {
                    await WriteFailureAsync(writer, ErrorCode.InvalidRequest, "A control request kind is required.");
                    return;
                }

                switch (kindElement.GetString())
                {
                    case ControlMessageKinds.Hello:
                        await HandleHelloAsync(document.RootElement, writer, cancellationToken);
                        break;
                    case ControlMessageKinds.PairStart:
                        await HandlePairStartAsync(client, document.RootElement, writer, cancellationToken);
                        break;
                    case ControlMessageKinds.PairRound1:
                        await HandlePairRound1Async(document.RootElement, writer);
                        break;
                    case ControlMessageKinds.PairRound2:
                        await HandlePairRound2Async(document.RootElement, writer);
                        break;
                    case ControlMessageKinds.PairComplete:
                        await HandlePairCompleteAsync(document.RootElement, writer, cancellationToken);
                        break;
                    case ControlMessageKinds.ExecOnce:
                        await HandleExecuteOnceAsync(document.RootElement, writer, cancellationToken);
                        break;
                    default:
                        await WriteFailureAsync(writer, ErrorCode.InvalidRequest, "The control request kind is not supported.");
                        break;
                }
            }
            catch (AuthenticationException exception)
            {
                Console.Error.WriteLine($"TLS authentication failed: {exception.Message}");
            }
            catch (IOException)
            {
                // Peers may close during the TLS handshake or after a single request.
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"TLS control connection failed: {exception}");
            }
        }
    }

    private async Task HandleHelloAsync(JsonElement root, StreamWriter writer, CancellationToken cancellationToken)
    {
        var request = root.Deserialize<ControlHelloRequest>(ContractJson.Options);
        if (request is null || request.ProtocolVersion != 1)
        {
            await WriteFailureAsync(writer, ErrorCode.InvalidRequest, "The hello request protocol version is unsupported.");
            return;
        }

        var pairedController = await stateStore.GetPairedControllerAsync(cancellationToken);
        await WriteSuccessAsync(writer, new ControlHelloResponse(
            ProtocolVersion: 1,
            identity.DeviceId,
            identity.CertificateSha256Fingerprint,
            pairedController is not null));
    }

    private async Task HandlePairStartAsync(
        TcpClient client, JsonElement root, StreamWriter writer, CancellationToken cancellationToken)
    {
        var request = root.Deserialize<ControlPairStartRequest>(ContractJson.Options);
        if (request is null || request.ProtocolVersion != 1)
        {
            await WriteFailureAsync(writer, ErrorCode.InvalidRequest, "The pairing request protocol version is unsupported.");
            return;
        }

        if (client.Client.LocalEndPoint is not IPEndPoint localEndpoint)
        {
            await WriteFailureAsync(writer, ErrorCode.Unavailable, "The agent could not determine its local pairing endpoint.");
            return;
        }

        var invitation = await pairingCoordinator.CreateInvitationAsync(
            new PairingEndpoint(localEndpoint.Address, localEndpoint.Port), cancellationToken);
        Console.WriteLine();
        Console.WriteLine("Pairing request received. Enter this one-time code on the controller:");
        Console.WriteLine($"  {invitation.OneTimeCode}  (expires {invitation.ExpiresAtUtc.LocalDateTime:G})");

        var binding = await pairingCoordinator.BindControllerAsync(
            invitation.PairingId, request.ControllerId, request.ControllerCertificate, cancellationToken);
        var agentRound1 = pairingCoordinator.CreateAgentRound1(invitation.PairingId);
        await WriteSuccessAsync(writer, new ControlPairStartResponse(ToContract(binding), invitation.ExpiresAtUtc, agentRound1));
    }

    private async Task HandlePairRound1Async(JsonElement root, StreamWriter writer)
    {
        var request = root.Deserialize<ControlPairRound1Request>(ContractJson.Options);
        if (request is null)
        {
            await WriteFailureAsync(writer, ErrorCode.InvalidRequest, "The pairing round 1 request is invalid.");
            return;
        }

        try
        {
            pairingCoordinator.ReceiveControllerRound1(request.PairingId, request.ControllerRound1);
            await WriteSuccessAsync(writer, new ControlPairRound2Response(pairingCoordinator.CreateAgentRound2(request.PairingId)));
        }
        catch (CryptographicException exception)
        {
            await WriteFailureAsync(writer, ErrorCode.Unauthenticated, $"Pairing validation failed while receiving controller round 1: {exception.Message}");
        }
        catch (InvalidOperationException exception)
        {
            await WriteFailureAsync(writer, ErrorCode.FailedPrecondition, $"Pairing protocol state was invalid for controller round 1: {exception.Message}");
        }
    }

    private async Task HandlePairRound2Async(JsonElement root, StreamWriter writer)
    {
        var request = root.Deserialize<ControlPairRound2Request>(ContractJson.Options);
        if (request is null)
        {
            await WriteFailureAsync(writer, ErrorCode.InvalidRequest, "The pairing round 2 request is invalid.");
            return;
        }

        try
        {
            pairingCoordinator.ReceiveControllerRound2(request.PairingId, request.ControllerRound2);
            await WriteSuccessAsync(writer, new ControlPairRound3Response(pairingCoordinator.CreateAgentRound3(request.PairingId)));
        }
        catch (CryptographicException exception)
        {
            await WriteFailureAsync(writer, ErrorCode.Unauthenticated, $"Pairing validation failed while receiving controller round 2: {exception.Message}");
        }
        catch (InvalidOperationException exception)
        {
            await WriteFailureAsync(writer, ErrorCode.FailedPrecondition, $"Pairing protocol state was invalid for controller round 2: {exception.Message}");
        }
    }

    private async Task HandlePairCompleteAsync(JsonElement root, StreamWriter writer, CancellationToken cancellationToken)
    {
        var request = root.Deserialize<ControlPairCompleteRequest>(ContractJson.Options);
        if (request is null)
        {
            await WriteFailureAsync(writer, ErrorCode.InvalidRequest, "The pairing completion request is invalid.");
            return;
        }

        try
        {
            pairingCoordinator.ReceiveControllerRound3(request.PairingId, request.ControllerRound3);
            var paired = await pairingCoordinator.CompleteAsync(
                request.PairingId,
                new PairingCompletionProof(request.ConfirmationMac, request.CertificateSignature),
                cancellationToken);
            await WriteSuccessAsync(writer, new ControlPairCompleteResponse(paired.ControllerId, paired.PairedAtUtc));
        }
        catch (CryptographicException exception)
        {
            await WriteFailureAsync(writer, ErrorCode.Unauthenticated, $"Pairing validation failed while receiving controller round 3: {exception.Message}");
        }
        catch (InvalidOperationException exception)
        {
            await WriteFailureAsync(writer, ErrorCode.FailedPrecondition, $"Pairing protocol state was invalid for controller round 3: {exception.Message}");
        }
    }

    private async Task HandleExecuteOnceAsync(JsonElement root, StreamWriter writer, CancellationToken cancellationToken)
    {
        var request = root.Deserialize<ControlExecuteOnceRequest>(ContractJson.Options);
        if (request is null || request.ProtocolVersion != 1)
        {
            await WriteFailureAsync(writer, ErrorCode.InvalidRequest, "The exec request protocol version is unsupported.");
            return;
        }

        if (request.Execution.ExecutionIdentity != ExecutionIdentity.CurrentUser)
        {
            await WriteFailureAsync(writer, ErrorCode.FailedPrecondition, "Only current-user execution is supported by exec_once.");
            return;
        }

        var pairedController = await stateStore.GetPairedControllerAsync(cancellationToken);
        if (pairedController is null)
        {
            await WriteFailureAsync(writer, ErrorCode.Unauthenticated, "Pair a controller before executing commands.");
            return;
        }

        if (!string.Equals(pairedController.ControllerId, request.ControllerId, StringComparison.Ordinal))
        {
            await WriteFailureAsync(writer, ErrorCode.Unauthorized, "The request controller identity does not match the paired controller.");
            return;
        }

        var certificateBytes = pairedController.Certificate;
        try
        {
            using var certificate = new X509Certificate2(certificateBytes);
            using var publicKey = certificate.GetECDsaPublicKey();
            if (publicKey is null || !ControlRequestAuthentication.VerifyExecuteOnce(
                    identity.DeviceId,
                    request.ControllerId,
                    request.Execution,
                    request.Signature,
                    publicKey))
            {
                await WriteFailureAsync(writer, ErrorCode.Unauthorized, "The paired controller signature is invalid.");
                return;
            }
        }
        catch (CryptographicException)
        {
            await WriteFailureAsync(writer, ErrorCode.Internal, "The paired controller certificate cannot validate execution requests.");
            return;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(certificateBytes);
        }

        if (!await executeOnceGate.WaitAsync(0, cancellationToken))
        {
            await WriteFailureAsync(writer, ErrorCode.ResourceExhausted, "Another exec_once command is still running.");
            return;
        }

        try
        {
            var jobId = "exec-" + Guid.NewGuid().ToString("N");
            var launch = new TaskLaunchRequest(
                jobId,
                request.Execution,
                ExecutionIdentity.CurrentUser,
                stateStore.DataRoot,
                "rc-exec-" + Guid.NewGuid().ToString("N"),
                TimeSpan.FromSeconds(5));
            await using var runner = new TaskHostRunner(launch);
            var status = await runner.RunAsync(cancellationToken);
            await stateStore.SaveJobSnapshotAsync(status.Job, cancellationToken);
            await stateStore.RegisterTaskHostOutputSegmentsAsync(jobId, cancellationToken);

            var standardOutput = await ReadOutputAsync(jobId, JobOutputKind.Stdout, cancellationToken);
            var standardError = await ReadOutputAsync(jobId, JobOutputKind.Stderr, cancellationToken);
            await WriteSuccessAsync(writer, new ControlExecuteOnceResponse(
                status.Job,
                standardOutput,
                status.StdoutLength > standardOutput.Length,
                standardError,
                status.StderrLength > standardError.Length));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await WriteFailureAsync(writer, ErrorCode.Internal, $"The command could not be executed: {exception.Message}");
        }
        finally
        {
            executeOnceGate.Release();
        }
    }

    private async Task HandleJobStartAsync(JsonElement root, StreamWriter writer, CancellationToken cancellationToken)
    {
        var request = root.Deserialize<ControlJobStartRequest>(ContractJson.Options);
        if (request is null || request.ProtocolVersion != 1)
        {
            await WriteFailureAsync(writer, ErrorCode.InvalidRequest, "The job start request protocol version is unsupported.");
            return;
        }

        if (request.Execution.ExecutionIdentity != ExecutionIdentity.CurrentUser)
        {
            await WriteFailureAsync(writer, ErrorCode.FailedPrecondition, "Only current-user execution is supported by job_start.");
            return;
        }

        if (!await VerifyPairedControllerRequestAsync(
                request.ControllerId,
                request.Signature,
                key => ControlRequestAuthentication.VerifyJobStart(identity.DeviceId, request.ControllerId, request.Execution, request.Signature, key),
                writer,
                cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var status = await taskRegistry.StartAsync(request.Execution, cancellationToken).ConfigureAwait(false);
            await WriteSuccessAsync(writer, new ControlJobStartResponse(status));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await WriteFailureAsync(writer, ErrorCode.Internal, $"The job could not be started: {exception.Message}");
        }
    }

    private async Task HandleJobStatusAsync(JsonElement root, StreamWriter writer, CancellationToken cancellationToken)
    {
        var request = root.Deserialize<ControlJobStatusRequest>(ContractJson.Options);
        if (request is null || request.ProtocolVersion != 1 || string.IsNullOrWhiteSpace(request.JobId))
        {
            await WriteFailureAsync(writer, ErrorCode.InvalidRequest, "The job status request is invalid.");
            return;
        }

        if (!await VerifyPairedControllerRequestAsync(
                request.ControllerId,
                request.Signature,
                key => ControlRequestAuthentication.VerifyJobStatus(identity.DeviceId, request.ControllerId, request.JobId, request.Signature, key),
                writer,
                cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var result = await taskRegistry.GetStatusAsync(request.JobId, cancellationToken).ConfigureAwait(false);
            await WriteSuccessAsync(writer, new ControlJobStatusResponse(result.Status, result.IsActive));
        }
        catch (KeyNotFoundException)
        {
            await WriteFailureAsync(writer, ErrorCode.NotFound, $"No job exists with ID '{request.JobId}'.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await WriteFailureAsync(writer, ErrorCode.Internal, $"The job status could not be read: {exception.Message}");
        }
    }

    private async Task HandleJobListAsync(JsonElement root, StreamWriter writer, CancellationToken cancellationToken)
    {
        var request = root.Deserialize<ControlJobListRequest>(ContractJson.Options);
        if (request is null || request.ProtocolVersion != 1 || (request.State is { } state && !Enum.IsDefined(state)))
        {
            await WriteFailureAsync(writer, ErrorCode.InvalidRequest, "The job list request is invalid.");
            return;
        }

        if (!await VerifyPairedControllerRequestAsync(
                request.ControllerId,
                request.Signature,
                key => ControlRequestAuthentication.VerifyJobList(identity.DeviceId, request.ControllerId, request.State, request.Signature, key),
                writer,
                cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var jobs = await taskRegistry.ListAsync(request.State, cancellationToken).ConfigureAwait(false);
            await WriteSuccessAsync(writer, new ControlJobListResponse(jobs));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await WriteFailureAsync(writer, ErrorCode.Internal, $"The job list could not be read: {exception.Message}");
        }
    }

    private async Task<bool> VerifyPairedControllerRequestAsync(
        string controllerId,
        byte[] signature,
        Func<ECDsa, bool> verifySignature,
        StreamWriter writer,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(controllerId) || signature is null || signature.Length == 0)
        {
            await WriteFailureAsync(writer, ErrorCode.InvalidRequest, "A controller identity and signature are required.");
            return false;
        }

        var pairedController = await stateStore.GetPairedControllerAsync(cancellationToken).ConfigureAwait(false);
        if (pairedController is null)
        {
            await WriteFailureAsync(writer, ErrorCode.Unauthenticated, "Pair a controller before making job requests.");
            return false;
        }

        if (!string.Equals(pairedController.ControllerId, controllerId, StringComparison.Ordinal))
        {
            await WriteFailureAsync(writer, ErrorCode.Unauthorized, "The request controller identity does not match the paired controller.");
            return false;
        }

        var certificateBytes = pairedController.Certificate;
        try
        {
            using var certificate = new X509Certificate2(certificateBytes);
            using var publicKey = certificate.GetECDsaPublicKey();
            if (publicKey is null || !verifySignature(publicKey))
            {
                await WriteFailureAsync(writer, ErrorCode.Unauthorized, "The paired controller signature is invalid.");
                return false;
            }

            return true;
        }
        catch (CryptographicException)
        {
            await WriteFailureAsync(writer, ErrorCode.Internal, "The paired controller certificate cannot validate job requests.");
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(certificateBytes);
        }
    }
    private async Task<byte[]> ReadOutputAsync(string jobId, JobOutputKind stream, CancellationToken cancellationToken)
    {
        var streamDirectory = Path.Combine(
            stateStore.DataRoot,
            "segments",
            jobId,
            stream == JobOutputKind.Stdout ? "stdout" : "stderr");
        if (!Directory.Exists(streamDirectory))
        {
            return [];
        }

        await using var collected = new MemoryStream();
        foreach (var path in Directory.EnumerateFiles(streamDirectory, "*.seg").OrderBy(path => path, StringComparer.Ordinal))
        {
            var remaining = MaximumReturnedOutputBytesPerStream - checked((int)collected.Length);
            if (remaining <= 0)
            {
                break;
            }

            await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, useAsync: true);
            var buffer = new byte[Math.Min(16 * 1024, remaining)];
            while (remaining > 0)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                await collected.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                remaining -= read;
            }
        }

        return collected.ToArray();
    }
    private static ControlPairingBinding ToContract(PairingBinding binding) => new(
        binding.PairingId,
        binding.AgentDeviceId,
        binding.ControllerId,
        binding.AgentEndpoint.Address.ToString(),
        binding.AgentEndpoint.Port,
        binding.AgentCertificateFingerprint,
        binding.AgentSpkiFingerprint,
        binding.ControllerCertificateFingerprint,
        binding.ControllerSpkiFingerprint);

    private static Task WriteSuccessAsync<T>(StreamWriter writer, T response) =>
        writer.WriteLineAsync(JsonSerializer.Serialize(Result.Success(response), ContractJson.Options));

    private static Task WriteFailureAsync(StreamWriter writer, ErrorCode code, string message) =>
        writer.WriteLineAsync(JsonSerializer.Serialize(
            Result.Failure<object>(new RemoteError(code, message, Retryable: false)), ContractJson.Options));

    public async ValueTask DisposeAsync()
    {
        shutdown.Cancel();
        listener.Stop();
        await taskRegistry.DisposeAsync().ConfigureAwait(false);
        shutdown.Dispose();
        executeOnceGate.Dispose();
    }
}