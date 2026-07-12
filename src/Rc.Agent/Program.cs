using Rc.Agent.Control;
using Rc.Agent.Discovery;
using Rc.Agent.Persistence;
using Rc.Agent.Security;
using Rc.Contracts;
using Rc.WindowsService;

const string ServiceName = "RemoteControllerAgent";
var serviceMode = args.Length == 1 && string.Equals(args[0], "--service", StringComparison.OrdinalIgnoreCase);
if (args.Any(argument => string.Equals(argument, "--service", StringComparison.OrdinalIgnoreCase)) && !serviceMode)
{
    Console.Error.WriteLine("The --service option cannot be combined with other commands.");
    return 64;
}

if (serviceMode)
{
    return WindowsServiceHost.Run(ServiceName, RunAgentAsync);
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

var dataRoot = ResolveDataRoot();
var localAdminResult = await LocalAdminCommand.TryRunAsync(args, dataRoot, Console.Out, Console.Error, cancellation.Token);
if (localAdminResult is { } exitCode)
{
    return exitCode;
}

await RunAgentAsync(cancellation.Token);
return 0;

static async Task RunAgentAsync(CancellationToken cancellationToken)
{
    var dataRoot = ResolveDataRoot();
    var displayName = Environment.GetEnvironmentVariable("RC_AGENT_DISPLAY_NAME") ?? Environment.MachineName;
    var tcpPort = ReadTcpPort();

    await using var stateStore = new AgentStateStore(dataRoot);
    await stateStore.InitializeAsync(cancellationToken);
    var certificateManager = new AgentCertificateManager(stateStore);
    using var identity = await certificateManager.GetOrCreateAsync(cancellationToken);
    using var pairingCoordinator = new PairingCoordinator(stateStore, certificateManager);
    await using var discoveryPublisher = new LanDiscoveryPublisher();
    await using var controlListener = new TlsControlListener(identity, stateStore, pairingCoordinator, tcpPort);
    await controlListener.InitializeAsync(cancellationToken);
    controlListener.Start();
    var controlListenerTask = controlListener.RunAsync(cancellationToken);

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
            cancellationToken);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
    }
    finally
    {
        try
        {
            await controlListenerTask;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}

static string ResolveDataRoot() => Environment.GetEnvironmentVariable("RC_AGENT_DATA_ROOT")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RemoteController");

static int ReadTcpPort()
{
    var configured = Environment.GetEnvironmentVariable("RC_AGENT_TCP_PORT");
    return int.TryParse(configured, out var port) && port is >= 1 and <= 65535 ? port : 43001;
}
