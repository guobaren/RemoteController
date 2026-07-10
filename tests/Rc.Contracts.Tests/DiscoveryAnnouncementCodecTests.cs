using System.Text;
using Rc.Contracts;
using Xunit;

namespace Rc.Contracts.Tests;

public sealed class DiscoveryAnnouncementCodecTests
{
    private const string Fingerprint = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    [Fact]
    public void RoundTripsSupportedAnnouncementWithNormalizedFields()
    {
        var original = new DiscoveryAnnouncement("device-1", "Caf\u0065\u0301", 43123, DiscoveryAnnouncement.CurrentProtocolVersion, Fingerprint.ToLowerInvariant());

        var payload = DiscoveryAnnouncementCodec.Encode(original);

        Assert.True(DiscoveryAnnouncementCodec.TryDecode(payload, out var decoded));
        Assert.NotNull(decoded);
        Assert.Equal("device-1", decoded.DeviceId);
        Assert.Equal("Café", decoded.DisplayName);
        Assert.Equal(43123, decoded.TcpPort);
        Assert.Equal(DiscoveryAnnouncement.CurrentProtocolVersion, decoded.ProtocolVersion);
        Assert.Equal(Fingerprint, decoded.CertificateSha256Fingerprint);
    }

    [Fact]
    public void PayloadContainsOnlyDiscoveryMetadataAndNoPairingSecret()
    {
        var payload = DiscoveryAnnouncementCodec.Encode(new DiscoveryAnnouncement("device-redacted", "Office PC", 43001, 1, Fingerprint));
        var text = Encoding.UTF8.GetString(payload);

        Assert.DoesNotContain("one-time-code", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pairing", text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(InvalidPayloads))]
    public void RejectsMalformedOversizedTrailingAndUnsupportedPayloads(byte[] payload)
    {
        Assert.False(DiscoveryAnnouncementCodec.TryDecode(payload, out _));
    }

    public static IEnumerable<object[]> InvalidPayloads()
    {
        var valid = DiscoveryAnnouncementCodec.Encode(new DiscoveryAnnouncement("device-invalid", "Test", 43001, 1, Fingerprint));

        var wrongMagic = valid.ToArray();
        wrongMagic[0] = (byte)'X';
        yield return [wrongMagic];

        yield return [valid[..^1]];

        var trailing = new byte[valid.Length + 1];
        valid.CopyTo(trailing, 0);
        yield return [trailing];

        var unsupportedVersion = valid.ToArray();
        unsupportedVersion[6] = 2;
        yield return [unsupportedVersion];

        var invalidUtf8 = valid.ToArray();
        invalidUtf8[43] = 0xff;
        yield return [invalidUtf8];

        yield return [new byte[DiscoveryAnnouncementCodec.MaxDatagramBytes + 1]];
    }
}
