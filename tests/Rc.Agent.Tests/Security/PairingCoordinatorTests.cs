using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Rc.Agent.Persistence;
using Rc.Agent.Security;
using Rc.Agent.Tests.Persistence;
using Xunit;

namespace Rc.Agent.Tests.Security;

public sealed class PairingCoordinatorTests
{
    [Fact]
    public async Task SuccessfulPairingPinsControllerAndRejectsAnotherControllerUntilUnpaired()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(directory.Path);
        await store.InitializeAsync();
        using var coordinator = new PairingCoordinator(store, new AgentCertificateManager(store));
        using var firstController = CreateControllerCertificate();

        var paired = await PairAsync(coordinator, "controller-first", firstController);
        var persisted = await store.GetPairedControllerAsync();

        Assert.Equal("controller-first", paired.ControllerId);
        Assert.NotNull(persisted);
        Assert.Equal("controller-first", persisted.ControllerId);
        Assert.Equal(firstController.Certificate.RawData, persisted.Certificate);
        Assert.Equal(0, coordinator.ActiveInvitationCount);
        Assert.True(PairedControllerCertificateValidator.Matches(persisted, firstController.Certificate));

        using var differentController = CreateControllerCertificate();
        Assert.False(PairedControllerCertificateValidator.Matches(persisted, differentController.Certificate));
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.CreateInvitationAsync(LoopbackEndpoint));

        await coordinator.UnpairAsync();
        var nextInvitation = await coordinator.CreateInvitationAsync(LoopbackEndpoint);
        Assert.NotEqual(Guid.Empty, nextInvitation.PairingId);
    }

    [Fact]
    public async Task OneWrongCodeCanRetryButThirdFailureInvalidatesTheInvitation()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(directory.Path);
        await store.InitializeAsync();
        using var coordinator = new PairingCoordinator(
            store,
            new AgentCertificateManager(store),
            new PairingCoordinatorOptions { MaxFailedAttempts = 3 });
        using var controller = CreateControllerCertificate();

        var invitation = await coordinator.CreateInvitationAsync(LoopbackEndpoint);
        var binding = await coordinator.BindControllerAsync(
            invitation.PairingId,
            "controller-retry",
            controller.Certificate.RawData);

        await AssertWrongCodeRejectedAsync(coordinator, invitation, binding);
        Assert.Equal(1, coordinator.ActiveInvitationCount);

        var paired = await CompleteBoundInvitationAsync(coordinator, invitation, binding, controller);
        Assert.Equal("controller-retry", paired.ControllerId);

        await coordinator.UnpairAsync();
        invitation = await coordinator.CreateInvitationAsync(LoopbackEndpoint);
        binding = await coordinator.BindControllerAsync(
            invitation.PairingId,
            "controller-lockout",
            controller.Certificate.RawData);

        await AssertWrongCodeRejectedAsync(coordinator, invitation, binding);
        await AssertWrongCodeRejectedAsync(coordinator, invitation, binding);
        await AssertWrongCodeRejectedAsync(coordinator, invitation, binding);

        Assert.Equal(0, coordinator.ActiveInvitationCount);
        Assert.Throws<InvalidOperationException>(() => coordinator.CreateAgentRound1(invitation.PairingId));
    }

    [Fact]
    public async Task PairingCompletionRejectsProofNotSignedByPinnedCertificate()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(directory.Path);
        await store.InitializeAsync();
        using var coordinator = new PairingCoordinator(store, new AgentCertificateManager(store));
        using var controller = CreateControllerCertificate();
        using var attackerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var invitation = await coordinator.CreateInvitationAsync(LoopbackEndpoint);
        var binding = await coordinator.BindControllerAsync(
            invitation.PairingId,
            "controller-proof",
            controller.Certificate.RawData);
        var proof = CompletePakeExchange(coordinator, invitation, binding, attackerKey);

        await Assert.ThrowsAsync<CryptographicException>(() => coordinator.CompleteAsync(invitation.PairingId, proof));
        Assert.Equal(1, coordinator.ActiveInvitationCount);
        Assert.Null(await store.GetPairedControllerAsync());
    }

    private static PairingEndpoint LoopbackEndpoint => new(IPAddress.Loopback, 43001);

    private static async Task<PairedController> PairAsync(
        PairingCoordinator coordinator,
        string controllerId,
        ControllerCertificate controller)
    {
        var invitation = await coordinator.CreateInvitationAsync(LoopbackEndpoint);
        var binding = await coordinator.BindControllerAsync(
            invitation.PairingId,
            controllerId,
            controller.Certificate.RawData);
        return await CompleteBoundInvitationAsync(coordinator, invitation, binding, controller);
    }

    private static async Task<PairedController> CompleteBoundInvitationAsync(
        PairingCoordinator coordinator,
        PairingInvitation invitation,
        PairingBinding binding,
        ControllerCertificate controller)
    {
        var proof = CompletePakeExchange(coordinator, invitation, binding, controller.PrivateKey);
        return await coordinator.CompleteAsync(invitation.PairingId, proof);
    }

    private static async Task AssertWrongCodeRejectedAsync(
        PairingCoordinator coordinator,
        PairingInvitation invitation,
        PairingBinding binding)
    {
        var transcriptHash = PairingTranscript.ComputeHash(binding);
        try
        {
            using var wrongController = new JpakePairingSession(
                invitation.PairingId,
                PairingPakeRole.Controller,
                "ZZZZZZ",
                transcriptHash);
            var agentRound1 = coordinator.CreateAgentRound1(invitation.PairingId);
            var wrongRound1 = wrongController.CreateRound1();
            coordinator.ReceiveControllerRound1(invitation.PairingId, wrongRound1);
            wrongController.ReceiveRound1(agentRound1);

            var agentRound2 = coordinator.CreateAgentRound2(invitation.PairingId);
            var wrongRound2 = wrongController.CreateRound2();
            coordinator.ReceiveControllerRound2(invitation.PairingId, wrongRound2);
            wrongController.ReceiveRound2(agentRound2);

            _ = coordinator.CreateAgentRound3(invitation.PairingId);
            var wrongRound3 = wrongController.CreateRound3();
            Assert.Throws<CryptographicException>(
                () => coordinator.ReceiveControllerRound3(invitation.PairingId, wrongRound3));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(transcriptHash);
        }

        await Task.CompletedTask;
    }

    private static PairingCompletionProof CompletePakeExchange(
        PairingCoordinator coordinator,
        PairingInvitation invitation,
        PairingBinding binding,
        ECDsa proofSigningKey)
    {
        var transcriptHash = PairingTranscript.ComputeHash(binding);
        try
        {
            using var controller = new JpakePairingSession(
                invitation.PairingId,
                PairingPakeRole.Controller,
                invitation.OneTimeCode,
                transcriptHash);
            var agentRound1 = coordinator.CreateAgentRound1(invitation.PairingId);
            var controllerRound1 = controller.CreateRound1();
            coordinator.ReceiveControllerRound1(invitation.PairingId, controllerRound1);
            controller.ReceiveRound1(agentRound1);

            var agentRound2 = coordinator.CreateAgentRound2(invitation.PairingId);
            var controllerRound2 = controller.CreateRound2();
            coordinator.ReceiveControllerRound2(invitation.PairingId, controllerRound2);
            controller.ReceiveRound2(agentRound2);

            var agentRound3 = coordinator.CreateAgentRound3(invitation.PairingId);
            var controllerRound3 = controller.CreateRound3();
            coordinator.ReceiveControllerRound3(invitation.PairingId, controllerRound3);
            controller.ReceiveRound3(agentRound3);

            using var result = controller.GetResult();
            return PairingCompletionProof.Create(result, binding, proofSigningKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(transcriptHash);
        }
    }

    private static ControllerCertificate CreateControllerCertificate()
    {
        var privateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(
            "CN=RemoteController Test Client",
            privateKey,
            HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        var usages = new OidCollection { new Oid("1.3.6.1.5.5.7.3.2") };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, true));
        var now = DateTimeOffset.UtcNow;
        return new ControllerCertificate(
            request.CreateSelfSigned(now.AddMinutes(-1), now.AddDays(30)),
            privateKey);
    }

    private sealed class ControllerCertificate : IDisposable
    {
        public ControllerCertificate(X509Certificate2 certificate, ECDsa privateKey)
        {
            Certificate = certificate;
            PrivateKey = privateKey;
        }

        public X509Certificate2 Certificate { get; }

        public ECDsa PrivateKey { get; }

        public void Dispose()
        {
            Certificate.Dispose();
            PrivateKey.Dispose();
        }
    }
}
