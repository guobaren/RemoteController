using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Rc.Contracts;

namespace Rc.UiAgent;

public static class DesktopSnapshotProvider
{
    public const int CapabilityVersion = 2;

    public static UiSessionSnapshot Capture(bool includeWindows)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("UI automation is only supported on Windows.");
        }

        var processId = Environment.ProcessId;
        if (!NativeMethods.ProcessIdToSessionId((uint)processId, out var sessionId))
        {
            Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
        }

        var displays = GetDisplays();
        var windows = includeWindows ? GetWindows() : [];
        return new UiSessionSnapshot((int)sessionId, Environment.UserName, true, displays, windows);
    }

    public static WindowSnapshot GetWindowSnapshot(long handle)
    {
        var window = new IntPtr(handle);
        if (handle == 0 || !NativeMethods.IsWindow(window) || !NativeMethods.GetWindowRect(window, out var bounds))
        {
            throw new InvalidOperationException("The requested window is no longer available.");
        }

        if (NativeMethods.GetWindowThreadProcessId(window, out var processId) == 0)
        {
            throw new InvalidOperationException("The requested window process is no longer available.");
        }

        return new WindowSnapshot(
            handle,
            GetWindowTitle(window),
            GetProcessName((int)processId),
            (int)processId,
            bounds.Left,
            bounds.Top,
            bounds.Right - bounds.Left,
            bounds.Bottom - bounds.Top,
            NativeMethods.IsWindowVisible(window));
    }

    public static bool IsInCurrentSession(long handle)
    {
        var window = new IntPtr(handle);
        if (handle == 0 || NativeMethods.GetWindowThreadProcessId(window, out var processId) == 0 ||
            !NativeMethods.ProcessIdToSessionId(processId, out var windowSession) ||
            !NativeMethods.ProcessIdToSessionId((uint)Environment.ProcessId, out var currentSession))
        {
            return false;
        }

        return windowSession == currentSession;
    }

    private static List<DisplaySnapshot> GetDisplays()
    {
        var displays = new List<DisplaySnapshot>();
        var index = 0;
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr deviceContext, ref NativeMethods.Rect monitorBounds, IntPtr data) =>
        {
            var monitorInfo = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
            if (!NativeMethods.GetMonitorInfo(monitor, ref monitorInfo))
            {
                return true;
            }

            var bounds = monitorInfo.Monitor;
            displays.Add(new DisplaySnapshot(
                index++,
                monitorInfo.DeviceName,
                bounds.Left,
                bounds.Top,
                bounds.Right - bounds.Left,
                bounds.Bottom - bounds.Top,
                (monitorInfo.Flags & NativeMethods.MonitorInfoPrimary) != 0));
            return true;
        }, IntPtr.Zero);
        return displays;
    }

    private static List<WindowSnapshot> GetWindows()
    {
        var windows = new List<WindowSnapshot>();
        NativeMethods.EnumWindows((handle, _) =>
        {
            if (!NativeMethods.IsWindowVisible(handle))
            {
                return true;
            }

            try
            {
                windows.Add(GetWindowSnapshot(handle.ToInt64()));
            }
            catch (InvalidOperationException)
            {
            }
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private static string GetWindowTitle(IntPtr handle)
    {
        var length = NativeMethods.GetWindowTextLength(handle);
        if (length == 0)
        {
            return string.Empty;
        }

        var buffer = new char[length + 1];
        var copied = NativeMethods.GetWindowText(handle, buffer, buffer.Length);
        return new string(buffer, 0, copied);
    }

    private static string GetProcessName(int processId)
    {
        try
        {
            return Process.GetProcessById(processId).ProcessName;
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }

    private static class NativeMethods
    {
        public const int MonitorInfoPrimary = 1;

        public delegate bool MonitorEnumProc(IntPtr monitor, IntPtr deviceContext, ref Rect bounds, IntPtr data);
        public delegate bool EnumWindowsProc(IntPtr window, IntPtr data);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumDisplayMonitors(IntPtr deviceContext, IntPtr clip, MonitorEnumProc callback, IntPtr data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr data);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindow(IntPtr window);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr window, out Rect bounds);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextLength(IntPtr window);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr window, [Out] char[] buffer, int maximumCount);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MonitorInfo
        {
            public int Size;
            public Rect Monitor;
            public Rect Work;
            public int Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
        }
    }
}
