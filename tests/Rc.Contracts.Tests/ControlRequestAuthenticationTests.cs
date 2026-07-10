using System.Security.Cryptography;
using Rc.Contracts;
using Xunit;

namespace Rc.Contracts.Tests;

public sealed class ControlRequestAuthenticationTests
{
    [Fact]
    public void ExecuteOnceSignatureVerifiesOnlyForTheOriginalAgentControllerAndCommand()
    {
        using var privateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var execution = ExecRequest.ForShell(ShellKind.PowerShell, "Write-Output hello", "C:\\Temp");
        var signature = ControlRequestAuthentication.SignExecuteOnce("agent-1", "controller-1", execution, privateKey);

        try
        {
            Assert.True(ControlRequestAuthentication.VerifyExecuteOnce("agent-1", "controller-1", execution, signature, privateKey));
            Assert.False(ControlRequestAuthentication.VerifyExecuteOnce("agent-2", "controller-1", execution, signature, privateKey));
            Assert.False(ControlRequestAuthentication.VerifyExecuteOnce(
                "agent-1",
                "controller-1",
                ExecRequest.ForShell(ShellKind.PowerShell, "Write-Output altered", "C:\\Temp"),
                signature,
                privateKey));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    [Fact]
    public void JobSignaturesAreBoundToTheirOperationAndPayload()
    {
        using var privateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var statusSignature = ControlRequestAuthentication.SignJobStatus("agent-1", "controller-1", "job-1", privateKey);
        var startSignature = ControlRequestAuthentication.SignJobStart(
            "agent-1",
            "controller-1",
            ExecRequest.ForShell(ShellKind.Cmd, "echo hello"),
            privateKey);

        try
        {
            Assert.True(ControlRequestAuthentication.VerifyJobStatus("agent-1", "controller-1", "job-1", statusSignature, privateKey));
            Assert.False(ControlRequestAuthentication.VerifyJobStatus("agent-1", "controller-1", "job-2", statusSignature, privateKey));
            Assert.False(ControlRequestAuthentication.VerifyJobStart(
                "agent-1",
                "controller-1",
                ExecRequest.ForShell(ShellKind.Cmd, "echo hello"),
                statusSignature,
                privateKey));
            Assert.True(ControlRequestAuthentication.VerifyJobStart(
                "agent-1",
                "controller-1",
                ExecRequest.ForShell(ShellKind.Cmd, "echo hello"),
                startSignature,
                privateKey));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(statusSignature);
            CryptographicOperations.ZeroMemory(startSignature);
        }
    }
}