using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices;
using Org.BouncyCastle.Crypto.Agreement.JPake;
using Org.BouncyCastle.Math;

namespace Rc.Agent.Security;

public enum PairingPakeRole
{
    Agent,
    Controller,
}

public sealed record PairingPakeRound1(
    string ParticipantId,
    string Gx1,
    string Gx2,
    string ProofX1Gv,
    string ProofX1R,
    string ProofX2Gv,
    string ProofX2R);

public sealed record PairingPakeRound2(
    string ParticipantId,
    string A,
    string ProofGv,
    string ProofR);

public sealed record PairingPakeRound3(string ParticipantId, string MacTag);

public sealed class PairingPakeResult : IDisposable
{
    private byte[] sessionKey;

    internal PairingPakeResult(byte[] sessionKey)
    {
        this.sessionKey = sessionKey.ToArray();
    }

    public byte[] SessionKey => sessionKey.ToArray();

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(sessionKey);
        sessionKey = [];
    }
}

/// <summary>
/// A single-use RFC 8236 J-PAKE participant. The caller supplies a SHA-256 hash of
/// the canonical pairing transcript as <paramref name="transcriptHash"/>.
/// </summary>
public sealed class JpakePairingSession : IDisposable
{
    private const int TranscriptHashLength = 32;
    private const int MaxSerializedIntegerBytes = 1024;
    private readonly Guid pairingId;
    private readonly string participantId;
    private readonly string expectedPeerId;
    private readonly byte[] transcriptHash;
    private readonly char[] password;
    private JPakeParticipant? participant;
    private BigInteger? keyingMaterial;
    private byte[]? sessionKey;
    private bool localRound1Created;
    private bool peerRound1Received;
    private bool localRound2Created;
    private bool peerRound2Received;
    private bool localRound3Created;
    private bool peerRound3Validated;
    private bool disposed;

    public JpakePairingSession(
        Guid pairingId,
        PairingPakeRole role,
        string oneTimeCode,
        ReadOnlySpan<byte> transcriptHash)
        : this(pairingId, role, oneTimeCode.AsSpan(), transcriptHash)
    {
    }

    public JpakePairingSession(
        Guid pairingId,
        PairingPakeRole role,
        ReadOnlySpan<char> oneTimeCode,
        ReadOnlySpan<byte> transcriptHash)
    {
        if (pairingId == Guid.Empty)
        {
            throw new ArgumentException("The pairing ID must not be empty.", nameof(pairingId));
        }

        if (oneTimeCode.IsEmpty || oneTimeCode.IsWhiteSpace())
        {
            throw new ArgumentException("The one-time code must not be empty.", nameof(oneTimeCode));
        }

        if (transcriptHash.Length != TranscriptHashLength)
        {
            throw new ArgumentException("The pairing transcript hash must be SHA-256 sized.", nameof(transcriptHash));
        }

        this.pairingId = pairingId;
        participantId = $"{pairingId:N}:{role.ToString().ToLowerInvariant()}";
        expectedPeerId = $"{pairingId:N}:{(role == PairingPakeRole.Agent ? "controller" : "agent")}";
        this.transcriptHash = transcriptHash.ToArray();
        password = DerivePassword(pairingId, oneTimeCode);
        participant = new JPakeParticipant(participantId, password);
    }

    public PairingPakeRound1 CreateRound1()
    {
        ThrowIfDisposed();
        if (localRound1Created)
        {
            throw new InvalidOperationException("Round 1 was already created for this pairing session.");
        }

        localRound1Created = true;
        return ToMessage(participant!.CreateRound1PayloadToSend());
    }

    public void ReceiveRound1(PairingPakeRound1 message)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(message);
        if (peerRound1Received)
        {
            throw new InvalidOperationException("A peer round 1 payload was already accepted for this pairing session.");
        }

