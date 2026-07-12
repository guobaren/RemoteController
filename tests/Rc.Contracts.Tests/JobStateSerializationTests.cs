using System.Reflection;
using System.Text.Json;
using Xunit;

namespace Rc.Contracts.Tests;

public sealed class JobStateSerializationTests
{
    [Theory]
    [InlineData("Queued", "queued")]
    [InlineData("Running", "running")]
    [InlineData("Exited", "exited")]
    [InlineData("FailedToStart", "failed_to_start")]
    [InlineData("Cancelled", "cancelled")]
    [InlineData("InterruptedByReboot", "interrupted_by_reboot")]
    [InlineData("HostCrashed", "host_crashed")]
    public void JobStateSerializesToItsStableWireName(string memberName, string wireName)
    {
        var jobStateType = Assembly.Load("Rc.Contracts").GetType("Rc.Contracts.JobState");

        Assert.NotNull(jobStateType);

        var state = Enum.Parse(jobStateType!, memberName);
        var json = JsonSerializer.Serialize(state, jobStateType!, ContractJson.Options);

        Assert.Equal($"\"{wireName}\"", json);
    }
}
