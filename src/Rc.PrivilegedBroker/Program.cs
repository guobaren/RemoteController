using Rc.PrivilegedBroker;
using Rc.WindowsService;

const string ServiceName = "RemoteControllerBroker";
var serviceMode = args.Length == 1 && string.Equals(args[0], "--service", StringComparison.OrdinalIgnoreCase);
if (args.Any(argument => string.Equals(argument, "--service", StringComparison.OrdinalIgnoreCase)) && !serviceMode)
{
    Console.Error.WriteLine("The --service option cannot be combined with other arguments.");
    return 64;
}

static async Task RunBrokerAsync(CancellationToken cancellationToken)
{
    var options = BrokerOptions.FromEnvironment();
    if (!BrokerProcessSecurity.IsElevated() && !options.AllowUnelevatedForTesting)
    {
        throw new InvalidOperationException("Rc.PrivilegedBroker must run with an elevated administrator token.");
    }

    var secret = await BrokerSecretStore.LoadOrCreateAsync(options.SecretPath, options.ClientSid, cancellationToken);
    await using var server = new PrivilegedBrokerServer(options, secret);
    Console.WriteLine($"Privileged broker listening on local pipe '{options.PipeName}'.");
    try
    {
        await server.RunAsync(cancellationToken);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
    }
}

if (serviceMode)
{
    return WindowsServiceHost.Run(ServiceName, RunBrokerAsync);
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};
await RunBrokerAsync(cancellation.Token);
return 0;
