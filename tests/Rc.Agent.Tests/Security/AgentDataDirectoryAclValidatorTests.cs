using System.Security.AccessControl;
using System.Security.Principal;
using Rc.Agent.Security;
using Rc.Agent.Tests.Persistence;
using Xunit;

namespace Rc.Agent.Tests.Security;

public sealed class AgentDataDirectoryAclValidatorTests
{
    [Fact]
    public void EnsureSafeRejectsAnExplicitWriteRuleForAnUntrustedPrincipal()
    {
        using var directory = new TemporaryDirectory();
        var security = new DirectoryInfo(directory.Path).GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.WriteData,
            AccessControlType.Allow));
        new DirectoryInfo(directory.Path).SetAccessControl(security);

        var exception = Assert.Throws<InvalidOperationException>(() => AgentDataDirectoryAclValidator.EnsureSafe(directory.Path));

        Assert.Contains("write", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public void EnsureSafeRejectsAnInheritedWriteRuleForAnUntrustedPrincipal()
    {
        using var parent = new TemporaryDirectory();
        var parentInfo = new DirectoryInfo(parent.Path);
        var security = parentInfo.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.WriteData,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        parentInfo.SetAccessControl(security);
        var dataRoot = Path.Combine(parent.Path, "agent-data");
        Directory.CreateDirectory(dataRoot);

        var exception = Assert.Throws<InvalidOperationException>(() => AgentDataDirectoryAclValidator.EnsureSafe(dataRoot));

        Assert.Contains("write", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnsureSafeRejectsDeletePermissionForAnUntrustedPrincipal()
    {
        using var directory = new TemporaryDirectory();
        var security = new DirectoryInfo(directory.Path).GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.Delete,
            AccessControlType.Allow));
        new DirectoryInfo(directory.Path).SetAccessControl(security);

        Assert.Throws<InvalidOperationException>(() => AgentDataDirectoryAclValidator.EnsureSafe(directory.Path));
    }

}