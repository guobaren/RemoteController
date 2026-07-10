using Rc.Cli.Commands;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: rcctl discover [--timeout-ms <1-60000>] [--text]");
    return 2;
}

return args[0] switch
{
    "discover" => await DiscoverCommand.RunAsync(args[1..], Console.Out, Console.Error),
    _ => await WriteUsageAndReturnAsync(args[0]),
};

static Task<int> WriteUsageAndReturnAsync(string command)
{
    Console.Error.WriteLine($"Unknown command: {command}. Usage: rcctl discover [--timeout-ms <1-60000>] [--text]");
    return Task.FromResult(2);
}
