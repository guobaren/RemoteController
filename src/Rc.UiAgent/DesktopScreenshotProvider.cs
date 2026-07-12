using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Rc.Contracts;

namespace Rc.UiAgent;

public static class DesktopScreenshotProvider
{
    public static UiScreenshotResponse Capture(UiTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("UI automation is only supported on Windows.");
        }

        return target switch
        {
            DisplayTarget display => CaptureDisplay(display),
            WindowTarget window => CaptureWindow(window),
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };
    }

    private static UiScreenshotResponse CaptureDisplay(DisplayTarget target)
    {
        var display = DesktopSnapshotProvider.Capture(includeWindows: false).Displays
            .SingleOrDefault(item => item.Index == target.DisplayIndex)
            ?? throw new InvalidOperationException("The requested display is no longer available.");
        return Encode(display.Width, display.Height, graphics =>
            graphics.CopyFromScreen(display.X, display.Y, 0, 0, new Size(display.Width, display.Height), CopyPixelOperation.SourceCopy));
    }

    private static UiScreenshotResponse CaptureWindow(WindowTarget target)
    {
        if (!DesktopSnapshotProvider.IsInCurrentSession(target.WindowHandle))
        {
            throw new InvalidOperationException("The requested window is unavailable in this active UI session.");
        }

        var window = DesktopSnapshotProvider.GetWindowSnapshot(target.WindowHandle);
        if (window.Width <= 0 || window.Height <= 0)
        {
            throw new InvalidOperationException("The requested window has no capturable bounds.");
        }

        return Encode(window.Width, window.Height, graphics =>
        {
            var deviceContext = graphics.GetHdc();
            try
            {
                if (!NativeMethods.PrintWindow(new IntPtr(target.WindowHandle), deviceContext, NativeMethods.PrintWindowFullContent))
                {
                    graphics.CopyFromScreen(window.X, window.Y, 0, 0, new Size(window.Width, window.Height), CopyPixelOperation.SourceCopy);
                }
            }
            finally
            {
                graphics.ReleaseHdc(deviceContext);
            }
        });
    }

    private static UiScreenshotResponse Encode(int width, int height, Action<Graphics> draw)
    {
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("The requested capture target has invalid bounds.");
        }

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            draw(graphics);
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return new UiScreenshotResponse(stream.ToArray());
    }

    private static class NativeMethods
    {
        public const uint PrintWindowFullContent = 0x00000002;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PrintWindow(IntPtr window, IntPtr deviceContext, uint flags);
    }
}
