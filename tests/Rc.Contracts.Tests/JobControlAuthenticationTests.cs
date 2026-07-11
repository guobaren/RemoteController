using System.Security.Cryptography;
using Rc.Contracts;
using Xunit;

namespace Rc.Contracts.Tests;

public sealed class JobControlAuthenticationTests
{
    [Fact]
    public void JobOperationSignaturesBindEveryOperationPayload()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var data = new byte[] { 1, 2, 3 };

        var logs = ControlRequestAuthentication.SignJobLogs("agent", "controller", "job", JobOutputKind.Stdout, 10, 20, key);
        Assert.True(ControlRequestAuthentication.VerifyJobLogs("agent", "controller", "job", JobOutputKind.Stdout, 10, 20, logs, key));
        Assert.False(ControlRequestAuthentication.VerifyJobLogs("agent", "controller", "job", JobOutputKind.Stderr, 10, 20, logs, key));

        var input = ControlRequestAuthentication.SignJobInput("agent", "controller", "job", data, key);
        Assert.True(ControlRequestAuthentication.VerifyJobInput("agent", "controller", "job", data, input, key));
        Assert.False(ControlRequestAuthentication.VerifyJobInput("agent", "controller", "job", new byte[] { 1, 2, 4 }, input, key));

        var close = ControlRequestAuthentication.SignJobCloseInput("agent", "controller", "job", key);
        Assert.True(ControlRequestAuthentication.VerifyJobCloseInput("agent", "controller", "job", close, key));
        Assert.False(ControlRequestAuthentication.VerifyJobCancel("agent", "controller", "job", close, key));

        var cancel = ControlRequestAuthentication.SignJobCancel("agent", "controller", "job", key);
        Assert.True(ControlRequestAuthentication.VerifyJobCancel("agent", "controller", "job", cancel, key));

        var wait = ControlRequestAuthentication.SignJobWait("agent", "controller", "job", TimeSpan.FromSeconds(1), key);
        Assert.True(ControlRequestAuthentication.VerifyJobWait("agent", "controller", "job", TimeSpan.FromSeconds(1), wait, key));
        Assert.False(ControlRequestAuthentication.VerifyJobWait("agent", "controller", "job", TimeSpan.FromSeconds(2), wait, key));
    }
}