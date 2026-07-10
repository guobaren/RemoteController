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
    public const string ExecOnce = "exec_once";
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
/// <summary>
/// A one-shot command request made by the paired controller. The request is signed
/// with the paired controller certificate so a TLS connection alone never grants
/// process-launch authority.
/// </summary>
public sealed record ControlExecuteOnceRequest(
    int ProtocolVersion,
    string ControllerId,
    ExecRequest Execution,
    byte[] Signature)
{
    public string Kind => ControlMessageKinds.ExecOnce;
}

/// <summary>
/// The terminal result of a one-shot command. Output is bounded per stream; when
/// a stream exceeds the response limit its complete data remains in Agent storage
/// for the later job-log protocol.
/// </summary>
public sealed record ControlExecuteOnceResponse(
    JobSnapshot Job,
    byte[] StandardOutput,
    bool StandardOutputTruncated,
    byte[] StandardError,
    bool StandardErrorTruncated);