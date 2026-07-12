using System.Security.Cryptography;
using Rc.Contracts;
using Xunit;

namespace Rc.Contracts.Tests;

public sealed class BrokerRequestAuthenticationTests
{
    [Fact]
    public void LaunchSignatureBindsIdentityCommandNonceAndTimestamp()
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(32);
        var issuedAt = DateTimeOffset.UtcNow;
        var launch = CreateLaunch("Write-Output trusted");
        var tag = BrokerRequestAuthentication.SignLaunch("request-1", issuedAt, nonce, launch, secret);
        var request = new BrokerLaunchRequest(1, "request-1", issuedAt, nonce, launch, tag);

        Assert.True(BrokerRequestAuthentication.VerifyLaunch(request, secret, issuedAt));
        Assert.False(BrokerRequestAuthentication.VerifyLaunch(request with { Launch = CreateLaunch("Write-Output tampered") }, secret, issuedAt));
        Assert.False(BrokerRequestAuthentication.VerifyLaunch(request with { Nonce = RandomNumberGenerator.GetBytes(32) }, secret, issuedAt));
        Assert.False(BrokerRequestAuthentication.VerifyLaunch(request, secret, issuedAt.AddMinutes(3)));
    }

    private static TaskLaunchRequest CreateLaunch(string command) => new(
        "job-elevated",
        ExecRequest.ForShellWithIdentity(ShellKind.PowerShell, command, ExecutionIdentity.ElevatedBroker),
        ExecutionIdentity.ElevatedBroker,
        "C:\\ProgramData\\RemoteController",
        "rc-elevated-test",
        TimeSpan.FromSeconds(1));
}
