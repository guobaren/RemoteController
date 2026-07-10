using System.Net;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using Rc.Cli.Security;
using Rc.Contracts;

namespace Rc.Cli.Commands;

public static class ProbeCommand
{
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length is < 3 or > 4 || !IPEndPoint.TryParse(args[0], out var endpoint))
        {
            await error.WriteLineAsync("Usage: rcctl probe <IP:port> --fingerprint <SHA256> [--text]");
            return 2;
        }

        var text = args.Contains("--text", StringComparer.Ordinal);
        var fingerprintIndex = Array.FindIndex(args, value => string.Equals(value, "--fingerprint", StringComparison.Ordinal));
        if (fingerprintIndex < 0 || fingerprintIndex + 1 >= args.Length || (text && args.Length != 4) || (!text && args.Length != 3))
        {
            await error.WriteLineAsync("Usage: rcctl probe <IP:port> --fingerprint <SHA256> [--text]");
            return 2;
        }

        var expectedFingerprint = NormalizeFingerprint(args[fingerprintIndex + 1]);
        if (expectedFingerprint is null)
        {
            await error.WriteLineAsync("The certificate fingerprint must be a 64-character SHA-256 hexadecimal value.");
            return 2;
        }

        try
        {
            await using var connection = await PinnedTlsConnection.ConnectAsync(endpoint, expectedFingerprint);
            var tls = connection.Stream;

            await using var writer = new StreamWriter(tls, new UTF8Encoding(false), 16 * 1024, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(tls, new UTF8Encoding(false), false, 16 * 1024, leaveOpen: true);
            await writer.WriteLineAsync(JsonSerializer.Serialize(new ControlHelloRequest(1), ContractJson.Options));
            var line = await reader.ReadLineAsync();
            var response = line is null
                ? null
                : JsonSerializer.Deserialize<ResultEnvelope<ControlHelloResponse>>(line, ContractJson.Options);
            if (response is not { Ok: true, Result: not null })
            {
                await error.WriteLineAsync("The agent did not return a valid hello response.");
                return 1;
            }

            if (text)
            {
                await output.WriteLineAsync($"deviceId: {response.Result.DeviceId}");
                await output.WriteLineAsync($"fingerprint: {response.Result.CertificateSha256Fingerprint}");
                await output.WriteLineAsync($"paired: {response.Result.HasPairedController}");
            }
            else
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(Result.Success(response.Result), ContractJson.Options));
            }

            return 0;
        }
        catch (AuthenticationException exception)
        {
            await error.WriteLineAsync($"TLS authentication failed: {exception.Message}");
            return 1;
        }
        catch (System.Net.Sockets.SocketException exception)
        {
            await error.WriteLineAsync($"Unable to connect: {exception.Message}");
            return 1;
        }
    }

    private static string? NormalizeFingerprint(string value)
    {
        var normalized = value.Replace(":", string.Empty, StringComparison.Ordinal).Trim();
        return normalized.Length == 64 && normalized.All(Uri.IsHexDigit)
            ? normalized.ToUpperInvariant()
            : null;
    }
}
