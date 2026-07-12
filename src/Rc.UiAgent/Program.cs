using System.Text.Json;
using System.IO.Pipes;
using System.IO;
using System.Text;
using Rc.Contracts;
using Rc.UiAgent;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: rc-ui-agent run | rc-ui-agent snapshot [--windows] | rc-ui-agent screenshot <display|window> <target> | rc-ui-agent window <handle> <activate|minimize|maximize|restore|close> | rc-ui-agent mouse move <display|window> <target> <x> <y> | rc-ui-agent type <display|window> <target> <text>");
    return 2;
}

try
{
    if (string.Equals(args[0], "run", StringComparison.OrdinalIgnoreCase) && args.Length == 1)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        var registrationPipe = Environment.GetEnvironmentVariable("RC_UI_REGISTRATION_PIPE") ?? "rc-ui-registration";
        var controlPipe = "rc-ui-agent-session-" + Environment.ProcessId;
        var server = new UiAgentCommandServer(controlPipe, Environment.GetEnvironmentVariable("RC_UI_AGENT_CONTROL_CLIENT_SID"));
        var serverTask = server.RunAsync(cancellation.Token);
        await RegisterAsync(registrationPipe, CreateRegistration(controlPipe));
        var registrationTask = MaintainRegistrationAsync(registrationPipe, controlPipe, cancellation.Token);
        await Console.Out.WriteLineAsync("UiAgent registered and ready for commands.");
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        await registrationTask;
        await serverTask;
        return 0;
    }

    if (string.Equals(args[0], "snapshot", StringComparison.OrdinalIgnoreCase) &&
        (args.Length == 1 || args.Length == 2 && string.Equals(args[1], "--windows", StringComparison.OrdinalIgnoreCase)))
    {
        var snapshot = DesktopSnapshotProvider.Capture(includeWindows: args.Length == 2);
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(snapshot, ContractJson.Options));
        return 0;
    }

    if (string.Equals(args[0], "window", StringComparison.OrdinalIgnoreCase) && args.Length == 3 &&
        long.TryParse(args[1], out var handle) && handle != 0 &&
        Enum.TryParse<WindowAction>(args[2], ignoreCase: true, out var action))
    {
        var window = DesktopWindowController.Act(handle, action);
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new UiWindowActionResponse(window), ContractJson.Options));
        return 0;
    }

    if (string.Equals(args[0], "screenshot", StringComparison.OrdinalIgnoreCase) && args.Length == 3 &&
        long.TryParse(args[2], out var targetId) && targetId >= 0)
    {
        UiTarget target = args[1].ToLowerInvariant() switch
        {
            "display" when targetId <= int.MaxValue => new DisplayTarget((int)targetId),
            "window" when targetId > 0 => new WindowTarget(targetId),
            _ => throw new ArgumentException("Screenshot target must be display <non-negative index> or window <positive handle>.")
        };
        var screenshot = DesktopScreenshotProvider.Capture(target);
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(screenshot, ContractJson.Options));
        return 0;
    }

    if (string.Equals(args[0], "mouse", StringComparison.OrdinalIgnoreCase) && args.Length == 6 &&
        string.Equals(args[1], "move", StringComparison.OrdinalIgnoreCase) &&
        TryCreateTarget(args[2], args[3], out var mouseTarget) &&
        int.TryParse(args[4], out var x) && int.TryParse(args[5], out var y))
    {
        var snapshot = DesktopInputController.MoveMouse(new UiMouseMoveRequest(mouseTarget, x, y));
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new UiSnapshotResponse(snapshot), ContractJson.Options));
        return 0;
    }

    if (string.Equals(args[0], "type", StringComparison.OrdinalIgnoreCase) && args.Length == 4 &&
        TryCreateTarget(args[1], args[2], out var textTarget))
    {
        var snapshot = DesktopInputController.TypeText(new UiTextRequest(textTarget, args[3]));
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new UiSnapshotResponse(snapshot), ContractJson.Options));
        return 0;
    }

    Console.Error.WriteLine("Usage: rc-ui-agent run | rc-ui-agent snapshot [--windows] | rc-ui-agent screenshot <display|window> <target> | rc-ui-agent window <handle> <activate|minimize|maximize|restore|close> | rc-ui-agent mouse move <display|window> <target> <x> <y> | rc-ui-agent type <display|window> <target> <text>");
    return 2;
}
catch (PlatformNotSupportedException exception)
{
    await Console.Error.WriteLineAsync($"UI Agent is unavailable: {exception.Message}");
    return 1;
}
catch (InvalidOperationException exception)
{
    await Console.Error.WriteLineAsync($"UI action rejected: {exception.Message}");
    return 1;
}
catch (ArgumentException exception)
{
    await Console.Error.WriteLineAsync($"UI request rejected: {exception.Message}");
    return 2;
}

static bool TryCreateTarget(string kind, string targetId, out UiTarget target)
{
    target = null!;
    if (!long.TryParse(targetId, out var parsed))
    {
        return false;
    }

    switch (kind.ToLowerInvariant())
    {
        case "display" when parsed is >= 0 and <= int.MaxValue:
            target = new DisplayTarget((int)parsed);
            return true;
        case "window" when parsed > 0:
            target = new WindowTarget(parsed);
            return true;
        default:
            return false;
    }
}

static async Task RegisterAsync(string pipeName, UiAgentRegistration registration)
{
    await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    await client.ConnectAsync(5000);
    await using var writer = new StreamWriter(client, new UTF8Encoding(false), 1024 * 1024, leaveOpen: true) { AutoFlush = true };
    using var reader = new StreamReader(client, new UTF8Encoding(false), false, 1024 * 1024, leaveOpen: true);
    await writer.WriteLineAsync(JsonSerializer.Serialize(registration, ContractJson.Options));
    var line = await reader.ReadLineAsync();
    var response = line is null ? null : JsonSerializer.Deserialize<ResultEnvelope<UiAgentRegistrationResponse>>(line, ContractJson.Options);
    if (response is not { Ok: true, Result.Accepted: true })
    {
        throw new InvalidOperationException(response?.Error?.Message ?? "The Agent rejected UiAgent registration.");
    }
}

static UiAgentRegistration CreateRegistration(string controlPipe) => new(
    1,
    DesktopSnapshotProvider.CapabilityVersion,
    controlPipe,
    DesktopSnapshotProvider.Capture(includeWindows: false));

static async Task MaintainRegistrationAsync(string registrationPipe, string controlPipe, CancellationToken cancellationToken)
{
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
    try
    {
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                await RegisterAsync(registrationPipe, CreateRegistration(controlPipe));
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                await Console.Error.WriteLineAsync($"UiAgent registration refresh failed: {exception.Message}");
            }
        }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
    }
}
