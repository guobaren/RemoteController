using Rc.Agent.Persistence;
using Rc.Contracts;

namespace Rc.Agent.Security;

public static class LocalAdminCommand
{
    public static async Task<int?> TryRunAsync(
        string[] args,
        string dataRoot,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (args.Length == 0)
        {
            return null;
        }
        if (!string.Equals(args[0], "unpair", StringComparison.OrdinalIgnoreCase))
        {
            await error.WriteLineAsync("Usage: rc-agent [unpair]");
            return 64;
        }
        if (args.Length != 1)
        {
            await error.WriteLineAsync("The local unpair command does not accept additional arguments.");
            return 64;
        }

        await using var store = new AgentStateStore(dataRoot);
        await store.InitializeAsync(cancellationToken);
        var paired = await store.GetPairedControllerAsync(cancellationToken);
        await store.RemovePairedControllerAsync(cancellationToken);
        await store.ResetPairingFailuresAsync(cancellationToken);
        await output.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(
            Result.Success(new
            {
                Unpaired = paired is not null,
                ControllerId = paired?.ControllerId,
            }),
            ContractJson.Options));
        return 0;
    }
}
