using System.Security.AccessControl;
using System.Security.Principal;
using Rc.Agent.Persistence;
using Rc.Agent.Security;
using Rc.Agent.Tests.Persistence;
using Xunit;

namespace Rc.Agent.Tests.Security;

public sealed class AgentDataDirectoryAclValidatorTests
{
    private const string TrustedSidsVariable = "RC_AGENT_TRUSTED_SIDS";
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
    public void EnsureSafeCreatesAProtectedDirectoryBelowAnUnsafeParent()
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
        var dataRoot = Path.Combine(parent.Path, "new-agent-data");

        AgentDataDirectoryAclValidator.EnsureSafe(dataRoot);

        Assert.True(Directory.Exists(dataRoot));
    }

    [Fact]
    public async Task StateStoreInitializesANewSecureDirectoryBelowAnUnsafeParent()
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

        await using var stateStore = new AgentStateStore(Path.Combine(parent.Path, "new-agent-data"));
        await stateStore.InitializeAsync();

        Assert.True(File.Exists(stateStore.DatabasePath));
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

    [Fact]
    public void EnsureSafeAllowsReadOnlyPermissionForAnUntrustedPrincipal()
    {
        using var directory = new TemporaryDirectory();
        var security = new DirectoryInfo(directory.Path).GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.ReadAndExecute,
            AccessControlType.Allow));
        new DirectoryInfo(directory.Path).SetAccessControl(security);

        AgentDataDirectoryAclValidator.EnsureSafe(directory.Path);
    }

    [Fact]
    public void EnsureSafeAllowsWritePermissionForAConfiguredTrustedSid()
    {
        using var directory = new TemporaryDirectory();
        var previous = Environment.GetEnvironmentVariable(TrustedSidsVariable);
        var worldSid = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        try
        {
            Environment.SetEnvironmentVariable(TrustedSidsVariable, worldSid.Value);
            var security = new DirectoryInfo(directory.Path).GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(worldSid, FileSystemRights.WriteData, AccessControlType.Allow));
            new DirectoryInfo(directory.Path).SetAccessControl(security);
            AgentDataDirectoryAclValidator.EnsureSafe(directory.Path);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TrustedSidsVariable, previous);
        }
    }

    [Fact]
    public void EnsureSafeRejectsMalformedConfiguredTrustedSid()
    {
        using var directory = new TemporaryDirectory();
        var previous = Environment.GetEnvironmentVariable(TrustedSidsVariable);
        try
        {
            Environment.SetEnvironmentVariable(TrustedSidsVariable, "not-a-sid");
            var exception = Assert.Throws<InvalidOperationException>(() => AgentDataDirectoryAclValidator.EnsureSafe(directory.Path));
            Assert.Contains(TrustedSidsVariable, exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TrustedSidsVariable, previous);
        }
    }
}
