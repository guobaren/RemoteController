using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text.Json;
using Rc.Contracts;

namespace Rc.UiAgent;

internal sealed class ChromiumDevToolsClient
{
    private static readonly HttpClient HttpClient = new(new HttpClientHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(10),
    };
    private readonly int port;

    private ChromiumDevToolsClient(int port)
    {
        this.port = port;
    }

    public static ChromiumDevToolsClient WaitForProfile(string userDataDirectory, TimeSpan timeout)
    {
        var activePortFile = Path.Combine(userDataDirectory, "DevToolsActivePort");
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var portLine = File.ReadLines(activePortFile).FirstOrDefault();
                if (int.TryParse(portLine, out var port) && port is > 0 and <= 65535)
                {
                    return new ChromiumDevToolsClient(port);
                }
            }
            catch (IOException)
            {
            }
            Thread.Sleep(100);
        }
        throw new InvalidOperationException("The browser did not open its local DevTools endpoint within ten seconds.");
    }

    public void Navigate(string targetId, string url)
    {
        _ = SendCommand(targetId, "Page.navigate", new { url });
    }

    public UiAutomationElementSnapshot GetDocument(string targetId, long windowHandle, int maximumDepth, int maximumElements)
    {
        var response = SendCommand(targetId, "DOM.getDocument", new { depth = maximumDepth, pierce = true });
        if (!response.TryGetProperty("root", out var root))
        {
            throw new InvalidOperationException("Chromium returned an invalid DOM response.");
        }
        return ChromiumDevToolsDocument.CreateSnapshot(root, windowHandle, maximumDepth, maximumElements);
    }

    public string FindPageTarget(string expectedUrl)
    {
        var targets = GetTargets();
        var page = targets.FirstOrDefault(target => target.Type == "page" && UrlMatches(target.Url, expectedUrl)) ??
            targets.SingleOrDefault(target => target.Type == "page");
        if (page is null)
        {
            throw new InvalidOperationException("The browser has not created a debuggable page yet.");
        }
        return page.Id;
    }

    private JsonElement SendCommand(string targetId, string method, object parameters)
    {
        var target = GetTargets().SingleOrDefault(candidate => candidate.Id == targetId)
            ?? throw new InvalidOperationException("The browser page is no longer available for DevTools control.");
        var endpoint = ValidateWebSocketEndpoint(target.WebSocketDebuggerUrl);
        using var socket = new ClientWebSocket();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        socket.ConnectAsync(endpoint, cancellation.Token).GetAwaiter().GetResult();
        var command = JsonSerializer.SerializeToUtf8Bytes(new { id = 1, method, @params = parameters });
        socket.SendAsync(command, WebSocketMessageType.Text, true, cancellation.Token).GetAwaiter().GetResult();

        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var received = socket.ReceiveAsync(buffer, cancellation.Token).GetAwaiter().GetResult();
            if (received.MessageType == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException("Chromium closed the DevTools connection unexpectedly.");
            }
            message.Write(buffer, 0, received.Count);
            if (!received.EndOfMessage)
            {
                if (message.Length > 16 * 1024 * 1024)
                {
                    throw new InvalidOperationException("The browser DevTools response exceeded 16 MiB.");
                }
                continue;
            }

            using var document = JsonDocument.Parse(message.GetBuffer().AsMemory(0, checked((int)message.Length)));
            var response = document.RootElement;
            message.SetLength(0);
            if (!response.TryGetProperty("id", out var id) || id.GetInt32() != 1)
            {
                continue;
            }
            if (response.TryGetProperty("error", out var error))
            {
                throw new InvalidOperationException($"Chromium rejected {method}: {error}");
            }
            return response.TryGetProperty("result", out var result) ? result.Clone() : throw new InvalidOperationException("Chromium returned no DevTools result.");
        }
    }

    private DevToolsTarget[] GetTargets()
    {
        var json = HttpClient.GetStringAsync($"http://127.0.0.1:{port}/json/list").GetAwaiter().GetResult();
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Chromium returned an invalid DevTools target list.");
        }

        return document.RootElement.EnumerateArray()
            .Select(target => new DevToolsTarget(
                GetRequiredString(target, "id"),
                GetRequiredString(target, "type"),
                GetRequiredString(target, "url"),
                GetRequiredString(target, "webSocketDebuggerUrl")))
            .ToArray();
    }

    private Uri ValidateWebSocketEndpoint(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "ws" || endpoint.Port != port ||
            !IPAddress.TryParse(endpoint.Host, out var address) || !IPAddress.IsLoopback(address))
        {
            throw new InvalidOperationException("Chromium reported a DevTools endpoint outside the local loopback interface.");
        }
        return endpoint;
    }

    private static string GetRequiredString(JsonElement value, string property) =>
        value.TryGetProperty(property, out var result) && result.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(result.GetString())
            ? result.GetString()! : throw new InvalidOperationException($"Chromium DevTools target has no {property}.");

    private static bool UrlMatches(string actual, string expected) => Uri.TryCreate(actual, UriKind.Absolute, out var actualUri) &&
        Uri.TryCreate(expected, UriKind.Absolute, out var expectedUri) && Uri.Compare(actualUri, expectedUri, UriComponents.HttpRequestUrl, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0;

    private sealed record DevToolsTarget(string Id, string Type, string Url, string WebSocketDebuggerUrl);
}
