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
        if (args.Length != 1)
        {
            await error.WriteLineAsync("Usage: rc-agent [identity|tls-diagnostics|repair-tls-identity|arm-pairing|pairing-code|unpair]");
            return 64;
        }

        if (string.Equals(args[0], "identity", StringComparison.OrdinalIgnoreCase))
        {
            if (!LocalAgentIdentityFile.TryRead(dataRoot, out var identity))
            {
                await error.WriteLineAsync("No local Agent identity is available. Ensure the Agent service has started successfully.");
                return 1;
            }

            await output.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(
                Result.Success(identity),
                ContractJson.Options));
            return 0;
        }

        if (string.Equals(args[0], "tls-diagnostics", StringComparison.OrdinalIgnoreCase))
        {
            var available = LocalTlsHandshakeDiagnosticsFile.TryRead(dataRoot, out var diagnostic);
            await output.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(
                Result.Success(new { Available = available, Diagnostic = diagnostic }),
                ContractJson.Options));
            return 0;
        }

        if (string.Equals(args[0], "repair-tls-identity", StringComparison.OrdinalIgnoreCase))
        {
            LocalTlsIdentityRepairRequest.Request(dataRoot);
            await output.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(
                Result.Success(new
                {
                    Scheduled = true,
                    Message = "Restart the RemoteControllerAgent service to regenerate its TLS identity. The service will refuse the repair if a controller is paired.",
                }),
                ContractJson.Options));
            return 0;
        }

        if (string.Equals(args[0], "arm-pairing", StringComparison.OrdinalIgnoreCase))
        {
            if (!LocalAgentIdentityFile.TryRead(dataRoot, out var identity))
            {
                await error.WriteLineAsync("No local Agent identity is available. Ensure the Agent service has started successfully.");
                return 1;
            }

            var localIdentity = identity!;
            await using var pairingStore = new AgentStateStore(dataRoot);
            await pairingStore.InitializeAsync(cancellationToken);
            if (await pairingStore.GetPairedControllerIdAsync(cancellationToken) is not null)
            {
                await error.WriteLineAsync("This Agent already has a paired controller. Run 'rc-agent unpair' locally before arming a new pairing code.");
                return 1;
            }

            if (LocalPairingCodeFile.TryReadCurrent(dataRoot, DateTimeOffset.UtcNow, out var existingPairingCode))
            {
                var currentPairingCode = existingPairingCode!;
                if (!currentPairingCode.IsArmed)
                {
                    await error.WriteLineAsync("A pairing attempt is already in progress. Wait for it to expire or complete before arming another code.");
                    return 1;
                }

                await output.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(
                    Result.Success(new
                    {
                        currentPairingCode.AgentDeviceId,
                        currentPairingCode.OneTimeCode,
                        currentPairingCode.ExpiresAtUtc,
                        localIdentity.CertificateSha256Fingerprint,
                    }),
                    ContractJson.Options));
                return 0;
            }

            var pairingCode = LocalPairingCodeFile.Arm(
                dataRoot,
                localIdentity,
                DateTimeOffset.UtcNow,
                TimeSpan.FromMinutes(10));
            await output.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(
                Result.Success(new
                {
                    pairingCode.AgentDeviceId,
                    pairingCode.OneTimeCode,
                    pairingCode.ExpiresAtUtc,
                    localIdentity.CertificateSha256Fingerprint,
                }),
                ContractJson.Options));
            return 0;
        }

        if (string.Equals(args[0], "pairing-code", StringComparison.OrdinalIgnoreCase))
        {
            if (!LocalPairingCodeFile.TryReadCurrent(dataRoot, DateTimeOffset.UtcNow, out var pairingCode))
            {
                await error.WriteLineAsync("No current local pairing code is available.");
                return 1;
            }

            await output.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(
                Result.Success(pairingCode),
                ContractJson.Options));
            return 0;
        }
        if (!string.Equals(args[0], "unpair", StringComparison.OrdinalIgnoreCase))
        {
            await error.WriteLineAsync("Usage: rc-agent [identity|tls-diagnostics|arm-pairing|pairing-code|unpair]");
            return 64;
        }

        await using var store = new AgentStateStore(dataRoot);
        await store.InitializeAsync(cancellationToken);
        var pairedControllerId = await store.GetPairedControllerIdAsync(cancellationToken);
        await store.RemovePairedControllerAsync(cancellationToken);
        await store.ResetPairingFailuresAsync(cancellationToken);
        await output.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(
            Result.Success(new
            {
                Unpaired = pairedControllerId is not null,
                ControllerId = pairedControllerId,
            }),
            ContractJson.Options));
        return 0;
    }
}
