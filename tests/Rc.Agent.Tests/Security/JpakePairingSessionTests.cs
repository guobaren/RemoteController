using System.Security.Cryptography;
using System.Text;
using Rc.Agent.Security;
using Xunit;

namespace Rc.Agent.Tests.Security;

public sealed class JpakePairingSessionTests
{
    [Fact]
    public void MatchingCodeAndTranscriptCompleteWithTheSameSessionKey()
    {
        var pairingId = Guid.NewGuid();
        var transcriptHash = SHA256.HashData(Encoding.UTF8.GetBytes("canonical pairing transcript"));
        using var agent = new JpakePairingSession(pairingId, PairingPakeRole.Agent, "893142", transcriptHash);
        using var controller = new JpakePairingSession(pairingId, PairingPakeRole.Controller, "893142", transcriptHash);

        CompleteExchange(agent, controller);

        using var agentResult = agent.GetResult();
        using var controllerResult = controller.GetResult();
        Assert.True(CryptographicOperations.FixedTimeEquals(agentResult.SessionKey, controllerResult.SessionKey));
    }

    [Fact]
    public void TranscriptHashChangesTheDerivedSessionKey()
    {
        var pairingId = Guid.NewGuid();
        using var agent = new JpakePairingSession(
            pairingId,
            PairingPakeRole.Agent,
            "893142",
            SHA256.HashData(Encoding.UTF8.GetBytes("transcript-a")));
        using var controller = new JpakePairingSession(
            pairingId,
            PairingPakeRole.Controller,
            "893142",
            SHA256.HashData(Encoding.UTF8.GetBytes("transcript-b")));

        CompleteExchange(agent, controller);

        using var agentResult = agent.GetResult();
        using var controllerResult = controller.GetResult();
        Assert.False(CryptographicOperations.FixedTimeEquals(agentResult.SessionKey, controllerResult.SessionKey));
    }

    [Fact]
    public void RoundOneFromDifferentParticipantIsRejectedBeforeJpakeValidation()
    {
        var pairingId = Guid.NewGuid();
        var transcriptHash = SHA256.HashData(Encoding.UTF8.GetBytes("canonical pairing transcript"));
        using var agent = new JpakePairingSession(pairingId, PairingPakeRole.Agent, "893142", transcriptHash);
        using var controller = new JpakePairingSession(pairingId, PairingPakeRole.Controller, "893142", transcriptHash);
        var forged = agent.CreateRound1() with { ParticipantId = "unrelated-pairing:agent" };

        Assert.Throws<CryptographicException>(() => controller.ReceiveRound1(forged));
    }

    private static void CompleteExchange(JpakePairingSession agent, JpakePairingSession controller)
    {
        var agentRound1 = agent.CreateRound1();
        var controllerRound1 = controller.CreateRound1();
        agent.ReceiveRound1(controllerRound1);
        controller.ReceiveRound1(agentRound1);

        var agentRound2 = agent.CreateRound2();
        var controllerRound2 = controller.CreateRound2();
        agent.ReceiveRound2(controllerRound2);
        controller.ReceiveRound2(agentRound2);

        var agentRound3 = agent.CreateRound3();
        var controllerRound3 = controller.CreateRound3();
        agent.ReceiveRound3(controllerRound3);
        controller.ReceiveRound3(agentRound3);
    }
}
