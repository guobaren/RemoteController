using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Rc.WindowsService;

public static class WindowsServiceHost
{
    private const uint ServiceWin32OwnProcess = 0x00000010;
    private const uint ServiceAcceptStop = 0x00000001;
    private const uint ServiceAcceptShutdown = 0x00000004;
    private const uint ServiceAcceptPreshutdown = 0x00000100;
    private const uint ServiceControlStop = 0x00000001;
    private const uint ServiceControlInterrogate = 0x00000004;
    private const uint ServiceControlShutdown = 0x00000005;
    private const uint ServiceControlPreshutdown = 0x0000000F;
    private const int ErrorFailedServiceControllerConnect = 1063;
    private const int ErrorServiceSpecificError = 1066;

    public static int Run(string serviceName, Func<CancellationToken, Task> runAsync)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentNullException.ThrowIfNull(runAsync);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows services are only supported on Windows.");
        }

        using var lifetime = new NativeLifetime(serviceName, runAsync);
        return lifetime.Run();
    }

    private sealed class NativeLifetime : IDisposable
    {
        private readonly string serviceName;
        private readonly Func<CancellationToken, Task> runAsync;
        private readonly CancellationTokenSource stopping = new();
        private readonly ServiceMainDelegate serviceMain;
        private readonly HandlerDelegate handler;
        private IntPtr statusHandle;
        private ServiceStatus status;
        private int checkpoint;
        private int exitCode;

        public NativeLifetime(string serviceName, Func<CancellationToken, Task> runAsync)
        {
            this.serviceName = serviceName;
            this.runAsync = runAsync;
            serviceMain = ServiceMain;
            handler = Handler;
            status = CreateStatus(ServiceState.StartPending);
        }

        public int Run()
        {
            var table = new[]
            {
                new ServiceTableEntry { ServiceName = serviceName, ServiceProcedure = serviceMain },
                new ServiceTableEntry(),
            };
            if (!StartServiceCtrlDispatcher(table))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == ErrorFailedServiceControllerConnect)
                {
                    throw new InvalidOperationException("The --service option must be started by the Windows Service Control Manager.");
                }
                throw new Win32Exception(error, "Could not connect to the Windows Service Control Manager.");
            }
            return exitCode;
        }

        private void ServiceMain(int argumentCount, IntPtr arguments)
        {
            statusHandle = RegisterServiceCtrlHandlerEx(serviceName, handler, IntPtr.Zero);
            if (statusHandle == IntPtr.Zero)
            {
                exitCode = Marshal.GetLastWin32Error();
                return;
            }

            SetStatus(ServiceState.StartPending, waitHintMilliseconds: 30_000);
            try
            {
                SetStatus(ServiceState.Running);
                runAsync(stopping.Token).GetAwaiter().GetResult();
                SetStatus(ServiceState.Stopped);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                SetStatus(ServiceState.Stopped);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                exitCode = 1;
                SetStatus(ServiceState.Stopped, win32ExitCode: ErrorServiceSpecificError, serviceSpecificExitCode: 1);
            }
        }

        private uint Handler(uint control, uint eventType, IntPtr eventData, IntPtr context)
        {
            switch (control)
            {
                case ServiceControlStop:
                case ServiceControlShutdown:
                case ServiceControlPreshutdown:
                    SetStatus(ServiceState.StopPending, waitHintMilliseconds: 30_000);
                    stopping.Cancel();
                    break;
                case ServiceControlInterrogate:
                    PublishStatus();
                    break;
            }
            return 0;
        }

        private void SetStatus(
            ServiceState state,
            uint waitHintMilliseconds = 0,
            uint win32ExitCode = 0,
            uint serviceSpecificExitCode = 0)
        {
            status.CurrentState = state;
            status.ControlsAccepted = state == ServiceState.Running
                ? ServiceAcceptStop | ServiceAcceptShutdown | ServiceAcceptPreshutdown
                : 0;
            status.Win32ExitCode = win32ExitCode;
            status.ServiceSpecificExitCode = serviceSpecificExitCode;
            status.WaitHint = waitHintMilliseconds;
            status.CheckPoint = state is ServiceState.StartPending or ServiceState.StopPending
                ? checked((uint)Interlocked.Increment(ref checkpoint))
                : 0;
            PublishStatus();
        }

        private void PublishStatus()
        {
            if (statusHandle != IntPtr.Zero && !SetServiceStatus(statusHandle, ref status))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not report Windows service status.");
            }
        }

        public void Dispose() => stopping.Dispose();

        private static ServiceStatus CreateStatus(ServiceState state) => new()
        {
            ServiceType = ServiceWin32OwnProcess,
            CurrentState = state,
        };
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ServiceTableEntry
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? ServiceName;
        public ServiceMainDelegate? ServiceProcedure;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public ServiceState CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    private enum ServiceState : uint
    {
        Stopped = 1,
        StartPending = 2,
        StopPending = 3,
        Running = 4,
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void ServiceMainDelegate(int argumentCount, IntPtr arguments);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint HandlerDelegate(uint control, uint eventType, IntPtr eventData, IntPtr context);

    [DllImport("advapi32.dll", EntryPoint = "StartServiceCtrlDispatcherW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceCtrlDispatcher([In] ServiceTableEntry[] serviceTable);

    [DllImport("advapi32.dll", EntryPoint = "RegisterServiceCtrlHandlerExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr RegisterServiceCtrlHandlerEx(string serviceName, HandlerDelegate handler, IntPtr context);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetServiceStatus(IntPtr statusHandle, ref ServiceStatus serviceStatus);
}
