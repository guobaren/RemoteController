using Rc.Agent.Security;
using Rc.Agent.Tests.Persistence;
using Xunit;

namespace Rc.Agent.Tests.Security;

public sealed class LocalTlsIdentityRepairRequestTests
{
    [Fact]
    public void RepairRequestCanBeCreatedAndCleared()
    {
        using var directory = new TemporaryDirectory();

        Assert.False(LocalTlsIdentityRepairRequest.IsRequested(directory.Path));

        LocalTlsIdentityRepairRequest.Request(directory.Path);

        Assert.True(LocalTlsIdentityRepairRequest.IsRequested(directory.Path));

        LocalTlsIdentityRepairRequest.Clear(directory.Path);

        Assert.False(LocalTlsIdentityRepairRequest.IsRequested(directory.Path));
    }
}
