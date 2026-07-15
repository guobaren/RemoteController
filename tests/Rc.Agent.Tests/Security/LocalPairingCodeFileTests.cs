using Xunit;
using Rc.Agent.Persistence;
using Rc.Agent.Security;
using Rc.Agent.Tests.Persistence;

namespace Rc.Agent.Tests.Security;

public sealed class LocalPairingCodeFileTests
{
    [Fact]
    public async Task LocalAdminArmPairingIsIdempotentUntilTheCodeExpires()
    {
        using var directory = new TemporaryDirectory();
        LocalAgentIdentityFile.Write(directory.Path, "agent-1", new string('A', 64));
        using var firstOutput = new StringWriter();
        using var secondOutput = new StringWriter();
        using var error = new StringWriter();

        var firstExitCode = await LocalAdminCommand.TryRunAsync(["arm-pairing"], directory.Path, firstOutput, error);
        var secondExitCode = await LocalAdminCommand.TryRunAsync(["arm-pairing"], directory.Path, secondOutput, error);

        Assert.Equal(0, firstExitCode);
        Assert.Equal(0, secondExitCode);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Equal(firstOutput.ToString(), secondOutput.ToString());
        Assert.True(LocalPairingCodeFile.TryReadCurrent(directory.Path, DateTimeOffset.UtcNow, out var pairingCode));
        Assert.True(pairingCode!.IsArmed);
    }

    [Fact]
    public void ArmedCodeIsPersistedAsLocalOnlyPrePairingState()
    {
        using var directory = new TemporaryDirectory();
        var identity = new LocalAgentIdentity("agent-1", new string('A', 64));
        var now = DateTimeOffset.UtcNow;

        var armed = LocalPairingCodeFile.Arm(directory.Path, identity, now, TimeSpan.FromMinutes(10));

        Assert.True(armed.IsArmed);
        Assert.Equal(10, armed.OneTimeCode.Length);
        Assert.Matches("^[23456789ABCDEFGHJKLMNPQRSTUVWXYZ]{10}$", armed.OneTimeCode);
        Assert.True(LocalPairingCodeFile.TryReadCurrent(directory.Path, now, out var current));
        Assert.Equal(armed, current);
    }

    [Fact]
    public async Task CurrentCodeCanBeReadAndExpiredCodeIsRemoved()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(directory.Path);
        await store.InitializeAsync();
        var certificateManager = new AgentCertificateManager(store);
        using var coordinator = new PairingCoordinator(store, certificateManager);
        var now = DateTimeOffset.UtcNow;
        var invitation = await coordinator.CreateInvitationAsync(
            new PairingEndpoint(System.Net.IPAddress.Loopback, 43001));

        LocalPairingCodeFile.Write(directory.Path, invitation);

        Assert.True(LocalPairingCodeFile.TryReadCurrent(directory.Path, now, out var current));
        Assert.NotNull(current);
        Assert.Equal(invitation.OneTimeCode, current!.OneTimeCode);
        Assert.Equal(invitation.AgentDeviceId, current.AgentDeviceId);

        Assert.False(LocalPairingCodeFile.TryReadCurrent(directory.Path, invitation.ExpiresAtUtc, out _));
        Assert.False(File.Exists(LocalPairingCodeFile.GetPath(directory.Path)));
    }
}
