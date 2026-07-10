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
using var identity = await new AgentCertificateManager(stateStore).GetOrCreateAsync(cancellation.Token);
await using var discoveryPublisher = new LanDiscoveryPublisher();

Console.WriteLine($"rc-agent {identity.DeviceId} is advertising LAN discovery on TCP port {tcpPort}.");
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

static int ReadTcpPort()
{
    var configured = Environment.GetEnvironmentVariable("RC_AGENT_TCP_PORT");
    return int.TryParse(configured, out var port) && port is >= 1 and <= 65535 ? port : 43001;
}
