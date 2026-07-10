using System.Reflection;
using System.Text.Json;
using Xunit;

namespace Rc.Contracts.Tests;

public sealed class ExecRequestSerializationTests
{
    [Fact]
    public void ExecRequestRoundTripsArgvAsAnArrayWithoutCombiningArguments()
    {
        var contracts = Assembly.Load("Rc.Contracts");
        var requestType = contracts.GetType("Rc.Contracts.ExecRequest");

        Assert.NotNull(requestType);

        var argv = new[] { "tool.exe", "--display-name", "two words" };
        var request = Activator.CreateInstance(requestType!, [argv, null, null]);
        var json = JsonSerializer.Serialize(request, requestType!, ContractJson.Options);
        var roundTripped = JsonSerializer.Deserialize(json, requestType!, ContractJson.Options);
        var returnedArgv = (string[]?)requestType!.GetProperty("Argv")?.GetValue(roundTripped);

        Assert.Equal("{\"argv\":[\"tool.exe\",\"--display-name\",\"two words\"],\"workingDirectory\":null,\"environment\":null}", json);
        Assert.Equal(argv, returnedArgv);
        Assert.DoesNotContain("tool.exe --display-name two words", json, StringComparison.Ordinal);
    }
}
