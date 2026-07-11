using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rc.Contracts;

namespace Rc.Cli.Security;

internal sealed class AuthenticatedControlConnection : IAsyncDisposable
{
    private const int MaximumLineLength = 1024 * 1024;
    private readonly IPEndPoint endpoint;
    private readonly string fingerprint;
    private readonly SemaphoreSlim requestGate = new(1, 1);
    private PinnedTlsConnection? transport;
    private StreamWriter? writer;
    private StreamReader? reader;

    private AuthenticatedControlConnection(IPEndPoint endpoint, string fingerprint)
    {
        this.endpoint = endpoint;
        this.fingerprint = fingerprint;
    }

    public string AgentDeviceId { get; private set; } = string.Empty;
    public string ControllerId { get; private set; } = string.Empty;
    public DateTimeOffset ExpiresAtUtc { get; private set; }

    public static async Task<AuthenticatedControlConnection> ConnectAsync(IPEndPoint endpoint, string fingerprint)
    {
        var connection = new AuthenticatedControlConnection(endpoint, fingerprint);
        try
        {
            await connection.OpenSessionAsync().ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<TResponse> SendAsync<TResponse>(object request, bool retryOnDisconnect = false)
    {
        await requestGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ExpiresAtUtc <= DateTimeOffset.UtcNow.AddSeconds(5))
            {
                await OpenSessionAsync().ConfigureAwait(false);
            }
            try
            {
                return await SendCoreAsync<TResponse>(request).ConfigureAwait(false);
            }
            catch (Exception exception) when (retryOnDisconnect && exception is IOException or SocketException)
            {
                await OpenSessionAsync().ConfigureAwait(false);
                return await SendCoreAsync<TResponse>(request).ConfigureAwait(false);
            }
        }
        finally
        {
            requestGate.Release();
        }
    }

    private async Task OpenSessionAsync()
    {
        await DisposeTransportAsync().ConfigureAwait(false);
        transport = await PinnedTlsConnection.ConnectAsync(endpoint, fingerprint).ConfigureAwait(false);
        writer = new StreamWriter(transport.Stream, new UTF8Encoding(false), MaximumLineLength, leaveOpen: true) { AutoFlush = true };
        reader = new StreamReader(transport.Stream, new UTF8Encoding(false), false, MaximumLineLength, leaveOpen: true);

        var hello = await SendCoreAsync<ControlHelloResponse>(new ControlHelloRequest(1)).ConfigureAwait(false);
        if (!hello.HasPairedController)
        {
            throw new InvalidOperationException("This agent has no paired controller. Run rcctl pair first.");
        }

        using var identity = await ControllerIdentity.LoadOrCreateAsync(Environment.MachineName).ConfigureAwait(false);
        var challenge = await SendCoreAsync<ControlSessionStartResponse>(new ControlSessionStartRequest(1, identity.ControllerId)).ConfigureAwait(false);
        using var privateKey = identity.GetPrivateKey();
        var signature = ControlRequestAuthentication.SignSessionAuthentication(
            challenge.AgentDeviceId,
            identity.ControllerId,
            challenge.SessionId,
            challenge.Challenge,
            challenge.ExpiresAtUtc,
            privateKey);
        try
        {
            var authenticated = await SendCoreAsync<ControlSessionAuthenticateResponse>(
                new ControlSessionAuthenticateRequest(1, challenge.SessionId, identity.ControllerId, signature)).ConfigureAwait(false);
            AgentDeviceId = challenge.AgentDeviceId;
            ControllerId = authenticated.ControllerId;
            ExpiresAtUtc = authenticated.ExpiresAtUtc;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    private async Task<TResponse> SendCoreAsync<TResponse>(object request)
    {
        if (writer is null || reader is null)
        {
            throw new IOException("The authenticated control connection is not open.");
        }
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, ContractJson.Options)).ConfigureAwait(false);
        var line = await reader.ReadLineAsync().ConfigureAwait(false);
        var response = line is null ? null : JsonSerializer.Deserialize<ResultEnvelope<TResponse>>(line, ContractJson.Options);
        if (response is not { Ok: true, Result: not null })
        {
            throw new InvalidOperationException(response?.Error?.Message ?? "The agent did not return a valid response.");
        }
        return response.Result;
    }

    private async Task DisposeTransportAsync()
    {
        if (writer is not null)
        {
            await writer.DisposeAsync().ConfigureAwait(false);
            writer = null;
        }
        reader?.Dispose();
        reader = null;
        if (transport is not null)
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            transport = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await requestGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisposeTransportAsync().ConfigureAwait(false);
        }
        finally
        {
            requestGate.Release();
            requestGate.Dispose();
        }
    }
}