using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Rc.Agent.Ui;
using Rc.Contracts;
using Xunit;

namespace Rc.Agent.Tests.Ui;

public sealed class UiAgentControlClientTests
{
    [Fact]
    public async Task SendsVersionedCommandAndReturnsDetachedResult()
    {
        var pipeName = "rc-ui-agent-test-" + Guid.NewGuid().ToString("N");
        var snapshot = new UiSessionSnapshot(42, "tester", true, [], []);
        var registration = new UiAgentRegistration(1, 1, pipeName, snapshot);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = Task.Run(async () =>
        {
            await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await server.WaitForConnectionAsync(cancellation.Token);
            using var reader = new StreamReader(server, new UTF8Encoding(false), false, 1024 * 1024, leaveOpen: true);
            await using var writer = new StreamWriter(server, new UTF8Encoding(false), 1024 * 1024, leaveOpen: true) { AutoFlush = true };
            var command = JsonSerializer.Deserialize<UiAgentCommandRequest>((await reader.ReadLineAsync(cancellation.Token))!, ContractJson.Options);
            Assert.NotNull(command);
            Assert.Equal(UiOperationKinds.Snapshot, command!.Operation);
            await writer.WriteLineAsync(JsonSerializer.Serialize(Result.Success(new UiAgentCommandResponse(
                JsonSerializer.SerializeToElement(new UiSnapshotResponse(snapshot), ContractJson.Options))), ContractJson.Options));
        }, cancellation.Token);

        var result = await UiAgentControlClient.SendAsync(registration, UiOperationKinds.Snapshot,
            JsonSerializer.SerializeToElement(new UiSnapshotRequest(false), ContractJson.Options), cancellation.Token);

        Assert.Equal(42, result.Deserialize<UiSnapshotResponse>(ContractJson.Options)!.Session.SessionId);
        await serverTask;
    }
}
