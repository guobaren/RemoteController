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
