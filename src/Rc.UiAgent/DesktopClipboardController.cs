using System.Text;
using System.Windows.Forms;
using Rc.Contracts;

namespace Rc.UiAgent;

public static class DesktopClipboardController
{
    private const string TextFormat = "text/plain";

    public static UiClipboardReadResponse Read(UiClipboardReadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureTextFormat(request.Format);
        return StaThreadDispatcher.Run(() => new UiClipboardReadResponse(Encoding.UTF8.GetBytes(Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty), TextFormat));
    }

    public static UiClipboardWriteResponse Write(UiClipboardWriteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureTextFormat(request.Format);
        return StaThreadDispatcher.Run(() =>
        {
            Clipboard.SetText(Encoding.UTF8.GetString(request.Data));
            return new UiClipboardWriteResponse();
        });
    }

    private static void EnsureTextFormat(string format)
    {
        if (!string.Equals(format, TextFormat, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Only text/plain clipboard content is supported.", nameof(format));
        }
    }

}
