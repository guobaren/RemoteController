using Rc.PrivilegedBroker;

var options = BrokerOptions.FromEnvironment();
if (!BrokerProcessSecurity.IsElevated() && !options.AllowUnelevatedForTesting)
{
    Console.Error.WriteLine("Rc.PrivilegedBroker must run with an elevated administrator token.");
    return 5;
}

var secret = await BrokerSecretStore.LoadOrCreateAsync(options.SecretPath);
await using var server = new PrivilegedBrokerServer(options, secret);
Console.WriteLine($"Privileged broker listening on local pipe '{options.PipeName}'.");
await server.RunAsync();
return 0;
