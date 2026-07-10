using System.Net;
using System.Net.Sockets;
using Rc.Contracts;

namespace Rc.Agent.Discovery;

public sealed record LanDiscoveryOptions
{
    public IPAddress MulticastAddress { get; init; } = IPAddress.Parse("239.255.78.77");

    public int Port { get; init; } = 43000;

    public TimeSpan AnnouncementInterval { get; init; } = TimeSpan.FromSeconds(3);

    public short MulticastTimeToLive { get; init; } = 1;

    public void Validate()
    {
        if (MulticastAddress.AddressFamily != AddressFamily.InterNetwork || MulticastAddress.GetAddressBytes()[0] is < 224 or > 239)
        {
            throw new ArgumentOutOfRangeException(nameof(MulticastAddress));
        }

        if (Port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(Port));
        }

        if (AnnouncementInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(AnnouncementInterval));
        }

        if (MulticastTimeToLive is < 1 or > 255)
        {
            throw new ArgumentOutOfRangeException(nameof(MulticastTimeToLive));
        }
    }
}

/// <summary>Periodically broadcasts unauthenticated LAN discovery metadata.</summary>
public sealed class LanDiscoveryPublisher : IAsyncDisposable
{
    private readonly LanDiscoveryOptions options;
    private readonly UdpClient client;
    private bool disposed;

    public LanDiscoveryPublisher(LanDiscoveryOptions? options = null)
    {
        this.options = options ?? new LanDiscoveryOptions();
        this.options.Validate();
        client = new UdpClient(AddressFamily.InterNetwork);
        client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, this.options.MulticastTimeToLive);
    }

    public async Task PublishOnceAsync(DiscoveryAnnouncement announcement, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var payload = DiscoveryAnnouncementCodec.Encode(announcement);
        await client.SendAsync(payload, new IPEndPoint(options.MulticastAddress, options.Port), cancellationToken);
    }

    public async Task RunAsync(Func<DiscoveryAnnouncement> announcementFactory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(announcementFactory);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await PublishOnceAsync(announcementFactory(), cancellationToken);
            await Task.Delay(options.AnnouncementInterval, cancellationToken);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!disposed)
        {
            disposed = true;
            client.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}

public sealed record DiscoveredLanDevice(DiscoveryAnnouncement Announcement, IPAddress Address);

public static class LanDiscoveryResults
{
    /// <summary>Uses the most recently received packet for a device ID, then sorts deterministically.</summary>
    public static IReadOnlyList<DiscoveredLanDevice> DeduplicateAndSort(IEnumerable<DiscoveredLanDevice> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var deduplicated = new Dictionary<string, DiscoveredLanDevice>(StringComparer.Ordinal);
        foreach (var device in devices)
        {
            ArgumentNullException.ThrowIfNull(device);
            deduplicated[device.Announcement.DeviceId] = device;
        }

        return deduplicated.Values
            .OrderBy(device => device.Announcement.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(device => device.Announcement.DeviceId, StringComparer.Ordinal)
            .ToArray();
    }
}
