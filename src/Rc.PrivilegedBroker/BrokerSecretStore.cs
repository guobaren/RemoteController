using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace Rc.PrivilegedBroker;

public static class BrokerSecretStore
{
    public static async Task<byte[]> LoadOrCreateAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        path = Path.GetFullPath(path);
        if (File.Exists(path))
        {
            var existing = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            return existing.Length >= 32 ? existing : throw new InvalidDataException("The broker secret is shorter than 32 bytes.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var secret = RandomNumberGenerator.GetBytes(32);
        try
        {
            await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await stream.WriteAsync(secret, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (IOException) when (File.Exists(path))
        {
            CryptographicOperations.ZeroMemory(secret);
            return await LoadOrCreateAsync(path, cancellationToken).ConfigureAwait(false);
        }

        RestrictAcl(path);
        return secret;
    }

    private static void RestrictAcl(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The broker account has no Windows SID.");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(currentUser, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }
}
