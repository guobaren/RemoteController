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

public sealed record UiSessionSnapshot(
    int SessionId,
    string? UserName,
    bool IsActive,
    IReadOnlyList<DisplaySnapshot> Displays,
    IReadOnlyList<WindowSnapshot> Windows);

public sealed record UiSnapshotRequest(bool IncludeWindows);

public sealed record UiSnapshotResponse(UiSessionSnapshot Session);
