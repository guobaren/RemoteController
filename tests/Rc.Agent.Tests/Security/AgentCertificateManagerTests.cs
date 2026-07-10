using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Rc.Agent.Persistence;
using Rc.Agent.Security;
using Rc.Agent.Tests.Persistence;
using Xunit;

namespace Rc.Agent.Tests.Security;

public sealed class AgentCertificateManagerTests
{
    [Fact]
    public async Task GetOrCreateAsyncCreatesAndRestoresStableP256TlsIdentity()
    {
        using var directory = new TemporaryDirectory();
        string deviceId;
        string fingerprint;
        DateTimeOffset createdAtUtc;

        await using (var store = new AgentStateStore(directory.Path))
        {
            await store.InitializeAsync();
            var manager = new AgentCertificateManager(store);
            using var identity = await manager.GetOrCreateAsync();

            deviceId = identity.DeviceId;
            fingerprint = identity.CertificateSha256Fingerprint;
            createdAtUtc = identity.CreatedAtUtc;
            Assert.True(identity.Certificate.HasPrivateKey);
            Assert.Equal(identity.Certificate.Subject, identity.Certificate.Issuer);
            using var privateKey = identity.Certificate.GetECDsaPrivateKey();
            Assert.NotNull(privateKey);
            Assert.Equal(256, privateKey.KeySize);
            Assert.Equal(64, fingerprint.Length);
        }

        await using (var store = new AgentStateStore(directory.Path))
        {
            await store.InitializeAsync();
            var manager = new AgentCertificateManager(store);
            using var restored = await manager.GetOrCreateAsync();

            Assert.Equal(deviceId, restored.DeviceId);
            Assert.Equal(fingerprint, restored.CertificateSha256Fingerprint);
            Assert.Equal(createdAtUtc, restored.CreatedAtUtc);
            Assert.True(restored.Certificate.HasPrivateKey);
        }
    }

    [Fact]
    public async Task GetOrCreateAsyncRejectsCorruptPersistedIdentityInsteadOfReplacingIt()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(directory.Path);
        await store.InitializeAsync();
        await store.SaveDeviceIdentityAsync(new DeviceIdentity(
            "existing-agent",
            [1, 2, 3],
            [4, 5, 6],
            DateTimeOffset.UtcNow));

        var manager = new AgentCertificateManager(store);

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.GetOrCreateAsync());
        var persisted = await store.GetDeviceIdentityAsync();
        Assert.NotNull(persisted);
        Assert.Equal("existing-agent", persisted.DeviceId);
        Assert.Equal(new byte[] { 1, 2, 3 }, persisted.Certificate);
    }
}
