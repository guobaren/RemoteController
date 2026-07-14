namespace Rc.Contracts;

public static class UiOperationKinds
{
    public const string Snapshot = "snapshot";
    public const string Displays = "displays";
    public const string Windows = "windows";
    public const string Screenshot = "screenshot";
    public const string WindowAction = "window_action";
    public const string MoveWindow = "move_window";
    public const string MouseMove = "mouse_move";
    public const string MouseButton = "mouse_button";
    public const string MouseWheel = "mouse_wheel";
    public const string Key = "key";
    public const string Shortcut = "shortcut";
    public const string Text = "text";
    public const string ClipboardRead = "clipboard_read";
    public const string ClipboardWrite = "clipboard_write";
    public const string AutomationTree = "automation_tree";
    public const string AutomationAction = "automation_action";

    public static bool IsSupported(string? operation) => operation is Snapshot or Displays or Windows or Screenshot or
        WindowAction or MoveWindow or MouseMove or MouseButton or MouseWheel or Key or Shortcut or Text or ClipboardRead or ClipboardWrite or
        AutomationTree or AutomationAction;
}

public sealed record UiAgentCommandRequest(int ProtocolVersion, string Operation, System.Text.Json.JsonElement Request);

public sealed record UiAgentCommandResponse(System.Text.Json.JsonElement Result);

public sealed class UiAgentRegistration
{
    public UiAgentRegistration(int protocolVersion, int capabilityVersion, string pipeName, UiSessionSnapshot session)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(1, protocolVersion);
        ArgumentOutOfRangeException.ThrowIfLessThan(capabilityVersion, 1);
        if (string.IsNullOrWhiteSpace(pipeName) || pipeName.Length > 128)
        {
            throw new ArgumentException("A bounded UI Agent pipe name is required.", nameof(pipeName));
        }

        ProtocolVersion = protocolVersion;
        CapabilityVersion = capabilityVersion;
        PipeName = pipeName;
        Session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public int ProtocolVersion { get; }
    public int CapabilityVersion { get; }
    public string PipeName { get; }
    public UiSessionSnapshot Session { get; }
}

public sealed record UiAgentRegistrationResponse(bool Accepted, string? Message = null);
