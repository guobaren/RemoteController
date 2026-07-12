using Rc.Agent.Persistence;
using Rc.Agent.Security;
using Rc.Contracts;
using Xunit;

namespace Rc.Agent.Tests.Persistence;

public sealed class AuditAndPairingSecurityTests
{
    [Fact]
    public async Task AuditEventsRoundTripAndQuotaEvictsOldestEvents()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(directory.Path);
        await store.InitializeAsync();

        for (var index = 0; index < 4; index++)
        {
            await store.AppendAuditEventAsync(new AgentAuditEvent(
                $"event-{index}",
                DateTimeOffset.UtcNow.AddSeconds(index),
                "job.test",
                "controller-a",
                $"job-{index}",
                index % 2 == 0,
                index % 2 == 0 ? null : ErrorCode.FailedPrecondition,
                new Dictionary<string, string> { ["index"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
        }

        var all = await store.ListAuditEventsAsync();
        Assert.Equal(4, all.Count);
        Assert.Equal("event-3", all[0].EventId);
        Assert.Equal("controller-a", all[0].ControllerId);

        var retainedBytes = await store.EnforceAuditQuotaAsync(260);
        var retained = await store.ListAuditEventsAsync();
        Assert.True(retainedBytes <= 260);
        Assert.NotEmpty(retained);
        Assert.DoesNotContain(retained, item => item.EventId == "event-0");
    }

    [Fact]
    public async Task PairingFailuresPersistCooldownAndUnpairAdvancesGenerationWithAudit()
    {
        using var directory = new TemporaryDirectory();
        long generation;
        await using (var store = new AgentStateStore(directory.Path))
        {
            await store.InitializeAsync();
            var now = DateTimeOffset.UtcNow;
            await store.RecordPairingFailureAsync(now, TimeSpan.FromMinutes(5), 2, TimeSpan.FromMinutes(15));
            var blocked = await store.RecordPairingFailureAsync(now.AddSeconds(1), TimeSpan.FromMinutes(5), 2, TimeSpan.FromMinutes(15));
            Assert.NotNull(blocked.BlockedUntilUtc);
            generation = blocked.Generation;

            await store.SavePairedControllerAsync(new PairedController("controller-a", [1, 2, 3], now));
            await store.RemovePairedControllerAsync();
            Assert.Null(await store.GetPairedControllerAsync());
        }

        await using var reopened = new AgentStateStore(directory.Path);
        await reopened.InitializeAsync();
        var persisted = await reopened.GetPairingSecurityStateAsync();
        Assert.Equal(generation + 1, persisted.Generation);
        Assert.NotNull(persisted.BlockedUntilUtc);
        Assert.Contains(await reopened.ListAuditEventsAsync(), item => item.EventType == "pairing.unpaired" && item.ControllerId == "controller-a");
    }

    [Fact]
    public async Task LocalAdminUnpairIsIdempotentAndClearsPairingCooldown()
    {
        using var directory = new TemporaryDirectory();
        await using (var store = new AgentStateStore(directory.Path))
        {
            await store.InitializeAsync();
            await store.SavePairedControllerAsync(new PairedController("controller-local", [4, 5, 6], DateTimeOffset.UtcNow));
            await store.RecordPairingFailureAsync(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5), 1, TimeSpan.FromMinutes(15));
        }

        using var output = new StringWriter();
        using var error = new StringWriter();
        var first = await LocalAdminCommand.TryRunAsync(["unpair"], directory.Path, output, error);
        var second = await LocalAdminCommand.TryRunAsync(["unpair"], directory.Path, output, error);

        Assert.Equal(0, first);
        Assert.Equal(0, second);
        Assert.Equal(string.Empty, error.ToString());
        await using var reopened = new AgentStateStore(directory.Path);
        await reopened.InitializeAsync();
        Assert.Null(await reopened.GetPairedControllerAsync());
        Assert.Null((await reopened.GetPairingSecurityStateAsync()).BlockedUntilUtc);
    }
}
