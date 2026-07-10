using System.Text.Json;
using Xunit;

namespace Rc.Contracts.Tests;

public sealed class TaskLaunchRequestTests
{
    [Fact]
    public void LaunchRequestHasStableWireFormatAndDefensiveEnvironmentCopy()
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["RC_MODE"] = "safe",
        };
        var request = new TaskLaunchRequest(
            "job-1",
            ExecRequest.ForDirectArgv(["cmd.exe", "/c", "echo hello"]),
            ExecutionIdentity.CurrentUser,
            "C:\\agent-data",
            "rc-job-1",
            TimeSpan.FromSeconds(10),
            environment);
        environment["RC_MODE"] = "changed";

        var json = JsonSerializer.Serialize(request, ContractJson.Options);

        Assert.Equal("{\"jobId\":\"job-1\",\"execution\":{\"directArgv\":[\"cmd.exe\",\"/c\",\"echo hello\"],\"workingDirectory\":null,\"environment\":null},\"executionIdentity\":\"current_user\",\"dataRoot\":\"C:\\\\agent-data\",\"controlPipeName\":\"rc-job-1\",\"cancellationGracePeriod\":\"00:00:10\",\"environment\":{\"RC_MODE\":\"safe\"}}", json);
        var mutable = Assert.IsAssignableFrom<IDictionary<string, string>>(request.Environment!);
        Assert.Throws<NotSupportedException>(() => mutable["RC_MODE"] = "unsafe");
    }

    [Fact]
    public void StandardInputMessagesDefensivelyCopyBytes()
    {
        var input = new byte[] { 1, 2 };
        var message = new TaskControlMessage(TaskControlKind.StandardInput, input);
        input[0] = 9;

        Assert.Equal(new byte[] { 1, 2 }, message.Data);
        Assert.Throws<ArgumentException>(() => new TaskControlMessage(TaskControlKind.Cancel, [1]));
        Assert.Throws<ArgumentException>(() => new TaskControlMessage(TaskControlKind.StandardInput));
    }
}
