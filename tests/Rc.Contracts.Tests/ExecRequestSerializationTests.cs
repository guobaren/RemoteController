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

        var directFactory = requestType!.GetMethod("ForDirectArgv", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(directFactory);

        var argv = new[] { "tool.exe", "--display-name", "two words" };
        var request = directFactory!.Invoke(null, [argv, null, null, null]);
        var json = JsonSerializer.Serialize(request, requestType!, ContractJson.Options);
        var roundTripped = JsonSerializer.Deserialize(json, requestType!, ContractJson.Options);
        var returnedArgv = (IReadOnlyList<string>?)requestType.GetProperty("DirectArgv")?.GetValue(roundTripped);

        Assert.Equal("{\"directArgv\":[\"tool.exe\",\"--display-name\",\"two words\"],\"workingDirectory\":null,\"environment\":null}", json);
        Assert.Equal(argv, returnedArgv);
        Assert.DoesNotContain("tool.exe --display-name two words", json, StringComparison.Ordinal);
    }
}