        ValidatePeerPayload(() => participant!.ValidateRound1PayloadReceived(ToPayload(message, expectedPeerId)));
        peerRound1Received = true;
    }

    public PairingPakeRound2 CreateRound2()
    {
        ThrowIfDisposed();
        if (!peerRound1Received || localRound2Created)
        {
            throw new InvalidOperationException("Round 2 is not available in the current pairing state.");
        }

        localRound2Created = true;
        return ToMessage(participant!.CreateRound2PayloadToSend());
    }

    public void ReceiveRound2(PairingPakeRound2 message)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(message);
        if (!localRound2Created || peerRound2Received)
        {
            throw new InvalidOperationException("A peer round 2 payload is not valid in the current pairing state.");
        }

        ValidatePeerPayload(() => participant!.ValidateRound2PayloadReceived(ToPayload(message, expectedPeerId)));
        keyingMaterial = participant!.CalculateKeyingMaterial();
        sessionKey = DeriveSessionKey(transcriptHash, keyingMaterial);
        peerRound2Received = true;
    }

    public PairingPakeRound3 CreateRound3()
    {
        ThrowIfDisposed();
        if (!peerRound2Received || localRound3Created)
        {
            throw new InvalidOperationException("Round 3 is not available in the current pairing state.");
        }

        localRound3Created = true;
        return ToMessage(participant!.CreateRound3PayloadToSend(keyingMaterial!));
    }

    public void ReceiveRound3(PairingPakeRound3 message)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(message);
        if (!localRound3Created || peerRound3Validated)
        {
            throw new InvalidOperationException("A peer round 3 payload is not valid in the current pairing state.");
        }

        ValidatePeerPayload(() => participant!.ValidateRound3PayloadReceived(ToPayload(message, expectedPeerId), keyingMaterial!));
        peerRound3Validated = true;
    }

    public PairingPakeResult GetResult()
    {
        ThrowIfDisposed();
        if (!peerRound3Validated || sessionKey is null)
        {
            throw new InvalidOperationException("The J-PAKE exchange has not completed mutual confirmation.");
        }

        return new PairingPakeResult(sessionKey);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        CryptographicOperations.ZeroMemory(transcriptHash);
        CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(password.AsSpan()));
        if (sessionKey is not null)
        {
            CryptographicOperations.ZeroMemory(sessionKey);
            sessionKey = null;
        }

        keyingMaterial = null;
        participant = null;
    }

    private static PairingPakeRound1 ToMessage(JPakeRound1Payload payload) => new(
        payload.ParticipantId,
        ToBase64(payload.Gx1),
        ToBase64(payload.Gx2),
        ToBase64(payload.KnowledgeProofForX1[0]),
        ToBase64(payload.KnowledgeProofForX1[1]),
        ToBase64(payload.KnowledgeProofForX2[0]),
        ToBase64(payload.KnowledgeProofForX2[1]));

    private static PairingPakeRound2 ToMessage(JPakeRound2Payload payload) => new(
        payload.ParticipantId,
        ToBase64(payload.A),
        ToBase64(payload.KnowledgeProofForX2s[0]),
        ToBase64(payload.KnowledgeProofForX2s[1]));

    private static PairingPakeRound3 ToMessage(JPakeRound3Payload payload) => new(payload.ParticipantId, ToBase64(payload.MacTag));

    private static JPakeRound1Payload ToPayload(PairingPakeRound1 message, string expectedParticipantId)
    {
        ValidateParticipantId(message.ParticipantId, expectedParticipantId);
        return new JPakeRound1Payload(
            message.ParticipantId,
            FromBase64(message.Gx1),
            FromBase64(message.Gx2),
            [FromBase64(message.ProofX1Gv), FromBase64(message.ProofX1R)],
            [FromBase64(message.ProofX2Gv), FromBase64(message.ProofX2R)]);
    }

    private static JPakeRound2Payload ToPayload(PairingPakeRound2 message, string expectedParticipantId)
    {
        ValidateParticipantId(message.ParticipantId, expectedParticipantId);
        return new JPakeRound2Payload(
            message.ParticipantId,
            FromBase64(message.A),
            [FromBase64(message.ProofGv), FromBase64(message.ProofR)]);
    }

    private static JPakeRound3Payload ToPayload(PairingPakeRound3 message, string expectedParticipantId)
    {
        ValidateParticipantId(message.ParticipantId, expectedParticipantId);
        return new JPakeRound3Payload(message.ParticipantId, FromBase64(message.MacTag));
    }

    private static void ValidateParticipantId(string actual, string expected)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new CryptographicException("The J-PAKE payload belongs to a different pairing participant.");
        }
    }

    private static void ValidatePeerPayload(Action validation)
    {
        try
        {
            validation();
        }
        catch (Org.BouncyCastle.Crypto.CryptoException exception)
        {
            throw new CryptographicException("The J-PAKE peer payload did not validate.", exception);
        }
    }

    private static string ToBase64(BigInteger value) => Convert.ToBase64String(value.ToByteArray());

    private static BigInteger FromBase64(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > ((MaxSerializedIntegerBytes + 2) / 3 * 4))
        {
            throw new CryptographicException("The J-PAKE payload integer exceeds the allowed size.");
        }

        try
        {
            var bytes = Convert.FromBase64String(value);
            if (bytes.Length is 0 or > MaxSerializedIntegerBytes)
            {
                throw new CryptographicException("The J-PAKE payload integer exceeds the allowed size.");
            }

            return new BigInteger(bytes);
        }
        catch (FormatException exception)
        {
            throw new CryptographicException("The J-PAKE payload contains invalid base64.", exception);
        }
    }

    private static char[] DerivePassword(Guid pairingId, ReadOnlySpan<char> oneTimeCode)
    {
        var suppliedCode = oneTimeCode.ToString();
        var normalizedCode = suppliedCode.Normalize(NormalizationForm.FormC);
        if (normalizedCode.Length is < 6 or > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(oneTimeCode), "The one-time code must be between 6 and 128 characters.");
        }

        var codeBytes = Encoding.UTF8.GetBytes(normalizedCode);
        try
        {
            var material = HkdfSha256(pairingId.ToByteArray(), codeBytes, "rc/pairing/jpake-password", 32);
            try
            {
                return Convert.ToHexString(material).ToCharArray();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(material);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(codeBytes);
        }
    }

    private static byte[] DeriveSessionKey(byte[] transcriptHash, BigInteger keyingMaterial)
    {
        var material = keyingMaterial.ToByteArrayUnsigned();
        try
        {
            return HkdfSha256(transcriptHash, material, "rc/pairing/session", 32);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
        }
    }

    private static byte[] HkdfSha256(byte[] salt, byte[] inputKeyMaterial, string info, int length)
    {
        using var extractor = new HMACSHA256(salt);
        var prk = extractor.ComputeHash(inputKeyMaterial);
        try
        {
            var infoBytes = Encoding.UTF8.GetBytes(info);
            var output = new byte[length];
            var previous = Array.Empty<byte>();
            try
            {
                for (var offset = 0; offset < length;)
                {
                    var blockInput = new byte[previous.Length + infoBytes.Length + 1];
                    Buffer.BlockCopy(previous, 0, blockInput, 0, previous.Length);
                    Buffer.BlockCopy(infoBytes, 0, blockInput, previous.Length, infoBytes.Length);
                    blockInput[^1] = checked((byte)(offset / 32 + 1));
                    using var expander = new HMACSHA256(prk);
                    var block = expander.ComputeHash(blockInput);
                    CryptographicOperations.ZeroMemory(blockInput);
                    var copyLength = Math.Min(block.Length, length - offset);
                    Buffer.BlockCopy(block, 0, output, offset, copyLength);
                    offset += copyLength;
                    CryptographicOperations.ZeroMemory(previous);
                    previous = block;
                }

                return output;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(infoBytes);
                CryptographicOperations.ZeroMemory(previous);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(prk);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }
}
