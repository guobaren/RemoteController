using System.Security.AccessControl;
using System.Security.Principal;

namespace Rc.Agent.Security;

public static class AgentDataDirectoryAclValidator
{
    public static void EnsureSafe(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        Directory.CreateDirectory(dataRoot);

        var currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current Windows user SID is unavailable.");
        var trustedSids = new HashSet<SecurityIdentifier>
        {
            currentUser,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
        };
        var security = new DirectoryInfo(dataRoot).GetAccessControl(AccessControlSections.Access);
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>();
        foreach (var rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow)
            {
                continue;
            }

            if (rule.IdentityReference is not SecurityIdentifier sid || trustedSids.Contains(sid))
            {
                continue;
            }

            if (GrantsWrite(rule.FileSystemRights))
            {
                throw new InvalidOperationException(
                    $"Data root grants write access to untrusted principal '{sid.Value}'.");
            }
        }
    }

    private static bool GrantsWrite(FileSystemRights rights)
    {
        const FileSystemRights WriteRights =
            FileSystemRights.FullControl |
            FileSystemRights.Write |
            FileSystemRights.WriteData |
            FileSystemRights.CreateFiles |
            FileSystemRights.AppendData |
            FileSystemRights.CreateDirectories |
            FileSystemRights.WriteAttributes |
            FileSystemRights.WriteExtendedAttributes |
            FileSystemRights.Delete |
            FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.ChangePermissions |
            FileSystemRights.TakeOwnership;
        return (rights & WriteRights) != 0;
    }
}