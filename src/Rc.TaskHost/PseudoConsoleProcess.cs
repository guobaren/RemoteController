using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Rc.Contracts;

namespace Rc.TaskHost;

internal sealed class PseudoConsoleProcess : IDisposable
{
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ProcThreadAttributePseudoConsole = 0x00020016;
    private const uint HandleFlagInherit = 0x00000001;
    private const int StartfUseStdHandles = 0x00000100;
    private IntPtr pseudoConsole;
    private bool disposed;

    private PseudoConsoleProcess(Process process, IntPtr pseudoConsole, FileStream input, FileStream output)
    {
        Process = process;
        this.pseudoConsole = pseudoConsole;
        Input = input;
        Output = output;
    }
    public Process Process { get; }

    public FileStream Input { get; }

    public FileStream Output { get; }

    public static PseudoConsoleProcess Start(ExecRequest execution, TerminalOptions terminal, IReadOnlyDictionary<string, string>? hostEnvironment)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(terminal);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            throw new PlatformNotSupportedException("ConPTY requires Windows 10 version 1809 or later.");
        }

        var security = new SecurityAttributes
        {
            Length = Marshal.SizeOf<SecurityAttributes>(),
            InheritHandle = true,
        };
        if (!CreatePipe(out var inputRead, out var inputWrite, ref security, 0) ||
            !CreatePipe(out var outputRead, out var outputWrite, ref security, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to create ConPTY pipes.");
        }

        IntPtr hpc = IntPtr.Zero;
        IntPtr attributeList = IntPtr.Zero;
        try
        {
            if (!SetHandleInformation(inputWrite, HandleFlagInherit, 0) ||
                !SetHandleInformation(outputRead, HandleFlagInherit, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to protect ConPTY parent pipe handles from inheritance.");
            }

            var result = CreatePseudoConsole(
                new Coord(checked((short)terminal.Columns), checked((short)terminal.Rows)),
                inputRead,
                outputWrite,
                0,
                out hpc);
            if (result != 0)
            {
                Marshal.ThrowExceptionForHR(result);
            }

            nuint attributeBytes = 0;
            _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeBytes);
            attributeList = Marshal.AllocHGlobal(checked((int)attributeBytes));
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeBytes))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to initialize the ConPTY process attribute list.");
            }

            if (!UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    (nuint)ProcThreadAttributePseudoConsole,
                    hpc,
                    (nuint)IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to attach the ConPTY process attribute.");
            }

            var startup = new StartupInfoEx();
            startup.StartupInfo.Size = Marshal.SizeOf<StartupInfoEx>();
            startup.StartupInfo.Flags = StartfUseStdHandles;
            startup.AttributeList = attributeList;
            var commandLine = (BuildCommandLine(execution) + "\0").ToCharArray();
            var environment = BuildEnvironmentBlock(execution.Environment, hostEnvironment);
            var environmentHandle = environment is null ? IntPtr.Zero : Marshal.StringToHGlobalUni(environment);
            try
            {
                if (!CreateProcessW(
                        null,
                        commandLine,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        false,
                        ExtendedStartupInfoPresent | CreateUnicodeEnvironment,
                        environmentHandle,
                        string.IsNullOrWhiteSpace(execution.WorkingDirectory) ? null : execution.WorkingDirectory,
                        ref startup,
                        out var processInformation))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to start the ConPTY child process.");
                }

                try
                {
                    var process = Process.GetProcessById(unchecked((int)processInformation.ProcessId));
                    process.EnableRaisingEvents = true;
                    var input = new FileStream(inputWrite, FileAccess.Write, 16 * 1024, isAsync: false);
                    inputWrite = null!;
                    var output = new FileStream(outputRead, FileAccess.Read, 16 * 1024, isAsync: false);
                    outputRead = null!;
                    return new PseudoConsoleProcess(process, hpc, input, output);                }
                finally
                {
                    CloseHandle(processInformation.Thread);
                    CloseHandle(processInformation.Process);
                }
            }
            finally
            {
                if (environmentHandle != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(environmentHandle);
                }
            }
        }
        catch
        {
            if (hpc != IntPtr.Zero)
            {
                ClosePseudoConsole(hpc);
            }
            throw;
        }
        finally
        {
            inputRead?.Dispose();
            inputWrite?.Dispose();
            outputRead?.Dispose();
            outputWrite?.Dispose();
            if (attributeList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }
        }
    }

    public void Resize(int columns, int rows)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var result = ResizePseudoConsole(pseudoConsole, new Coord(checked((short)columns), checked((short)rows)));
        if (result != 0)
        {
            Marshal.ThrowExceptionForHR(result);
        }
    }

    public void CloseConsole()
    {
        if (pseudoConsole == IntPtr.Zero)
        {
            return;
        }
        ClosePseudoConsole(pseudoConsole);
        pseudoConsole = IntPtr.Zero;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        Input.Dispose();
        CloseConsole();
        Output.Dispose();
        Process.Dispose();
    }

    private static string BuildCommandLine(ExecRequest execution)
    {
        IReadOnlyList<string> arguments;
        if (execution.DirectArgv is { } direct)
        {
            arguments = direct;
        }
        else
        {
            var shell = execution.Shell!;
            arguments = shell.Kind switch
            {
                ShellKind.PowerShell => ["powershell.exe", "-NoLogo", "-NoProfile", "-Command", shell.Command],
                ShellKind.Cmd => [Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", "/d", "/s", "/c", shell.Command],
                _ => throw new ArgumentOutOfRangeException(nameof(execution)),
            };
        }
        return string.Join(' ', arguments.Select(QuoteWindowsArgument));
    }

    private static string QuoteWindowsArgument(string argument)
    {
        if (argument.Length > 0 && !argument.Any(char.IsWhiteSpace) && !argument.Contains('"'))
        {
            return argument;
        }
        var result = new StringBuilder(argument.Length + 2).Append('"');
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }
            if (character == '"')
            {
                result.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }
            result.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }
        result.Append('\\', backslashes * 2).Append('"');
        return result.ToString();
    }

    private static string? BuildEnvironmentBlock(
        IReadOnlyDictionary<string, string>? executionEnvironment,
        IReadOnlyDictionary<string, string>? hostEnvironment)
    {
        if (executionEnvironment is null && hostEnvironment is null)
        {
            return null;
        }
        var environment = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        Apply(environment, executionEnvironment);
        Apply(environment, hostEnvironment);
        return string.Concat(environment.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase).Select(item => $"{item.Key}={item.Value}\0")) + "\0";
    }

    private static void Apply(Dictionary<string, string> destination, IReadOnlyDictionary<string, string>? source)
    {
        if (source is null) return;
        foreach (var item in source) destination[item.Key] = item.Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Coord(short x, short y)
    {
        public readonly short X = x;
        public readonly short Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2Bytes;
        public IntPtr Reserved2;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe, ref SecurityAttributes pipeAttributes, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);

    [DllImport("kernel32.dll")]
    private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr pseudoConsole);

    [DllImport("kernel32.dll")]
    private static extern int ResizePseudoConsole(IntPtr pseudoConsole, Coord size);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr pseudoConsole);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr attributeList, int attributeCount, int flags, ref nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr attributeList, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previousValue, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string? applicationName,
        [In, Out] char[] commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
