using System.Runtime.InteropServices;
using Rc.Contracts;

namespace Rc.UiAgent;

public static class DesktopWindowController
{
    public static bool IsForeground(long handle) => NativeMethods.GetForegroundWindow() == new IntPtr(handle);

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
                ActivateWindow(window);
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

    private static void ActivateWindow(IntPtr window)
    {
        _ = NativeMethods.ShowWindow(window, NativeMethods.ShowRestore);
        var currentThread = NativeMethods.GetCurrentThreadId();
        var targetThread = NativeMethods.GetWindowThreadProcessId(window, out _);
        var attached = targetThread != 0 && targetThread != currentThread && NativeMethods.AttachThreadInput(currentThread, targetThread, true);
        try
        {
            _ = NativeMethods.BringWindowToTop(window);
            if (!NativeMethods.SetForegroundWindow(window))
            {
                throw new InvalidOperationException("The requested window could not be activated.");
            }
            _ = NativeMethods.SetFocus(window);
            for (var attempt = 0; attempt < 10; attempt++)
            {
                if (NativeMethods.GetForegroundWindow() == window)
                {
                    return;
                }
                Thread.Sleep(20);
            }
            throw new InvalidOperationException("The requested window did not become the foreground window.");
        }
        finally
        {
            if (attached)
            {
                _ = NativeMethods.AttachThreadInput(currentThread, targetThread, false);
            }
        }
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

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool BringWindowToTop(IntPtr window);

        [DllImport("user32.dll")]
        public static extern IntPtr SetFocus(IntPtr window);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool attach);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    }
}
