using System.Security.Cryptography;
using System.Text.Json;

namespace Rc.Agent.Security;

/// <summary>
/// Publishes the current pairing code only in the Agent data directory. The
/// installer restricts that directory to LocalService, SYSTEM, and local
/// administrators, so this is a local-console handoff rather than network
/// pairing material.
/// </summary>
public static class LocalPairingCodeFile
{
    private const string FileName = "pairing-code.json";

    public static string GetPath(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        return Path.Combine(Path.GetFullPath(dataRoot), FileName);
    }

    public static void Write(string dataRoot, PairingInvitation invitation)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        Write(dataRoot, new LocalPairingCode(
            invitation.AgentDeviceId,
            invitation.OneTimeCode,
            invitation.ExpiresAtUtc));
    }

    /// <summary>
    /// Creates a local-only pairing code before a controller connects. The Agent
    /// consumes this code when it receives the next pairing start request.
    /// </summary>
    public static LocalPairingCode Arm(
        string dataRoot,
        LocalAgentIdentity identity,
        DateTimeOffset now,
        TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        }

        var code = new char[10];
        for (var index = 0; index < code.Length; index++)
        {
            code[index] = PairingCoordinator.OneTimeCodeAlphabet[
                RandomNumberGenerator.GetInt32(PairingCoordinator.OneTimeCodeAlphabet.Length)];
        }

        var pairingCode = new LocalPairingCode(
            identity.DeviceId,
            new string(code),
            now.Add(lifetime),
            IsArmed: true);
        Write(dataRoot, pairingCode);
        return pairingCode;
    }

    private static void Write(string dataRoot, LocalPairingCode contents)
    {
        var path = GetPath(dataRoot);
        var temporaryPath = Path.Combine(Path.GetDirectoryName(path)!, $".{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(contents));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    public static bool TryReadCurrent(string dataRoot, DateTimeOffset now, out LocalPairingCode? pairingCode)
    {
        pairingCode = null;
        var path = GetPath(dataRoot);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var saved = JsonSerializer.Deserialize<LocalPairingCode>(File.ReadAllText(path));
            if (saved is null || string.IsNullOrWhiteSpace(saved.OneTimeCode) || saved.ExpiresAtUtc <= now)
            {
                File.Delete(path);
                return false;
            }

            pairingCode = saved;
            return true;
        }
        catch (JsonException)
        {
            File.Delete(path);
            return false;
        }
    }

    public static void Delete(string dataRoot)
    {
        var path = GetPath(dataRoot);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}

public sealed record LocalPairingCode(
    string AgentDeviceId,
    string OneTimeCode,
    DateTimeOffset ExpiresAtUtc,
    bool IsArmed = false);
