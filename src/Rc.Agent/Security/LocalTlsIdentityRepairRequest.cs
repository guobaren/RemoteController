namespace Rc.Agent.Security;

/// <summary>
/// Records a local-administrator request to regenerate the Agent TLS identity
/// on the next service start. The service consumes the request only when no
/// controller is paired, so recovery cannot silently invalidate an active
/// controller trust relationship.
/// </summary>
public static class LocalTlsIdentityRepairRequest
{
    private const string FileName = "tls-identity-repair.request";

    public static string GetPath(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        return Path.Combine(Path.GetFullPath(dataRoot), FileName);
    }

    public static void Request(string dataRoot)
    {
        var path = GetPath(dataRoot);
        var temporaryPath = Path.Combine(Path.GetDirectoryName(path)!, $".{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, DateTimeOffset.UtcNow.ToString("O"));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    public static bool IsRequested(string dataRoot) => File.Exists(GetPath(dataRoot));

    public static void Clear(string dataRoot)
    {
        File.Delete(GetPath(dataRoot));
    }
}
