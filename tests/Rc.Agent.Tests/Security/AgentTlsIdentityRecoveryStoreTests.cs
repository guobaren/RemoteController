using Rc.Agent.Persistence;
using Rc.Agent.Tests.Persistence;
using Xunit;

namespace Rc.Agent.Tests.Security;

public sealed class AgentTlsIdentityRecoveryStoreTests
{
    [Fact]
    public async Task IdentityCanBeRemovedWithoutChangingPairingState()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(directory.Path);
        await store.InitializeAsync();
        await store.SaveDeviceIdentityAsync(new DeviceIdentity(
            "agent-for-recovery",
            [1, 2, 3],
            [4, 5, 6],
            DateTimeOffset.UtcNow));

        Assert.False(await store.HasPairedControllerAsync());

        await store.DeleteDeviceIdentityAsync();

        Assert.Null(await store.GetDeviceIdentityAsync());
        Assert.False(await store.HasPairedControllerAsync());
    }
}
