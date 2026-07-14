using System.Runtime.InteropServices;
using System.Windows.Forms;
using Rc.Contracts;

namespace Rc.UiAgent;

public static class DesktopInputController
{
    public static UiSessionSnapshot MoveMouse(UiMouseMoveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ActivateWindowTarget(request.Target);
        var point = ResolvePoint(request.Target, request.X, request.Y);
        SendMouse(point.X, point.Y, NativeMethods.MouseMove | NativeMethods.MouseAbsolute | NativeMethods.MouseVirtualDesk, 0);
        return DesktopSnapshotProvider.Capture(includeWindows: false);
    }

    public static UiSessionSnapshot SetMouseButton(UiMouseButtonRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _ = ResolveCurrentPointer(request.Target);
        var flags = request.Button switch
        {
            MouseButton.Left => request.IsDown ? NativeMethods.LeftDown : NativeMethods.LeftUp,
            MouseButton.Middle => request.IsDown ? NativeMethods.MiddleDown : NativeMethods.MiddleUp,
            MouseButton.Right => request.IsDown ? NativeMethods.RightDown : NativeMethods.RightUp,
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };
        // MOUSEEVENTF_ABSOLUTE only applies to a move. Supplying it with a pure
        // button transition can be accepted by SendInput without delivering the
        // button message to the control under the cursor. The current pointer is
        // validated above; send just the physical button transition.
        SendMouseButton(flags);
        return DesktopSnapshotProvider.Capture(includeWindows: false);
    }

    public static UiSessionSnapshot ScrollMouse(UiMouseWheelRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var point = ResolveCurrentPointer(request.Target);
        // Keep the wheel event on the explicitly validated pointer position. This
        // also avoids relying on an implicit cursor position in the interactive session.
        SendMouse(point.X, point.Y, NativeMethods.MouseMove | NativeMethods.MouseWheel | NativeMethods.MouseAbsolute | NativeMethods.MouseVirtualDesk, request.Delta);
        return DesktopSnapshotProvider.Capture(includeWindows: false);
    }

    public static UiSessionSnapshot SetKeys(UiKeyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureTargetAvailable(request.Target);
        var inputs = request.Keys.Select(key => CreateKeyInput(key, request.IsDown)).ToArray();
        SendInputs(inputs);
        return DesktopSnapshotProvider.Capture(includeWindows: false);
    }

    public static UiSessionSnapshot TypeText(UiTextRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureTargetAvailable(request.Target);
        if (string.IsNullOrEmpty(request.Text))
        {
            throw new ArgumentException("Text input cannot be empty.", nameof(request));
        }

        var inputs = new List<NativeMethods.Input>(request.Text.Length * 2);
        foreach (var character in request.Text)
        {
            inputs.Add(CreateUnicodeInput(character, keyUp: false));
            inputs.Add(CreateUnicodeInput(character, keyUp: true));
        }
        SendInputs(inputs);
        return DesktopSnapshotProvider.Capture(includeWindows: false);
    }

    public static UiSessionSnapshot SendShortcut(UiShortcutRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ActivateWindowTarget(request.Target);
        EnsureTargetAvailable(request.Target);
        SendShortcutToForegroundWindow(request.Keys);
        return DesktopSnapshotProvider.Capture(includeWindows: false);
    }

    private static void SendShortcutToForegroundWindow(IReadOnlyList<string> keys)
    {
        var modifiers = string.Concat(keys.Where(IsModifierKey).Select(ToSendKeysModifier));
        var nonModifiers = keys.Where(key => !IsModifierKey(key)).ToArray();
        var sequence = modifiers + string.Concat(nonModifiers.Select(ToSendKeysKey));
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { SendKeys.SendWait(sequence); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException("The shortcut could not be delivered to the foreground window.", failure);
        }
    }

