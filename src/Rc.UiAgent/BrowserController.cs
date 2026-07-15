using System.Diagnostics;
using System.IO;
using System.Windows.Automation;
using Rc.Contracts;

namespace Rc.UiAgent;

public static class BrowserController
{
    private static readonly Dictionary<long, BrowserSession> Sessions = [];
    private static readonly object SessionsLock = new();
    private static readonly Dictionary<BrowserKind, string> BrowserProcesses = new()
    {
        [BrowserKind.Edge] = "msedge",
        [BrowserKind.Chrome] = "chrome",
    };

    public static UiBrowserLaunchResponse Launch(UiBrowserLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var before = DesktopSnapshotProvider.Capture(true).Windows.Select(window => window.Handle).ToHashSet();
        var session = StartBrowser(request.Browser, request.Url);
        var window = WaitForBrowserWindow(request.Browser, before);
        if (session is not null)
        {
            session.TargetId = session.Client.FindPageTarget(request.Url);
            lock (SessionsLock)
            {
                Sessions[window.Handle] = session;
            }
        }
        return new UiBrowserLaunchResponse(request.Browser, window);
    }

    public static UiBrowserNavigateResponse Navigate(UiBrowserNavigateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureBrowserWindow(request.Target);
        if (TryGetSession(request.Target.WindowHandle, out var session))
        {
            session.Client.Navigate(session.TargetId, request.Url);
        }
        else
        {
            DesktopInputController.NavigateBrowser(request.Target, request.Url);
        }
        return new UiBrowserNavigateResponse(DesktopSnapshotProvider.GetWindowSnapshot(request.Target.WindowHandle));
    }

    public static UiBrowserDomResponse GetDom(UiBrowserDomRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var window = EnsureBrowserWindow(request.Target);
        if (!TryGetSession(request.Target.WindowHandle, out var session))
        {
            throw new InvalidOperationException("Reliable page DOM is available only for Edge or Chrome windows launched by this UI agent.");
        }
        var document = session.Client.GetDocument(session.TargetId, request.Target.WindowHandle, request.MaximumDepth, request.MaximumElements);
        return new UiBrowserDomResponse(window, document);
    }

    private static BrowserSession? StartBrowser(BrowserKind browser, string url)
    {
        if (browser == BrowserKind.Default)
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return null;
        }

        var executable = FindBrowserExecutable(browser)
            ?? throw new InvalidOperationException($"{browser} is not installed for the active UI user.");
        var userDataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteController", "browser", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(userDataDirectory);
        var startInfo = new ProcessStartInfo(executable) { UseShellExecute = false };
        startInfo.ArgumentList.Add("--remote-debugging-address=127.0.0.1");
        startInfo.ArgumentList.Add("--remote-debugging-port=0");
        startInfo.ArgumentList.Add($"--user-data-dir={userDataDirectory}");
        startInfo.ArgumentList.Add("--no-first-run");
        startInfo.ArgumentList.Add("--new-window");
        startInfo.ArgumentList.Add(url);
        _ = Process.Start(startInfo) ?? throw new InvalidOperationException($"{browser} did not start.");
        return new BrowserSession(ChromiumDevToolsClient.WaitForProfile(userDataDirectory, TimeSpan.FromSeconds(10)));
    }

    private static string? FindBrowserExecutable(BrowserKind browser)
    {
        var relative = browser == BrowserKind.Edge
            ? Path.Combine("Microsoft", "Edge", "Application", "msedge.exe")
            : Path.Combine("Google", "Chrome", "Application", "chrome.exe");
        return new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) }
            .Select(root => Path.Combine(root, relative))
            .FirstOrDefault(File.Exists);
    }

    private static WindowSnapshot WaitForBrowserWindow(BrowserKind browser, HashSet<long> before)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var candidates = DesktopSnapshotProvider.Capture(true).Windows.Where(window => IsBrowser(window, browser)).ToArray();
            var window = candidates.FirstOrDefault(candidate => !before.Contains(candidate.Handle)) ?? candidates.FirstOrDefault();
            if (window is not null)
            {
                return window;
            }
            Thread.Sleep(100);
        }
        throw new InvalidOperationException("The browser did not create a visible window within ten seconds.");
    }

    private static WindowSnapshot EnsureBrowserWindow(WindowTarget target)
    {
        var window = DesktopSnapshotProvider.GetWindowSnapshot(target.WindowHandle);
        if (!IsBrowser(window, BrowserKind.Default))
        {
            throw new InvalidOperationException("The requested window is not a supported browser window.");
        }
        return window;
    }

    private static bool IsBrowser(WindowSnapshot window, BrowserKind browser) => browser == BrowserKind.Default
        ? BrowserProcesses.Values.Contains(window.ProcessName, StringComparer.OrdinalIgnoreCase) || string.Equals(window.ProcessName, "firefox", StringComparison.OrdinalIgnoreCase)
        : BrowserProcesses.TryGetValue(browser, out var process) && string.Equals(window.ProcessName, process, StringComparison.OrdinalIgnoreCase);

    private static bool TryGetSession(long windowHandle, out BrowserSession session)
    {
        lock (SessionsLock)
        {
            return Sessions.TryGetValue(windowHandle, out session!);
        }
    }

    private sealed class BrowserSession(ChromiumDevToolsClient client)
    {
        public ChromiumDevToolsClient Client { get; } = client;
        public string TargetId { get; set; } = string.Empty;
    }
}
