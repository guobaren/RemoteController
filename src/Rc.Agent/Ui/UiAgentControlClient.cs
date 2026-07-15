using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Rc.Contracts;

namespace Rc.Agent.Ui;

public static class UiAgentControlClient
{
    private const int MaximumMessageCharacters = 1024 * 1024;

    public static async Task<JsonElement> SendAsync(UiAgentRegistration registration, string operation, JsonElement request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (!UiOperationKinds.IsSupported(operation))
        {
            throw new ArgumentException("The UI operation is not supported.", nameof(operation));
        }

        await using var client = new NamedPipeClientStream(".", registration.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await client.ConnectAsync(5_000, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or UnauthorizedAccessException)
        {
            throw new UiAgentUnavailableException("The active UiAgent cannot be reached.", exception);
        }

        await using var writer = new StreamWriter(client, new UTF8Encoding(false), MaximumMessageCharacters, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(client, new UTF8Encoding(false), false, MaximumMessageCharacters, leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(new UiAgentCommandRequest(1, operation, request), ContractJson.Options)).ConfigureAwait(false);
        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        var response = line is null ? null : JsonSerializer.Deserialize<ResultEnvelope<UiAgentCommandResponse>>(line, ContractJson.Options);
        if (response is not { Ok: true, Result: not null })
        {
            throw new UiAgentRequestException(response?.Error ?? new RemoteError(ErrorCode.Unavailable, "The UiAgent closed the control pipe.", true));
        }

        return response.Result.Result.Clone();
    }
}

public sealed class UiAgentUnavailableException : IOException
{
    public UiAgentUnavailableException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class UiAgentRequestException : Exception
{
    public UiAgentRequestException(RemoteError error) : base(error.Message) => Error = error;
    public RemoteError Error { get; }
}
