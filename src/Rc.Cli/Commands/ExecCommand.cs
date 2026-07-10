using System.Net;
using System.Net.Sockets;
using System.Globalization;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rc.Cli.Security;
using Rc.Contracts;

namespace Rc.Cli.Commands;

public static class ExecCommand
{
    private const int MaximumLineLength = 1024 * 1024;

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        if (!TryParseArguments(args, out var endpoint, out var fingerprint, out var execution, out var text, out var argumentError))
        {
            await error.WriteLineAsync(argumentError);
            return 2;
        }

        try
        {
            var hello = await SendAsync<ControlHelloResponse>(endpoint!, fingerprint!, new ControlHelloRequest(1));
            if (!hello.HasPairedController)
            {
                await error.WriteLineAsync("This agent has no paired controller. Run rcctl pair first.");
                return 1;
            }

            using var identity = await ControllerIdentity.LoadOrCreateAsync(Environment.MachineName);
            using var privateKey = identity.GetPrivateKey();
            var signature = ControlRequestAuthentication.SignExecuteOnce(
                hello.DeviceId,
                identity.ControllerId,
                execution!,
                privateKey);
            try
            {
                var request = new ControlExecuteOnceRequest(1, identity.ControllerId, execution!, signature);
                var response = await SendAsync<ControlExecuteOnceResponse>(endpoint!, fingerprint!, request);
                if (text)
                {
                    await output.WriteAsync(Encoding.UTF8.GetString(response.StandardOutput));
                    await error.WriteAsync(Encoding.UTF8.GetString(response.StandardError));
                    await error.WriteLineAsync($"[rcctl] jobId={response.Job.JobId} state={response.Job.State} exitCode={response.Job.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}");
                    if (response.StandardOutputTruncated || response.StandardErrorTruncated)
                    {
                        await error.WriteLineAsync("[rcctl] Output was truncated to 256 KiB per stream. The complete captured output remains on the agent.");
                    }
                }
                else
                {
                    await output.WriteLineAsync(JsonSerializer.Serialize(Result.Success(response), ContractJson.Options));
                }

                return response.Job.ExitCode ?? 1;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(signature);
            }
        }
        catch (AuthenticationException exception)
        {
            await error.WriteLineAsync($"TLS authentication failed: {exception.Message}");
            return 1;
        }
        catch (SocketException exception)
        {
            await error.WriteLineAsync($"Unable to connect: {exception.Message}");
            return 1;
        }
        catch (InvalidOperationException exception)
        {
            await error.WriteLineAsync($"Command was rejected: {exception.Message}");
            return 1;
        }
        catch (CryptographicException exception)
        {
            await error.WriteLineAsync($"Controller identity failed: {exception.Message}");
            return 1;
        }
    }

    private static async Task<TResponse> SendAsync<TResponse>(IPEndPoint endpoint, string fingerprint, object request)
    {
        await using var connection = await PinnedTlsConnection.ConnectAsync(endpoint, fingerprint);
        var tls = connection.Stream;
        await using var writer = new StreamWriter(tls, new UTF8Encoding(false), MaximumLineLength, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(tls, new UTF8Encoding(false), false, MaximumLineLength, leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, ContractJson.Options));
        var line = await reader.ReadLineAsync();
        var response = line is null
            ? null
            : JsonSerializer.Deserialize<ResultEnvelope<TResponse>>(line, ContractJson.Options);
        if (response is not { Ok: true, Result: not null })
        {
            throw new InvalidOperationException(response?.Error?.Message ?? "The agent did not return a valid response.");
        }

        return response.Result;
    }

    private static bool TryParseArguments(
        string[] args,
        out IPEndPoint? endpoint,
        out string? fingerprint,
        out ExecRequest? execution,
        out bool text,
        out string? error)
    {
        endpoint = null;
        fingerprint = null;
        execution = null;
        text = false;
        error = null;
        if (args.Length == 0 || !IPEndPoint.TryParse(args[0], out endpoint))
        {
            error = Usage();
            return false;
        }

        var shell = ShellKind.PowerShell;
        string? command = null;
        string? workingDirectory = null;
        for (var index = 1; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--fingerprint" when index + 1 < args.Length:
                    fingerprint = NormalizeFingerprint(args[++index]);
                    break;
                case "--shell" when index + 1 < args.Length:
                    if (!Enum.TryParse<ShellKind>(args[++index], ignoreCase: true, out shell))
                    {
                        error = "--shell must be powershell or cmd.";
                        return false;
                    }
                    break;
                case "--command" when index + 1 < args.Length:
                    command = args[++index];
                    break;
                case "--workdir" when index + 1 < args.Length:
                    workingDirectory = args[++index];
                    break;
                case "--text":
                    text = true;
                    break;
                default:
                    error = Usage();
                    return false;
            }
        }

        if (fingerprint is null)
        {
            error = "A SHA-256 TLS fingerprint is required for exec.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(command))
        {
            error = "--command is required.";
            return false;
        }

        try
        {
            execution = ExecRequest.ForShell(shell, command, workingDirectory);
            return true;
        }
        catch (ArgumentException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static string Usage() =>
        "Usage: rcctl exec <IP:port> --fingerprint <SHA256> --command <command> [--shell powershell|cmd] [--workdir <path>] [--text]";

    private static string? NormalizeFingerprint(string value)
    {
        var normalized = value.Replace(":", string.Empty, StringComparison.Ordinal).Trim();
        return normalized.Length == 64 && normalized.All(Uri.IsHexDigit)
            ? normalized.ToUpperInvariant()
            : null;
    }
}