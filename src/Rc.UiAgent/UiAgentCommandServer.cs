using System.IO.Pipes;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Rc.Contracts;

namespace Rc.UiAgent;

public sealed class UiAgentCommandServer
{
    private const int MaximumMessageCharacters = 1024 * 1024;
    private readonly string pipeName;
    private readonly string? clientSid;

    public UiAgentCommandServer(string pipeName, string? clientSid = null)
    {
        if (string.IsNullOrWhiteSpace(pipeName))
        {
            throw new ArgumentException("A UI Agent control pipe name is required.", nameof(pipeName));
        }
        this.pipeName = pipeName;
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
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The UiAgent account SID is unavailable."), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(clientSid), PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }

    private static async Task HandleAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(server, new UTF8Encoding(false), false, MaximumMessageCharacters, leaveOpen: true);
        await using var writer = new StreamWriter(server, new UTF8Encoding(false), MaximumMessageCharacters, leaveOpen: true) { AutoFlush = true };
        try
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null || line.Length > MaximumMessageCharacters)
            {
                throw new InvalidDataException("The UI command was empty or too large.");
            }

            var request = JsonSerializer.Deserialize<UiAgentCommandRequest>(line, ContractJson.Options);
            if (request is null || request.ProtocolVersion != 1 || !UiOperationKinds.IsSupported(request.Operation))
            {
                throw new ArgumentException("The UI command is invalid.");
            }

            var result = UiAgentCommandDispatcher.Execute(request);
            await writer.WriteLineAsync(JsonSerializer.Serialize(Result.Success(new UiAgentCommandResponse(result)), ContractJson.Options)).ConfigureAwait(false);
        }
        catch (PlatformNotSupportedException exception)
        {
            await WriteFailureAsync(writer, ErrorCode.Unavailable, exception.Message, retryable: false).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or InvalidOperationException or JsonException)
        {
            await WriteFailureAsync(writer, ErrorCode.InvalidRequest, exception.Message, retryable: false).ConfigureAwait(false);
        }
    }

    private static Task WriteFailureAsync(StreamWriter writer, ErrorCode code, string message, bool retryable) =>
        writer.WriteLineAsync(JsonSerializer.Serialize(Result.Failure<object>(new RemoteError(code, message, retryable)), ContractJson.Options));
}

internal static class UiAgentCommandDispatcher
{
    public static JsonElement Execute(UiAgentCommandRequest command)
    {
        object result = command.Operation switch
        {
            UiOperationKinds.Snapshot => Handle<UiSnapshotRequest, UiSnapshotResponse>(command, request => new UiSnapshotResponse(DesktopSnapshotProvider.Capture(request.IncludeWindows))),
            UiOperationKinds.Displays => Handle<UiDisplaysRequest, UiDisplaysResponse>(command, _ => new UiDisplaysResponse(DesktopSnapshotProvider.Capture(false).Displays)),
            UiOperationKinds.Windows => Handle<UiWindowsRequest, UiWindowsResponse>(command, _ => new UiWindowsResponse(DesktopSnapshotProvider.Capture(true).Windows)),
            UiOperationKinds.Screenshot => Handle<UiScreenshotRequest, UiScreenshotResponse>(command, request => DesktopScreenshotProvider.Capture(request.Target)),
            UiOperationKinds.WindowAction => Handle<UiWindowActionRequest, UiWindowActionResponse>(command, request => new UiWindowActionResponse(DesktopWindowController.Act(request.Target.WindowHandle, request.Action))),
            UiOperationKinds.MoveWindow => Handle<UiMoveWindowRequest, UiMoveWindowResponse>(command, request => new UiMoveWindowResponse(DesktopWindowController.Move(request.Target.WindowHandle, request.X, request.Y, request.Width, request.Height))),
            UiOperationKinds.MouseMove => Handle<UiMouseMoveRequest, UiSnapshotResponse>(command, request => new UiSnapshotResponse(DesktopInputController.MoveMouse(request))),
            UiOperationKinds.MouseButton => Handle<UiMouseButtonRequest, UiSnapshotResponse>(command, request => new UiSnapshotResponse(DesktopInputController.SetMouseButton(request))),
            UiOperationKinds.MouseWheel => Handle<UiMouseWheelRequest, UiSnapshotResponse>(command, request => new UiSnapshotResponse(DesktopInputController.ScrollMouse(request))),
            UiOperationKinds.Key => Handle<UiKeyRequest, UiSnapshotResponse>(command, request => new UiSnapshotResponse(DesktopInputController.SetKeys(request))),
            UiOperationKinds.Shortcut => Handle<UiShortcutRequest, UiSnapshotResponse>(command, request => new UiSnapshotResponse(DesktopInputController.SendShortcut(request))),
            UiOperationKinds.Text => Handle<UiTextRequest, UiSnapshotResponse>(command, request => new UiSnapshotResponse(DesktopInputController.TypeText(request))),
            UiOperationKinds.ClipboardRead => Handle<UiClipboardReadRequest, UiClipboardReadResponse>(command, DesktopClipboardController.Read),
            UiOperationKinds.ClipboardWrite => Handle<UiClipboardWriteRequest, UiClipboardWriteResponse>(command, DesktopClipboardController.Write),
            UiOperationKinds.AutomationTree => Handle<UiAutomationTreeRequest, UiAutomationTreeResponse>(command, UiAutomationController.GetTree),
            UiOperationKinds.AutomationAction => Handle<UiAutomationActionRequest, UiAutomationActionResponse>(command, UiAutomationController.Act),
            _ => throw new ArgumentException("The UI operation is not supported.", nameof(command)),
        };
        return JsonSerializer.SerializeToElement(result, ContractJson.Options);
    }

    private static TResponse Handle<TRequest, TResponse>(UiAgentCommandRequest command, Func<TRequest, TResponse> action)
    {
        var request = command.Request.Deserialize<TRequest>(ContractJson.Options)
            ?? throw new ArgumentException("The UI operation request is invalid.", nameof(command));
        return action(request);
    }
}
