using Rc.Agent.Security;

namespace Rc.Contracts;

/// <summary>
/// The first request permitted on an unauthenticated TLS control connection.
/// It intentionally exposes only public identity metadata and never grants command access.
/// </summary>
public sealed record ControlHelloRequest(int ProtocolVersion)
{
    public string Kind => ControlMessageKinds.Hello;
}

public sealed record ControlHelloResponse(
    int ProtocolVersion,
    string DeviceId,
    string CertificateSha256Fingerprint,
    bool HasPairedController);

public static class ControlMessageKinds
{
    public const string Hello = "hello";
    public const string PairStart = "pair_start";
    public const string PairRound1 = "pair_round1";
    public const string PairRound2 = "pair_round2";
    public const string PairComplete = "pair_complete";
}

public sealed record ControlPairStartRequest(
    int ProtocolVersion,
    string ControllerId,
    byte[] ControllerCertificate)
{
    public string Kind => ControlMessageKinds.PairStart;
}

public sealed record ControlPairRound1Request(Guid PairingId, PairingPakeRound1 ControllerRound1)
{
    public string Kind => ControlMessageKinds.PairRound1;
}

public sealed record ControlPairRound2Request(Guid PairingId, PairingPakeRound2 ControllerRound2)
{
    public string Kind => ControlMessageKinds.PairRound2;
}

public sealed record ControlPairCompleteRequest(
    Guid PairingId,
    PairingPakeRound3 ControllerRound3,
    byte[] ConfirmationMac,
    byte[] CertificateSignature)
{
    public string Kind => ControlMessageKinds.PairComplete;
}

public sealed record ControlPairingBinding(
    Guid PairingId,
    string AgentDeviceId,
    string ControllerId,
    string AgentAddress,
    int AgentPort,
    byte[] AgentCertificateFingerprint,
    byte[] AgentSpkiFingerprint,
    byte[] ControllerCertificateFingerprint,
    byte[] ControllerSpkiFingerprint);

public sealed record ControlPairStartResponse(
    ControlPairingBinding Binding,
    DateTimeOffset ExpiresAtUtc,
    PairingPakeRound1 AgentRound1);

public sealed record ControlPairRound2Response(PairingPakeRound2 AgentRound2);

public sealed record ControlPairRound3Response(PairingPakeRound3 AgentRound3);

public sealed record ControlPairCompleteResponse(string ControllerId, DateTimeOffset PairedAtUtc);