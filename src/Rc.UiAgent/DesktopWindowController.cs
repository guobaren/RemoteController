using System.Runtime.InteropServices;
using Rc.Contracts;

namespace Rc.UiAgent;

public static class DesktopWindowController
{
    public static WindowSnapshot Act(long handle, WindowAction action)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("UI automation is only supported on Windows.");
        }

        if (!DesktopSnapshotProvider.IsInCurrentSession(handle))
        {
            throw new InvalidOperationException("The requested window is unavailable in this active UI session.");
        }

        var window = new IntPtr(handle);
        switch (action)
        {
            case WindowAction.Activate:
                _ = NativeMethods.ShowWindow(window, NativeMethods.ShowRestore);
                _ = NativeMethods.SetForegroundWindow(window);
                break;
            case WindowAction.Minimize:
                _ = NativeMethods.ShowWindow(window, NativeMethods.ShowMinimize);
                break;
            case WindowAction.Maximize:
                _ = NativeMethods.ShowWindow(window, NativeMethods.ShowMaximize);
                break;
            case WindowAction.Restore:
                _ = NativeMethods.ShowWindow(window, NativeMethods.ShowRestore);
                break;
            case WindowAction.Close:
                if (!NativeMethods.PostMessage(window, NativeMethods.WindowClose, IntPtr.Zero, IntPtr.Zero))
                {
                    throw new InvalidOperationException("The requested window could not be closed.");
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action));
        }

        return DesktopSnapshotProvider.GetWindowSnapshot(handle);
    }

    public static WindowSnapshot Move(long handle, int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Window dimensions must be positive.");
        }
        if (!DesktopSnapshotProvider.IsInCurrentSession(handle))
        {
            throw new InvalidOperationException("The requested window is unavailable in this active UI session.");
        }
        if (!NativeMethods.SetWindowPos(new IntPtr(handle), IntPtr.Zero, x, y, width, height, NativeMethods.NoZOrder | NativeMethods.NoActivate))
        {
            throw new InvalidOperationException("The requested window could not be moved.");
        }
        return DesktopSnapshotProvider.GetWindowSnapshot(handle);
    }

    private static class NativeMethods
    {
        public const int ShowMaximize = 3;
        public const int ShowMinimize = 6;
        public const int ShowRestore = 9;
        public const uint WindowClose = 0x0010;
        public const uint NoZOrder = 0x0004;
        public const uint NoActivate = 0x0010;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    }
}
