using System.Reflection;
using Xunit;

namespace Rc.Contracts.Tests;

public sealed class VersionedServiceContractsTests
{
    [Theory]
    [InlineData("Rc.Contracts.IPairServiceV1", "PairAsync", "Rc.Contracts.PairRequest", "Rc.Contracts.PairResponse")]
    [InlineData("Rc.Contracts.IExecServiceV1", "StartAsync", "Rc.Contracts.ExecRequest", "Rc.Contracts.ExecResponse")]
    [InlineData("Rc.Contracts.IJobServiceV1", "GetAsync", "Rc.Contracts.JobRequest", "Rc.Contracts.JobResponse")]
    [InlineData("Rc.Contracts.IFileServiceV1", "GetManifestAsync", "Rc.Contracts.FileManifestRequest", "Rc.Contracts.FileManifestResponse")]
    [InlineData("Rc.Contracts.IUiServiceV1", "GetSnapshotAsync", "Rc.Contracts.UiSnapshotRequest", "Rc.Contracts.UiSnapshotResponse")]
    public void VersionedServiceUsesAnExplicitRequestAndResponse(string serviceName, string methodName, string requestName, string responseName)
    {
        var assembly = Assembly.Load("Rc.Contracts");
        var service = assembly.GetType(serviceName);
        var request = assembly.GetType(requestName);
        var response = assembly.GetType(responseName);

        Assert.NotNull(service);
        Assert.NotNull(request);
        Assert.NotNull(response);

        var method = service!.GetMethod(methodName);
        Assert.NotNull(method);
        Assert.Equal(request, method!.GetParameters()[0].ParameterType);
        Assert.Equal(typeof(ValueTask<>).MakeGenericType(response!), method.ReturnType);
    }
}
