using System.Security.AccessControl;
using System.Security.Principal;
using Xunit;

namespace Rc.PrivilegedBroker.Tests;

public sealed class BrokerConfigurationTests
{
    [Fact]
    public async Task ExistingSecretAclIsReconciledForConfiguredClientSid()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rc-broker-secret-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "broker.key");
        try
        {
            var original = await BrokerSecretStore.LoadOrCreateAsync(path);
            var clientSid = new SecurityIdentifier(WellKnownSidType.WorldSid, null);

            var restored = await BrokerSecretStore.LoadOrCreateAsync(path, clientSid.Value);

            Assert.Equal(original, restored);
            var rules = new FileInfo(path).GetAccessControl(AccessControlSections.Access)
                .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
                .OfType<FileSystemAccessRule>();
            Assert.Contains(rules, rule =>
                Equals(rule.IdentityReference, clientSid) &&
                rule.AccessControlType == AccessControlType.Allow &&
                (rule.FileSystemRights & FileSystemRights.Read) == FileSystemRights.Read);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