    private static bool IsModifierKey(string key) => key.Equals("Control", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("ControlKey", StringComparison.OrdinalIgnoreCase) || key.Equals("Shift", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("ShiftKey", StringComparison.OrdinalIgnoreCase) || key.Equals("Alt", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("Menu", StringComparison.OrdinalIgnoreCase);

    private static string ToSendKeysModifier(string key) => key.Equals("Shift", StringComparison.OrdinalIgnoreCase) || key.Equals("ShiftKey", StringComparison.OrdinalIgnoreCase) ? "+" :
        key.Equals("Alt", StringComparison.OrdinalIgnoreCase) || key.Equals("Menu", StringComparison.OrdinalIgnoreCase) ? "%" : "^";

    private static string ToSendKeysKey(string key) => key.Length == 1 && char.IsLetterOrDigit(key[0]) ? key.ToLowerInvariant() : $"{{{key.ToUpperInvariant()}}}";

    public static void NavigateBrowser(WindowTarget target, string url)
    {
        EnsureTargetAvailable(target);
        _ = DesktopWindowController.Act(target.WindowHandle, WindowAction.Activate);
        SendInputs([CreateKeyInput("Control", false), CreateKeyInput("L", false), CreateKeyInput("L", true), CreateKeyInput("Control", true)]);
        var characters = url.SelectMany(character => new[] { CreateUnicodeInput(character, false), CreateUnicodeInput(character, true) }).ToArray();
        SendInputs(characters);
        SendInputs([CreateKeyInput("Enter", false), CreateKeyInput("Enter", true)]);
    }

    private static (int X, int Y) ResolvePoint(UiTarget target, int x, int y)
    {
        EnsureTargetAvailable(target);
        var (originX, originY, width, height) = target switch
        {
            DisplayTarget display => DesktopSnapshotProvider.Capture(includeWindows: false).Displays
                .Where(item => item.Index == display.DisplayIndex)
                .Select(item => (item.X, item.Y, item.Width, item.Height))
                .SingleOrDefault(),
            WindowTarget window => ToBounds(DesktopSnapshotProvider.GetWindowSnapshot(window.WindowHandle)),
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };
        if (width <= 0 || height <= 0 || x < 0 || y < 0 || x >= width || y >= height)
        {
            throw new ArgumentOutOfRangeException(nameof(x), "Input coordinates must be within the explicit target bounds.");
        }
        return (originX + x, originY + y);
    }

    private static (int X, int Y) ResolveCurrentPointer(UiTarget target)
    {
        EnsureTargetAvailable(target);
        var (originX, originY, width, height) = target switch
        {
            DisplayTarget display => DesktopSnapshotProvider.Capture(includeWindows: false).Displays
                .Where(item => item.Index == display.DisplayIndex)
                .Select(item => (item.X, item.Y, item.Width, item.Height))
                .SingleOrDefault(),
            WindowTarget window => ToBounds(DesktopSnapshotProvider.GetWindowSnapshot(window.WindowHandle)),
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };
        var pointer = Cursor.Position;
        if (width <= 0 || height <= 0 || pointer.X < originX || pointer.Y < originY || pointer.X >= originX + width || pointer.Y >= originY + height)
        {
            throw new InvalidOperationException("Move the mouse within the explicit target before sending a mouse button or wheel event.");
        }
        return (pointer.X, pointer.Y);
    }

    private static (int X, int Y, int Width, int Height) ToBounds(WindowSnapshot window) => (window.X, window.Y, window.Width, window.Height);

    // Pointer movement intentionally remains independent from button transitions.
    // A button command targeting a window brings that window to the foreground so
    // SendInput is delivered to the requested interactive window, even when a
    // separate move command was issued earlier.
    private static void ActivateWindowTarget(UiTarget target)
    {
        if (target is WindowTarget window && !DesktopWindowController.IsForeground(window.WindowHandle))
        {
            _ = DesktopWindowController.Act(window.WindowHandle, WindowAction.Activate);
        }
    }

    private static void EnsureTargetAvailable(UiTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        switch (target)
        {
            case DisplayTarget display when DesktopSnapshotProvider.Capture(includeWindows: false).Displays.Any(item => item.Index == display.DisplayIndex):
                return;
            case WindowTarget window when DesktopSnapshotProvider.IsInCurrentSession(window.WindowHandle):
                _ = DesktopSnapshotProvider.GetWindowSnapshot(window.WindowHandle);
                return;
            default:
                throw new InvalidOperationException("The requested UI target is unavailable in this active UI session.");
        }
    }

    private static NativeMethods.Input CreateKeyInput(string key, bool keyUp)
    {
        if (!Enum.TryParse<Keys>(key, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
        {
            throw new ArgumentException($"'{key}' is not a supported virtual key.", nameof(key));
        }
        var flags = keyUp ? NativeMethods.KeyUp : 0;
        var keyboard = new NativeMethods.KeyboardInput { Flags = flags };
        if (TryGetModifierScanCode(key, out var scanCode))
        {
            keyboard.ScanCode = scanCode;
            keyboard.Flags |= NativeMethods.KeyScanCode;
        }
        else
        {
            keyboard.VirtualKey = (ushort)parsed;
        }
        return new NativeMethods.Input
        {
            Type = NativeMethods.InputKeyboard,
            Union = new NativeMethods.InputUnion
            {
                Keyboard = keyboard,
            },
        };
    }

    private static bool TryGetModifierScanCode(string key, out ushort scanCode)
    {
        scanCode = key.Trim().ToUpperInvariant() switch
        {
            "CONTROL" or "CONTROLKEY" => 0x1D,
            "SHIFT" or "SHIFTKEY" => 0x2A,
            "ALT" or "MENU" => 0x38,
            _ => 0,
        };
        return scanCode != 0;
    }

    private static NativeMethods.Input CreateUnicodeInput(char character, bool keyUp) => new()
    {
        Type = NativeMethods.InputKeyboard,
        Union = new NativeMethods.InputUnion
        {
            Keyboard = new NativeMethods.KeyboardInput
            {
                ScanCode = character,
                Flags = NativeMethods.KeyUnicode | (keyUp ? NativeMethods.KeyUp : 0),
            },
        },
    };

    private static void SendMouse(int x, int y, uint flags, int wheelData)
    {
        var left = NativeMethods.GetSystemMetrics(NativeMethods.VirtualScreenLeft);
        var top = NativeMethods.GetSystemMetrics(NativeMethods.VirtualScreenTop);
        var width = NativeMethods.GetSystemMetrics(NativeMethods.VirtualScreenWidth);
        var height = NativeMethods.GetSystemMetrics(NativeMethods.VirtualScreenHeight);
        if (width <= 1 || height <= 1)
        {
            throw new InvalidOperationException("The virtual desktop has invalid bounds.");
        }
        var input = new NativeMethods.Input
        {
            Type = NativeMethods.InputMouse,
            Union = new NativeMethods.InputUnion
            {
                Mouse = new NativeMethods.MouseInput
                {
                    X = (int)Math.Round((x - left) * 65535d / (width - 1)),
                    Y = (int)Math.Round((y - top) * 65535d / (height - 1)),
                    MouseData = unchecked((uint)wheelData),
                    Flags = flags,
                },
            },
        };
        SendInputs([input]);
    }

    private static void SendMouseButton(uint flags) => SendInputs([
        new NativeMethods.Input
        {
            Type = NativeMethods.InputMouse,
            Union = new NativeMethods.InputUnion
            {
                Mouse = new NativeMethods.MouseInput { Flags = flags },
            },
        },
    ]);

    private static void SendInputs(IReadOnlyList<NativeMethods.Input> inputs)
    {
        var sent = NativeMethods.SendInput((uint)inputs.Count, [.. inputs], Marshal.SizeOf<NativeMethods.Input>());
        if (sent != inputs.Count)
        {
            throw new InvalidOperationException("Windows rejected one or more input events.");
        }
    }

    private static class NativeMethods
    {
        public const uint InputMouse = 0;
        public const uint InputKeyboard = 1;
        public const uint MouseMove = 0x0001;
        public const uint LeftDown = 0x0002;
        public const uint LeftUp = 0x0004;
        public const uint RightDown = 0x0008;
        public const uint RightUp = 0x0010;
        public const uint MiddleDown = 0x0020;
        public const uint MiddleUp = 0x0040;
        public const uint MouseWheel = 0x0800;
        public const uint MouseAbsolute = 0x8000;
        public const uint MouseVirtualDesk = 0x4000;
        public const uint KeyUp = 0x0002;
        public const uint KeyUnicode = 0x0004;
        public const uint KeyScanCode = 0x0008;
        public const int VirtualScreenLeft = 76;
        public const int VirtualScreenTop = 77;
        public const int VirtualScreenWidth = 78;
        public const int VirtualScreenHeight = 79;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int index);

        [StructLayout(LayoutKind.Sequential)]
        public struct Input
        {
            public uint Type;
            public InputUnion Union;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion
        {
            [FieldOffset(0)]
            public MouseInput Mouse;
            [FieldOffset(0)]
            public KeyboardInput Keyboard;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MouseInput
        {
            public int X;
            public int Y;
            public uint MouseData;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KeyboardInput
        {
            public ushort VirtualKey;
            public ushort ScanCode;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }
    }
}
