using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Rc.Contracts;

namespace Rc.Cli.Security;

internal static class ControllerPairingProtocol
{
    private static readonly byte[] TranscriptDomain = Encoding.UTF8.GetBytes("rc/pairing/transcript/v1");
    private static readonly byte[] ConfirmationDomain = Encoding.UTF8.GetBytes("rc/pairing/controller-confirm/v1");

    public static byte[] ComputeTranscriptHash(ControlPairingBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!IPAddress.TryParse(binding.AgentAddress, out var address))
        {
            throw new CryptographicException("The agent returned an invalid pairing address.");
        }

        using var stream = new MemoryStream();
        WriteField(stream, TranscriptDomain);
        WriteUInt32(stream, 1);
        WriteField(stream, binding.PairingId.ToByteArray());
        WriteString(stream, binding.AgentDeviceId);
        WriteString(stream, binding.ControllerId);
        WriteField(stream, address.MapToIPv6().GetAddressBytes());
        WriteUInt16(stream, checked((ushort)binding.AgentPort));
        WriteFingerprint(stream, binding.AgentCertificateFingerprint);
        WriteFingerprint(stream, binding.AgentSpkiFingerprint);
        WriteFingerprint(stream, binding.ControllerCertificateFingerprint);
        WriteFingerprint(stream, binding.ControllerSpkiFingerprint);
        return SHA256.HashData(stream.ToArray());
    }

    public static (byte[] ConfirmationMac, byte[] CertificateSignature) CreateCompletionProof(
        byte[] sessionKey, ControlPairingBinding binding, ECDsa privateKey)
    {
        var transcriptHash = ComputeTranscriptHash(binding);
        try
        {
            var payload = new byte[ConfirmationDomain.Length + transcriptHash.Length];
            Buffer.BlockCopy(ConfirmationDomain, 0, payload, 0, ConfirmationDomain.Length);
            Buffer.BlockCopy(transcriptHash, 0, payload, ConfirmationDomain.Length, transcriptHash.Length);
            try
            {
                using var mac = new HMACSHA256(sessionKey);
                return (mac.ComputeHash(payload), privateKey.SignData(payload, HashAlgorithmName.SHA256));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(payload);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(transcriptHash);
        }
    }

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value.Normalize(NormalizationForm.FormC));
        try
        {
            WriteField(stream, bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void WriteFingerprint(Stream stream, byte[] value)
    {
        if (value.Length != 32)
        {
            throw new CryptographicException("A pairing fingerprint must be SHA-256 sized.");
        }

        WriteField(stream, value);
    }

    private static void WriteField(Stream stream, byte[] value)
    {
        WriteUInt32(stream, checked((uint)value.Length));
        stream.Write(value);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        stream.Write(bytes);
    }
}