using System.Security.AccessControl;
using System.Security.Principal;

namespace Rc.Agent.Security;

public static class AgentDataDirectoryAclValidator
{
    public static void EnsureSafe(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current Windows user SID is unavailable.");
        var configuredTrustedSids = ReadConfiguredTrustedSids();
        if (!Directory.Exists(dataRoot))
        {
            var directory = new DirectoryInfo(dataRoot);
            directory.Create();
            directory.SetAccessControl(CreateSafeDirectorySecurity(currentUser, configuredTrustedSids));
        }

        var trustedSids = new HashSet<SecurityIdentifier>
        {
            currentUser,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
        };
        trustedSids.UnionWith(configuredTrustedSids);
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

    private static IReadOnlyList<SecurityIdentifier> ReadConfiguredTrustedSids()
    {
        var configured = Environment.GetEnvironmentVariable("RC_AGENT_TRUSTED_SIDS");
        if (string.IsNullOrWhiteSpace(configured))
        {
            return [];
        }

        try
        {
            return configured.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(value => new SecurityIdentifier(value))
                .ToArray();
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException("RC_AGENT_TRUSTED_SIDS contains an invalid Windows SID.", exception);
        }
    }

    private static DirectorySecurity CreateSafeDirectorySecurity(
        SecurityIdentifier currentUser,
        IReadOnlyList<SecurityIdentifier> additionalTrustedSids)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddFullControl(security, currentUser);
        AddFullControl(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        AddFullControl(security, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        foreach (var sid in additionalTrustedSids)
        {
            AddFullControl(security, sid);
        }
        return security;
    }

    private static void AddFullControl(DirectorySecurity security, SecurityIdentifier sid) =>
        security.AddAccessRule(new FileSystemAccessRule(
            sid,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

    private static bool GrantsWrite(FileSystemRights rights)
    {
        if ((rights & FileSystemRights.FullControl) == FileSystemRights.FullControl)
        {
            return true;
        }

        const FileSystemRights WriteRights =
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
