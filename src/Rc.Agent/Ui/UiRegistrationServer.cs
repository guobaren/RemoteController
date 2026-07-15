using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Rc.Contracts;

namespace Rc.Agent.Ui;

public sealed class UiRegistrationServer
{
    private const int MaximumRequestCharacters = 1024 * 1024;
    private readonly string pipeName;
    private readonly string? clientSid;
    private readonly UiSessionRegistry registry;

    public UiRegistrationServer(string pipeName, UiSessionRegistry registry, string? clientSid = null)
    {
        if (string.IsNullOrWhiteSpace(pipeName))
        {
            throw new ArgumentException("A UI registration pipe name is required.", nameof(pipeName));
        }
        this.pipeName = pipeName;
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.clientSid = clientSid;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var server = CreatePipeServer();
            try
            {
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                await HandleAsync(server, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private NamedPipeServerStream CreatePipeServer()
    {
        if (string.IsNullOrWhiteSpace(clientSid))
        {
            return new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        }

        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(
            WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("The Agent account SID is unavailable."),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(clientSid),
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 0, 0, security);
    }

    private async Task HandleAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(server, new UTF8Encoding(false), false, MaximumRequestCharacters, leaveOpen: true);
        await using var writer = new StreamWriter(server, new UTF8Encoding(false), MaximumRequestCharacters, leaveOpen: true) { AutoFlush = true };
        try
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null || line.Length > MaximumRequestCharacters)
            {
                throw new InvalidDataException("The UiAgent registration was empty or too large.");
            }
            var registration = JsonSerializer.Deserialize<UiAgentRegistration>(line, ContractJson.Options)
                ?? throw new InvalidDataException("The UiAgent registration was invalid.");
            registry.Register(registration);
            await writer.WriteLineAsync(JsonSerializer.Serialize(Result.Success(new UiAgentRegistrationResponse(true)), ContractJson.Options)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(Result.Failure<UiAgentRegistrationResponse>(
                new RemoteError(ErrorCode.InvalidRequest, exception.Message, false)), ContractJson.Options)).ConfigureAwait(false);
        }
    }
}
