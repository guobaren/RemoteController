using Xunit;
using Rc.Agent.Security;
using Rc.Agent.Tests.Persistence;

namespace Rc.Agent.Tests.Security;

public sealed class LocalAgentIdentityFileTests
{
    [Fact]
    public void IdentityCanBeReadAndFingerprintIsNormalized()
    {
        using var directory = new TemporaryDirectory();

        LocalAgentIdentityFile.Write(directory.Path, "agent-1", new string('a', 64));

        Assert.True(LocalAgentIdentityFile.TryRead(directory.Path, out var identity));
        Assert.NotNull(identity);
        Assert.Equal("agent-1", identity!.DeviceId);
        Assert.Equal(new string('A', 64), identity.CertificateSha256Fingerprint);
    }

    [Fact]
    public void InvalidFingerprintIsRejected()
    {
        using var directory = new TemporaryDirectory();

        Assert.Throws<ArgumentException>(() =>
            LocalAgentIdentityFile.Write(directory.Path, "agent-1", "not-a-sha256-fingerprint"));
    }
}
