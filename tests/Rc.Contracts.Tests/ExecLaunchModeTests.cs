using System.Reflection;
using System.Text.Json;
using Xunit;

namespace Rc.Contracts.Tests;

public sealed class ExecLaunchModeTests
{
    [Fact]
    public void DirectArgvAndShellLaunchesHaveMutuallyExclusiveWireShapes()
    {
        var assembly = Assembly.Load("Rc.Contracts");
        var requestType = assembly.GetType("Rc.Contracts.ExecRequest");
        var shellKindType = assembly.GetType("Rc.Contracts.ShellKind");

        Assert.NotNull(requestType);
        Assert.NotNull(shellKindType);

        var directFactory = requestType!.GetMethod("ForDirectArgv", BindingFlags.Public | BindingFlags.Static);
        var shellFactory = requestType.GetMethod("ForShell", BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(directFactory);
        Assert.NotNull(shellFactory);

        var direct = directFactory!.Invoke(null, [new List<string> { "tool.exe", "--name", "two words" }, null, null, null]);
        var powerShell = Enum.Parse(shellKindType!, "PowerShell");
        var shell = shellFactory!.Invoke(null, [powerShell, "Get-Process", null, null, null]);
        var directJson = JsonSerializer.Serialize(direct, requestType, ContractJson.Options);
        var shellJson = JsonSerializer.Serialize(shell, requestType, ContractJson.Options);

        Assert.Equal("{\"directArgv\":[\"tool.exe\",\"--name\",\"two words\"],\"workingDirectory\":null,\"environment\":null}", directJson);
        Assert.Equal("{\"shell\":{\"kind\":\"power_shell\",\"command\":\"Get-Process\"},\"workingDirectory\":null,\"environment\":null}", shellJson);
    }
}
