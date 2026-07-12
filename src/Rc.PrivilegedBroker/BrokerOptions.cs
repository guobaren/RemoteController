using System.Security.Principal;

namespace Rc.PrivilegedBroker;

public sealed record BrokerOptions(
    string PipeName,
    string SecretPath,
    string AllowedDataRoot,
    int Concurrency,
    bool AllowUnelevatedForTesting)
{
    public static BrokerOptions FromEnvironment()
    {
        var root = Environment.GetEnvironmentVariable("RC_AGENT_DATA_ROOT");
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RemoteController");
        }

        var pipeName = Environment.GetEnvironmentVariable("RC_BROKER_PIPE_NAME");
        if (string.IsNullOrWhiteSpace(pipeName))
        {
            pipeName = "rc-privileged-broker";
        }

        var secretPath = Environment.GetEnvironmentVariable("RC_BROKER_SECRET_PATH");
        if (string.IsNullOrWhiteSpace(secretPath))
        {
            secretPath = Path.Combine(root, "broker-auth.key");
        }

        var allowedRoot = Environment.GetEnvironmentVariable("RC_BROKER_ALLOWED_DATA_ROOT");
        if (string.IsNullOrWhiteSpace(allowedRoot))
        {
            allowedRoot = root;
        }

        var concurrency = 2;
        if (int.TryParse(Environment.GetEnvironmentVariable("RC_ELEVATED_TASK_LIMIT"), out var configured) && configured > 0)
        {
            concurrency = configured;
        }

        return new BrokerOptions(
            pipeName,
            Path.GetFullPath(secretPath),
            Path.GetFullPath(allowedRoot),
            concurrency,
            string.Equals(Environment.GetEnvironmentVariable("RC_BROKER_ALLOW_UNELEVATED"), "1", StringComparison.Ordinal));
    }
}

public static class BrokerProcessSecurity
{
    public static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
