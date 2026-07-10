using System.Reflection;
using System.Text.Json;
using Xunit;

namespace Rc.Contracts.Tests;

public sealed class ResultEnvelopeSerializationTests
{
    [Fact]
    public void SuccessEnvelopeSerializesWithCamelCasePropertyNames()
    {
        var contracts = Assembly.Load("Rc.Contracts");
        var envelopeDefinition = contracts.GetType("Rc.Contracts.ResultEnvelope`1");
        var jsonOptionsType = contracts.GetType("Rc.Contracts.ContractJson");

        Assert.NotNull(envelopeDefinition);
        Assert.NotNull(jsonOptionsType);

        var envelopeType = envelopeDefinition!.MakeGenericType(typeof(string));
        var options = (JsonSerializerOptions?)jsonOptionsType!.GetProperty("Options", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);

        Assert.NotNull(options);

        var envelope = Activator.CreateInstance(envelopeType, [true, "ready", null]);
        var json = JsonSerializer.Serialize(envelope, envelopeType, options);

        Assert.Equal("{\"succeeded\":true,\"value\":\"ready\",\"error\":null}", json);
    }

    [Fact]
    public void RetryableErrorSerializesWithCamelCasePropertyNames()
    {
        var contracts = Assembly.Load("Rc.Contracts");
        var resultType = contracts.GetType("Rc.Contracts.Result");

        Assert.NotNull(resultType);

        var failure = resultType!.GetMethod("Failure", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(failure);

        var remoteError = new RemoteError(ErrorCode.Unavailable, "agent is offline", true);
        var envelope = failure!.MakeGenericMethod(typeof(string)).Invoke(null, [remoteError]);
        var json = JsonSerializer.Serialize(envelope, ContractJson.Options);

        Assert.Equal("{\"succeeded\":false,\"value\":null,\"error\":{\"code\":\"unavailable\",\"message\":\"agent is offline\",\"retryable\":true}}", json);
    }
}
