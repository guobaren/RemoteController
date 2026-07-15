using System.Text.Json;

namespace Rc.Agent.Security;

/// <summary>
/// Stores the public Agent identity in the access-controlled data directory so
/// a local administrator can verify the TLS fingerprint before pairing.
/// </summary>
public static class LocalAgentIdentityFile
{
    private const string FileName = "agent-identity.json";

    public static string GetPath(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        return Path.Combine(Path.GetFullPath(dataRoot), FileName);
    }

    public static void Write(string dataRoot, string deviceId, string certificateSha256Fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(certificateSha256Fingerprint);
        if (certificateSha256Fingerprint.Length != 64 || !certificateSha256Fingerprint.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("The certificate SHA-256 fingerprint must be 64 hexadecimal characters.", nameof(certificateSha256Fingerprint));
        }

        var path = GetPath(dataRoot);
        var temporaryPath = Path.Combine(Path.GetDirectoryName(path)!, $".{FileName}.{Guid.NewGuid():N}.tmp");
        var identity = new LocalAgentIdentity(deviceId, certificateSha256Fingerprint);

        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(identity));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    public static void Clear(string dataRoot)
    {
        File.Delete(GetPath(dataRoot));
    }

    public static bool TryRead(string dataRoot, out LocalAgentIdentity? identity)
    {
        identity = null;
        var path = GetPath(dataRoot);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var saved = JsonSerializer.Deserialize<LocalAgentIdentity>(File.ReadAllText(path));
            if (saved is null || string.IsNullOrWhiteSpace(saved.DeviceId) ||
                string.IsNullOrWhiteSpace(saved.CertificateSha256Fingerprint) ||
                saved.CertificateSha256Fingerprint.Length != 64 ||
                !saved.CertificateSha256Fingerprint.All(Uri.IsHexDigit))
            {
                return false;
            }

            identity = saved with { CertificateSha256Fingerprint = saved.CertificateSha256Fingerprint.ToUpperInvariant() };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

public sealed record LocalAgentIdentity(
    string DeviceId,
    string CertificateSha256Fingerprint);
