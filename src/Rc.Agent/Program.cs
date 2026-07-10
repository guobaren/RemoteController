using Rc.Agent.Control;
using Rc.Agent.Discovery;
using Rc.Agent.Persistence;
using Rc.Agent.Security;
using Rc.Contracts;

var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

var dataRoot = Environment.GetEnvironmentVariable("RC_AGENT_DATA_ROOT")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RemoteController");
var displayName = Environment.GetEnvironmentVariable("RC_AGENT_DISPLAY_NAME") ?? Environment.MachineName;
var tcpPort = ReadTcpPort();

await using var stateStore = new AgentStateStore(dataRoot);
await stateStore.InitializeAsync(cancellation.Token);
var certificateManager = new AgentCertificateManager(stateStore);
using var identity = await certificateManager.GetOrCreateAsync(cancellation.Token);
using var pairingCoordinator = new PairingCoordinator(stateStore, certificateManager);
await using var discoveryPublisher = new LanDiscoveryPublisher();
await using var controlListener = new TlsControlListener(identity, stateStore, pairingCoordinator, tcpPort);
controlListener.Start();
var controlListenerTask = controlListener.RunAsync(cancellation.Token);

Console.WriteLine($"rc-agent {identity.DeviceId} is advertising LAN discovery and TLS control on TCP port {tcpPort}.");
Console.WriteLine($"TLS certificate SHA-256: {identity.CertificateSha256Fingerprint}");
try
{
    await discoveryPublisher.RunAsync(
        () => new DiscoveryAnnouncement(
            identity.DeviceId,
            displayName,
            tcpPort,
            DiscoveryAnnouncement.CurrentProtocolVersion,
            identity.CertificateSha256Fingerprint),
        cancellation.Token);
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
}
finally
{
    cancellation.Cancel();
    await controlListenerTask;
}

static int ReadTcpPort()
{
    var configured = Environment.GetEnvironmentVariable("RC_AGENT_TCP_PORT");
    return int.TryParse(configured, out var port) && port is >= 1 and <= 65535 ? port : 43001;
}