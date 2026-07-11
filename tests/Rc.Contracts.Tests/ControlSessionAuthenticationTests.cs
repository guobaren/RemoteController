using System.Security.Cryptography;
using Rc.Contracts;
using Xunit;

namespace Rc.Contracts.Tests;

public sealed class ControlSessionAuthenticationTests
{
    [Fact]
    public void SessionSignatureBindsConnectionChallengeAndExpiry()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var sessionId = Guid.NewGuid();
        var challenge = RandomNumberGenerator.GetBytes(32);
        var expires = DateTimeOffset.UtcNow.AddMinutes(10);
        var signature = ControlRequestAuthentication.SignSessionAuthentication("agent", "controller", sessionId, challenge, expires, key);

        Assert.True(ControlRequestAuthentication.VerifySessionAuthentication("agent", "controller", sessionId, challenge, expires, signature, key));
        Assert.False(ControlRequestAuthentication.VerifySessionAuthentication("agent", "controller", Guid.NewGuid(), challenge, expires, signature, key));
        Assert.False(ControlRequestAuthentication.VerifySessionAuthentication("agent", "controller", sessionId, RandomNumberGenerator.GetBytes(32), expires, signature, key));
        Assert.False(ControlRequestAuthentication.VerifySessionAuthentication("agent", "controller", sessionId, challenge, expires.AddSeconds(1), signature, key));
    }
}