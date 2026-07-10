using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Rc.Contracts;

/// <summary>
/// Unauthenticated LAN discovery metadata. A receiver must establish a separate
/// authenticated pairing or mTLS connection before trusting the sender.
/// </summary>
public sealed class DiscoveryAnnouncement
{
    public const ushort CurrentProtocolVersion = 1;

    public DiscoveryAnnouncement(string deviceId, string displayName, int tcpPort, ushort protocolVersion, string certificateSha256Fingerprint)
    {
        DeviceId = NormalizeText(deviceId, nameof(deviceId), 128);
        DisplayName = NormalizeText(displayName, nameof(displayName), 128);
        if (tcpPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(tcpPort));
        }

        if (protocolVersion != CurrentProtocolVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(protocolVersion));
        }

        CertificateSha256Fingerprint = NormalizeFingerprint(certificateSha256Fingerprint);
        TcpPort = tcpPort;
        ProtocolVersion = protocolVersion;
    }

    public string DeviceId { get; }

    public string DisplayName { get; }

    public int TcpPort { get; }

    public ushort ProtocolVersion { get; }

    public string CertificateSha256Fingerprint { get; }

    internal static string NormalizeText(string value, string parameterName, int maxUtf8Bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Normalize(NormalizationForm.FormC);
        if (normalized.Any(char.IsControl) || Utf8.GetByteCount(normalized) > maxUtf8Bytes)
        {
            throw new ArgumentException("The value contains unsupported characters or is too long.", parameterName);
        }

        return normalized;
    }

    internal static string NormalizeFingerprint(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length != 64 || !value.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("The certificate fingerprint must be a 64-character SHA-256 hexadecimal value.", nameof(value));
        }

        return value.ToUpperInvariant();
    }

    internal static readonly UTF8Encoding Utf8 = new(false, true);
}

/// <summary>Strict bounded binary codec for discovery datagrams; it is not an authentication protocol.</summary>
public static class DiscoveryAnnouncementCodec
{
    public const int MaxDatagramBytes = 512;

    private const byte WireFormatVersion = 1;
    private const int FixedHeaderLength = 43;
    private static readonly byte[] Magic = "RCDA"u8.ToArray();

    public static byte[] Encode(DiscoveryAnnouncement announcement)
    {
        ArgumentNullException.ThrowIfNull(announcement);
        var deviceId = DiscoveryAnnouncement.Utf8.GetBytes(announcement.DeviceId);
        var displayName = DiscoveryAnnouncement.Utf8.GetBytes(announcement.DisplayName);
        var fingerprint = Convert.FromHexString(announcement.CertificateSha256Fingerprint);
        try
        {
            var length = FixedHeaderLength + deviceId.Length + displayName.Length;
            if (length > MaxDatagramBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(announcement), "The discovery announcement exceeds the datagram limit.");
            }

            var payload = new byte[length];
            Magic.CopyTo(payload, 0);
            payload[4] = WireFormatVersion;
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(5, 2), announcement.ProtocolVersion);
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(7, 2), checked((ushort)announcement.TcpPort));
            fingerprint.CopyTo(payload, 9);
            payload[41] = checked((byte)deviceId.Length);
            payload[42] = checked((byte)displayName.Length);
            deviceId.CopyTo(payload, 43);
            displayName.CopyTo(payload, 43 + deviceId.Length);
            return payload;
        }
        finally
        {
            Array.Clear(deviceId);
            Array.Clear(displayName);
            Array.Clear(fingerprint);
        }
    }

    public static bool TryDecode(ReadOnlySpan<byte> payload, [NotNullWhen(true)] out DiscoveryAnnouncement? announcement)
    {
        announcement = null;
        if (payload.Length is < FixedHeaderLength or > MaxDatagramBytes || !payload[..4].SequenceEqual(Magic) || payload[4] != WireFormatVersion)
        {
            return false;
        }

        var protocolVersion = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(5, 2));
        if (protocolVersion != DiscoveryAnnouncement.CurrentProtocolVersion)
        {
            return false;
        }

        var tcpPort = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(7, 2));
        var deviceIdLength = payload[41];
        var displayNameLength = payload[42];
        var expectedLength = FixedHeaderLength + deviceIdLength + displayNameLength;
        if (tcpPort == 0 || expectedLength != payload.Length || deviceIdLength == 0 || displayNameLength == 0)
        {
            return false;
        }

        try
        {
            var deviceId = DiscoveryAnnouncement.Utf8.GetString(payload.Slice(43, deviceIdLength));
            var displayName = DiscoveryAnnouncement.Utf8.GetString(payload.Slice(43 + deviceIdLength, displayNameLength));
            announcement = new DiscoveryAnnouncement(deviceId, displayName, tcpPort, protocolVersion, Convert.ToHexString(payload.Slice(9, 32)));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
