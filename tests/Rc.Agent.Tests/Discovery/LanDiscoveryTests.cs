using System.Net;
using Rc.Agent.Discovery;
using Rc.Agent.Security;
using Rc.Contracts;
using Xunit;

namespace Rc.Agent.Tests.Discovery;

public sealed class LanDiscoveryTests
{
    private const string Fingerprint = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    [Fact]
    public void DeduplicatesByDeviceIdKeepsLatestAndSortsByDisplayName()
    {
        var results = LanDiscoveryResults.DeduplicateAndSort(
        [
            Device("device-2", "Zulu", "192.168.1.20", 43001),
            Device("device-1", "alpha", "192.168.1.10", 43001),
            Device("device-2", "Beta", "192.168.1.21", 43002),
        ]);

        Assert.Collection(
            results,
            first =>
            {
                Assert.Equal("alpha", first.Announcement.DisplayName);
                Assert.Equal("device-1", first.Announcement.DeviceId);
            },
            second =>
            {
                Assert.Equal("Beta", second.Announcement.DisplayName);
                Assert.Equal("192.168.1.21", second.Address.ToString());
                Assert.Equal(43002, second.Announcement.TcpPort);
            });
    }

    [Theory]
    [InlineData("192.168.1.20:43001", true)]
    [InlineData("[fe80::1]:43001", true)]
    [InlineData("host.example:43001", false)]
    [InlineData("192.168.1.20", false)]
    [InlineData("192.168.1.20:0", false)]
    public void ManualPairingEndpointFallbackAcceptsOnlyConcreteIpEndpoints(string value, bool expected)
    {
        var parsed = PairingEndpoint.TryParse(value, out var endpoint);

        Assert.Equal(expected, parsed);
        Assert.Equal(expected, endpoint is not null);
    }

    [Fact]
    public void OptionsRejectNonMulticastAddress()
    {
        var options = new LanDiscoveryOptions { MulticastAddress = IPAddress.Loopback };

        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    private static DiscoveredLanDevice Device(string deviceId, string displayName, string address, int port) =>
        new(new DiscoveryAnnouncement(deviceId, displayName, port, 1, Fingerprint), IPAddress.Parse(address));
}
