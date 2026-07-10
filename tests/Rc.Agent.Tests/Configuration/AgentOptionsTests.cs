using System.Runtime.InteropServices;
using Rc.Agent.Configuration;
using Xunit;

namespace Rc.Agent.Tests.Configuration;

public sealed class AgentOptionsTests
{
    private static readonly string[] SupportedWindows = ["Windows 10", "Windows 11"];

    [Fact]
    public void DefaultsDescribeSupportedWindowsHostsAndOperationalLimits()
    {
        var options = new AgentOptions();

        Assert.Equal(SupportedWindows, options.SupportedOperatingSystems);
        Assert.Equal(Architecture.X64, options.RequiredArchitecture);
        Assert.Equal(8, options.NormalTaskLimit);
        Assert.Equal(2, options.ElevatedTaskLimit);
        Assert.Equal(200L * 1024 * 1024, options.LogQuotaBytes);
        Assert.Equal(TimeSpan.FromSeconds(10), options.CancellationGrace);
    }
}