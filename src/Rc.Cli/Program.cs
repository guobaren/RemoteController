using Rc.Cli.Commands;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: rcctl discover [--timeout-ms <1-60000>] [--text] | rcctl probe <IP:port> --fingerprint <SHA256> [--text] | rcctl pair <IP:port> --fingerprint <SHA256> [--name <controller-name>] [--text] | rcctl exec <IP:port> --fingerprint <SHA256> --command <command> [--shell powershell|cmd] [--workdir <path>] [--text] | rcctl job start|status|list ...");
    return 2;
}

return args[0] switch
{
    "discover" => await DiscoverCommand.RunAsync(args[1..], Console.Out, Console.Error),
    "probe" => await ProbeCommand.RunAsync(args[1..], Console.Out, Console.Error),
    "pair" => await PairCommand.RunAsync(args[1..], Console.In, Console.Out, Console.Error),
    "exec" => await ExecCommand.RunAsync(args[1..], Console.Out, Console.Error),
    "job" => await JobCommand.RunAsync(args[1..], Console.Out, Console.Error),
    _ => await WriteUsageAndReturnAsync(args[0]),
};

static Task<int> WriteUsageAndReturnAsync(string command)
{
    Console.Error.WriteLine($"Unknown command: {command}. Usage: rcctl discover [--timeout-ms <1-60000>] [--text] | rcctl probe <IP:port> --fingerprint <SHA256> [--text] | rcctl pair <IP:port> --fingerprint <SHA256> [--name <controller-name>] [--text] | rcctl exec <IP:port> --fingerprint <SHA256> --command <command> [--shell powershell|cmd] [--workdir <path>] [--text] | rcctl job start|status|list ...");
    return Task.FromResult(2);
}