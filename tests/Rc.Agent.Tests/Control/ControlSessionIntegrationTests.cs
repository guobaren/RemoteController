using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Rc.Agent.Configuration;
using Rc.Agent.Control;
using Rc.Agent.Persistence;
using Rc.Agent.Security;
using Rc.Agent.Tests.Persistence;
using Rc.Contracts;
using Xunit;

namespace Rc.Agent.Tests.Control;

public sealed class ControlSessionIntegrationTests
{
    [Fact]
    public async Task AuthenticatedSessionReusesOneTlsConnectionForMultipleUnsignedRequests()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(directory.Path);
        await store.InitializeAsync();
        var certificateManager = new AgentCertificateManager(store);
        using var agentIdentity = await certificateManager.GetOrCreateAsync();
        using var pairingCoordinator = new PairingCoordinator(store, certificateManager);
        using var controllerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var controllerRequest = new CertificateRequest("CN=session-test-controller", controllerKey, HashAlgorithmName.SHA256);
        using var controllerCertificate = controllerRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        const string controllerId = "session-test-controller";
        await store.SavePairedControllerAsync(new PairedController(controllerId, controllerCertificate.Export(X509ContentType.Cert), DateTimeOffset.UtcNow));

        var port = ReservePort();
        await using var listener = new TlsControlListener(agentIdentity, store, pairingCoordinator, port);
        await listener.InitializeAsync();
        listener.Start();
        using var cancellation = new CancellationTokenSource();
        var listenerTask = listener.RunAsync(cancellation.Token);
        try
        {
            using var client = new TcpClient(AddressFamily.InterNetwork);
            await client.ConnectAsync(IPAddress.Loopback, port);
            await using var tls = new SslStream(client.GetStream(), false, (_, certificate, _, _) =>
                certificate is not null && string.Equals(
                    Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData())),
                    agentIdentity.CertificateSha256Fingerprint,
                    StringComparison.Ordinal));
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            });
            using var reader = new StreamReader(tls, new UTF8Encoding(false), false, 1024 * 1024, leaveOpen: true);
            await using var writer = new StreamWriter(tls, new UTF8Encoding(false), 1024 * 1024, leaveOpen: true) { AutoFlush = true };

            var hello = await SendAsync<ControlHelloResponse>(writer, reader, new ControlHelloRequest(1));
            var challenge = await SendAsync<ControlSessionStartResponse>(writer, reader, new ControlSessionStartRequest(1, controllerId));
            var signature = ControlRequestAuthentication.SignSessionAuthentication(
                hello.DeviceId, controllerId, challenge.SessionId, challenge.Challenge, challenge.ExpiresAtUtc, controllerKey);
            var authenticated = await SendAsync<ControlSessionAuthenticateResponse>(writer, reader,
                new ControlSessionAuthenticateRequest(1, challenge.SessionId, controllerId, signature));

            var first = await SendAsync<ControlJobListResponse>(writer, reader, new ControlJobListRequest(1, controllerId, null, []));
            var second = await SendAsync<ControlJobListResponse>(writer, reader, new ControlJobListRequest(1, controllerId, JobState.Running, []));

            Assert.Equal(challenge.SessionId, authenticated.SessionId);
            Assert.Empty(first.Jobs);
            Assert.Empty(second.Jobs);

            await store.RemovePairedControllerAsync();
            await writer.WriteLineAsync(JsonSerializer.Serialize(
                new ControlJobListRequest(1, controllerId, null, []), ContractJson.Options));
            var revokedLine = await reader.ReadLineAsync();
            var revoked = JsonSerializer.Deserialize<ResultEnvelope<ControlJobListResponse>>(revokedLine!, ContractJson.Options);
            Assert.NotNull(revoked);
            Assert.False(revoked!.Ok);
            Assert.Equal(ErrorCode.Unauthenticated, revoked.Error?.Code);
            Assert.Contains(await store.ListAuditEventsAsync(), item => item.EventType == "session.revoked");
        }
        finally
        {
            cancellation.Cancel();
            await listenerTask;
        }
    }

    [Fact]
    public async Task AuthenticatedTlsSessionAcceptsFileReadWriteAndResumableUpload()
    {
        using var directory = new TemporaryDirectory();
        var stateRoot = Path.Combine(directory.Path, "state");
        var fileRoot = Path.Combine(directory.Path, "files");
        await using var store = new AgentStateStore(stateRoot);
        await store.InitializeAsync();
        var certificateManager = new AgentCertificateManager(store);
        using var agentIdentity = await certificateManager.GetOrCreateAsync();
        using var pairingCoordinator = new PairingCoordinator(store, certificateManager);
        using var controllerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var controllerRequest = new CertificateRequest("CN=file-test-controller", controllerKey, HashAlgorithmName.SHA256);
        using var controllerCertificate = controllerRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        const string controllerId = "file-test-controller";
        await store.SavePairedControllerAsync(new PairedController(controllerId, controllerCertificate.Export(X509ContentType.Cert), DateTimeOffset.UtcNow));
        var options = new AgentOptions
        {
            FileRoot = fileRoot,
            TransferQuotaBytes = 1024,
            MaximumTransferChunkBytes = 4,
            MaximumAtomicWriteBytes = 16,
            TransferSessionLifetime = TimeSpan.FromHours(1),
        };

        var port = ReservePort();
        await using var listener = new TlsControlListener(agentIdentity, store, pairingCoordinator, port, options);
        await listener.InitializeAsync();
        listener.Start();
        using var cancellation = new CancellationTokenSource();
        var listenerTask = listener.RunAsync(cancellation.Token);
        try
        {
            using var client = new TcpClient(AddressFamily.InterNetwork);
            await client.ConnectAsync(IPAddress.Loopback, port);
            await using var tls = new SslStream(client.GetStream(), false, (_, certificate, _, _) =>
                certificate is not null && string.Equals(
                    Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData())),
                    agentIdentity.CertificateSha256Fingerprint,
                    StringComparison.Ordinal));
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            });
            using var reader = new StreamReader(tls, new UTF8Encoding(false), false, 1024 * 1024, leaveOpen: true);
            await using var writer = new StreamWriter(tls, new UTF8Encoding(false), 1024 * 1024, leaveOpen: true) { AutoFlush = true };

            var hello = await SendAsync<ControlHelloResponse>(writer, reader, new ControlHelloRequest(1));
            var challenge = await SendAsync<ControlSessionStartResponse>(writer, reader, new ControlSessionStartRequest(1, controllerId));
            var signature = ControlRequestAuthentication.SignSessionAuthentication(
                hello.DeviceId, controllerId, challenge.SessionId, challenge.Challenge, challenge.ExpiresAtUtc, controllerKey);
            await SendAsync<ControlSessionAuthenticateResponse>(writer, reader,
                new ControlSessionAuthenticateRequest(1, challenge.SessionId, controllerId, signature));

            var written = await SendAsync<FileWriteResponse>(writer, reader,
                new ControlFileWriteRequest(1, controllerId, new FileWriteRequest("notes/a.txt", Encoding.UTF8.GetBytes("hello"), false)));
            var read = await SendAsync<FileReadResponse>(writer, reader,
                new ControlFileReadRequest(1, controllerId, new FileReadRequest("notes/a.txt", 1, 3)));
            Assert.Equal(5, written.Entry.Length);
            Assert.Equal("ell", Encoding.UTF8.GetString(read.Chunk.Data));

            var data = Encoding.UTF8.GetBytes("abcdefgh");
            var manifest = new FileManifest("local", [new FileManifestEntry(string.Empty, data.Length, DateTimeOffset.UtcNow, Convert.ToHexString(SHA256.HashData(data)))]);
            var started = await SendAsync<TransferStartResponse>(writer, reader,
                new ControlTransferStartRequest(1, controllerId, new TransferStartRequest(TransferDirection.Upload, "local", "uploaded.bin", manifest, 4)));
            await SendAsync<TransferWriteChunkResponse>(writer, reader,
                new ControlTransferWriteChunkRequest(1, controllerId, new TransferWriteChunkRequest(
                    new FileChunk(started.Session.SessionId, string.Empty, 0, data[..4], false),
                    Convert.ToHexString(SHA256.HashData(data[..4])))));

            var persisted = await SendAsync<TransferStatusResponse>(writer, reader,
                new ControlTransferStatusRequest(1, controllerId, new TransferStatusRequest(started.Session.SessionId)));
            Assert.Single(persisted.Session.CompletedChunks);
            await SendAsync<TransferWriteChunkResponse>(writer, reader,
                new ControlTransferWriteChunkRequest(1, controllerId, new TransferWriteChunkRequest(
                    new FileChunk(started.Session.SessionId, string.Empty, 4, data[4..], true),
                    Convert.ToHexString(SHA256.HashData(data[4..])))));
            var completed = await SendAsync<TransferCompleteResponse>(writer, reader,
                new ControlTransferCompleteRequest(1, controllerId, new TransferCompleteRequest(started.Session.SessionId)));

            Assert.Equal(TransferSessionState.Completed, completed.Session.State);
            Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(fileRoot, "uploaded.bin")));
        }
        finally
        {
            cancellation.Cancel();
            await listenerTask;
        }
    }
    [Fact]
    public async Task PersistedPairingCooldownBlocksPairStartAndWritesAudit()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(directory.Path);
        await store.InitializeAsync();
        await store.RecordPairingFailureAsync(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5), 1, TimeSpan.FromMinutes(15));
        var certificateManager = new AgentCertificateManager(store);
        using var agentIdentity = await certificateManager.GetOrCreateAsync();
        using var pairingCoordinator = new PairingCoordinator(store, certificateManager);
        using var controllerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var controllerRequest = new CertificateRequest("CN=blocked-pair-controller", controllerKey, HashAlgorithmName.SHA256);
        using var controllerCertificate = controllerRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));

        var port = ReservePort();
        await using var listener = new TlsControlListener(agentIdentity, store, pairingCoordinator, port);
        await listener.InitializeAsync();
        listener.Start();
        using var cancellation = new CancellationTokenSource();
        var listenerTask = listener.RunAsync(cancellation.Token);
        try
        {
            using var client = new TcpClient(AddressFamily.InterNetwork);
            await client.ConnectAsync(IPAddress.Loopback, port);
            await using var tls = new SslStream(client.GetStream(), false, (_, certificate, _, _) => certificate is not null);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            });
            using var reader = new StreamReader(tls, new UTF8Encoding(false), false, 1024 * 1024, leaveOpen: true);
            await using var writer = new StreamWriter(tls, new UTF8Encoding(false), 1024 * 1024, leaveOpen: true) { AutoFlush = true };

            await writer.WriteLineAsync(JsonSerializer.Serialize(
                new ControlPairStartRequest(1, "blocked-controller", controllerCertificate.Export(X509ContentType.Cert)),
                ContractJson.Options));
            var line = await reader.ReadLineAsync();
            var envelope = JsonSerializer.Deserialize<ResultEnvelope<ControlPairStartResponse>>(line!, ContractJson.Options);

            Assert.NotNull(envelope);
            Assert.False(envelope!.Ok);
            Assert.Equal(ErrorCode.ResourceExhausted, envelope.Error!.Code);
            var audit = await store.ListAuditEventsAsync();
            Assert.Contains(audit, item => item.EventType == "pairing.blocked" && item.ErrorCode == ErrorCode.ResourceExhausted);
        }
        finally
        {
            cancellation.Cancel();
            await listenerTask;
        }
    }
    private static async Task<T> SendAsync<T>(StreamWriter writer, StreamReader reader, object request)
    {
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, ContractJson.Options));
        var line = await reader.ReadLineAsync();
        var envelope = JsonSerializer.Deserialize<ResultEnvelope<T>>(line!, ContractJson.Options);
        Assert.NotNull(envelope);
        Assert.True(envelope!.Ok, envelope.Error?.Message);
        return Assert.IsType<T>(envelope.Result);
    }

    private static int ReservePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
