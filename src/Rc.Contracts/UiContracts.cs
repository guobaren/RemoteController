using System.Text.Json.Serialization;

namespace Rc.Contracts;

public sealed record DisplaySnapshot(
    int Index,
    string DeviceName,
    int X,
    int Y,
    int Width,
    int Height,
    bool IsPrimary);

public sealed record WindowSnapshot(
    long Handle,
    string Title,
    string ProcessName,
    int ProcessId,
    int X,
    int Y,
    int Width,
    int Height,
    bool IsVisible);

public sealed class UiSessionSnapshot
{
    public UiSessionSnapshot(
        int sessionId,
        string? userName,
        bool isActive,
        IReadOnlyList<DisplaySnapshot> displays,
        IReadOnlyList<WindowSnapshot> windows)
    {
        ArgumentNullException.ThrowIfNull(displays);
        ArgumentNullException.ThrowIfNull(windows);
        SessionId = sessionId;
        UserName = userName;
        IsActive = isActive;
        Displays = Array.AsReadOnly(displays.ToArray());
        Windows = Array.AsReadOnly(windows.ToArray());
    }

    public int SessionId { get; }

    public string? UserName { get; }

    public bool IsActive { get; }

    public IReadOnlyList<DisplaySnapshot> Displays { get; }

    public IReadOnlyList<WindowSnapshot> Windows { get; }
}

public sealed record UiSnapshotRequest(bool IncludeWindows);

public sealed record UiSnapshotResponse(UiSessionSnapshot Session);

public sealed record UiStatusRequest();

public sealed record UiStatusResponse(UiSessionSnapshot Session);

public sealed record UiDisplaysRequest();

public sealed class UiDisplaysResponse
{
    public UiDisplaysResponse(IReadOnlyList<DisplaySnapshot> displays)
    {
        ArgumentNullException.ThrowIfNull(displays);
        Displays = Array.AsReadOnly(displays.ToArray());
    }

    public IReadOnlyList<DisplaySnapshot> Displays { get; }
}

public sealed record UiWindowsRequest();

public sealed class UiWindowsResponse
{
    public UiWindowsResponse(IReadOnlyList<WindowSnapshot> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        Windows = Array.AsReadOnly(windows.ToArray());
    }

    public IReadOnlyList<WindowSnapshot> Windows { get; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(DisplayTarget), "display")]
[JsonDerivedType(typeof(WindowTarget), "window")]
public abstract class UiTarget;

public sealed class DisplayTarget : UiTarget
{
    public DisplayTarget(int displayIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(displayIndex);
        DisplayIndex = displayIndex;
    }

    [JsonPropertyName("displayIndex")]
    public int DisplayIndex { get; }
}

public sealed class WindowTarget : UiTarget
{
    public WindowTarget(long windowHandle)
    {
        ArgumentOutOfRangeException.ThrowIfZero(windowHandle);
        WindowHandle = windowHandle;
    }

    [JsonPropertyName("windowHandle")]
    public long WindowHandle { get; }
}

public sealed class UiScreenshotRequest
{
    public UiScreenshotRequest(UiTarget target)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
    }

    public UiTarget Target { get; }
}

public sealed class UiScreenshotResponse
{
    private readonly byte[] pngBytes;

    public UiScreenshotResponse(byte[] pngBytes)
    {
        ArgumentNullException.ThrowIfNull(pngBytes);
        this.pngBytes = pngBytes.ToArray();
    }

    public byte[] PngBytes => pngBytes.ToArray();
}

public enum WindowAction
{
    Activate,
    Minimize,
    Maximize,
    Restore,
    Close,
}

public sealed record UiWindowActionRequest(WindowTarget Target, WindowAction Action);

public sealed record UiWindowActionResponse(WindowSnapshot Window);

public sealed record UiMoveWindowRequest(WindowTarget Target, int X, int Y, int Width, int Height);

public sealed record UiMoveWindowResponse(WindowSnapshot Window);

public sealed record UiMouseMoveRequest(UiTarget Target, int X, int Y);

public enum MouseButton
{
    Left,
    Middle,
    Right,
}

public sealed record UiMouseButtonRequest(UiTarget Target, MouseButton Button, bool IsDown);

public sealed record UiMouseWheelRequest(UiTarget Target, int Delta);

public sealed class UiKeyRequest
{
    public UiKeyRequest(UiTarget target, IReadOnlyList<string> keys, bool isDown)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0 || keys.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("At least one non-empty key is required.", nameof(keys));
        }

        Target = target;
        Keys = Array.AsReadOnly(keys.ToArray());
        IsDown = isDown;
    }

    public UiTarget Target { get; }

    public IReadOnlyList<string> Keys { get; }

    public bool IsDown { get; }
}

public sealed record UiTextRequest(UiTarget Target, string Text);

public sealed class UiClipboardWriteRequest
{
    private readonly byte[] data;

    public UiClipboardWriteRequest(byte[] data, string format = "text/plain")
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        this.data = data.ToArray();
        Format = format;
    }

    public byte[] Data => data.ToArray();

    public string Format { get; }
}

public sealed record UiClipboardWriteResponse();

public sealed record UiClipboardReadRequest(string Format = "text/plain");

public sealed class UiClipboardReadResponse
{
    private readonly byte[] data;

    public UiClipboardReadResponse(byte[] data, string format)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        this.data = data.ToArray();
        Format = format;
    }

    public byte[] Data => data.ToArray();

    public string Format { get; }
}
