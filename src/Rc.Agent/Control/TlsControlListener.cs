using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Rc.Agent.Persistence;
using Rc.Agent.Security;
using Rc.Contracts;

namespace Rc.Agent.Control;

/// <summary>
/// TLS endpoint for public identity probing and the pre-mTLS pairing exchange.
/// Each connection accepts exactly one bounded JSON request and returns one result.
/// </summary>
public sealed class TlsControlListener : IAsyncDisposable
{
    private const int MaximumLineLength = 16 * 1024;
    private readonly TcpListener listener;
    private readonly AgentTlsIdentity identity;
    private readonly AgentStateStore stateStore;
    private readonly PairingCoordinator pairingCoordinator;
    private readonly CancellationTokenSource shutdown = new();
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

        pairingCoordinator.ReceiveControllerRound1(request.PairingId, request.ControllerRound1);
        await WriteSuccessAsync(writer, new ControlPairRound2Response(pairingCoordinator.CreateAgentRound2(request.PairingId)));
    }

    private async Task HandlePairRound2Async(JsonElement root, StreamWriter writer)
    {
        var request = root.Deserialize<ControlPairRound2Request>(ContractJson.Options);
        if (request is null)
        {
            await WriteFailureAsync(writer, ErrorCode.InvalidRequest, "The pairing round 2 request is invalid.");
            return;
        }

        pairingCoordinator.ReceiveControllerRound2(request.PairingId, request.ControllerRound2);
        await WriteSuccessAsync(writer, new ControlPairRound3Response(pairingCoordinator.CreateAgentRound3(request.PairingId)));
    }

    private async Task HandlePairCompleteAsync(JsonElement root, StreamWriter writer, CancellationToken cancellationToken)
    {
        var request = root.Deserialize<ControlPairCompleteRequest>(ContractJson.Options);
        if (request is null)
        {
            await WriteFailureAsync(writer, ErrorCode.InvalidRequest, "The pairing completion request is invalid.");
            return;
        }

        pairingCoordinator.ReceiveControllerRound3(request.PairingId, request.ControllerRound3);
        var paired = await pairingCoordinator.CompleteAsync(
            request.PairingId,
            new PairingCompletionProof(request.ConfirmationMac, request.CertificateSignature),
            cancellationToken);
        await WriteSuccessAsync(writer, new ControlPairCompleteResponse(paired.ControllerId, paired.PairedAtUtc));
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

    public ValueTask DisposeAsync()
    {
        shutdown.Cancel();
        listener.Stop();
        shutdown.Dispose();
        return ValueTask.CompletedTask;
    }
}