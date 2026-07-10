using Rc.Agent.Security;
using Xunit;

namespace Rc.Agent.Tests.Security;

public sealed class DpapiSecretProtectorTests
{
    [Fact]
    public void ProtectThenUnprotectRestoresSensitiveBytesForTheCurrentUser()
    {
        var protector = new DpapiSecretProtector();
        var secret = new byte[] { 3, 1, 4, 1, 5, 9, 2, 6 };

        var protectedBytes = protector.Protect(secret);
        var restored = protector.Unprotect(protectedBytes);

        Assert.False(secret.SequenceEqual(protectedBytes));
        Assert.Equal(secret, restored);
    }
}