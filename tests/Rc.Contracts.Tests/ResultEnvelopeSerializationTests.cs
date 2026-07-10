using System.Text.Json;
using Xunit;

namespace Rc.Contracts.Tests;

public sealed class ResultEnvelopeSerializationTests
{
    [Fact]
    public void SuccessEnvelopeSerializesAsOkAndResultWithoutError()
    {
        var json = JsonSerializer.Serialize(Result.Success("ready"), ContractJson.Options);

        Assert.Equal("{\"ok\":true,\"result\":\"ready\"}", json);
        Assert.DoesNotContain("error", json, StringComparison.Ordinal);
    }

    [Fact]
    public void RetryableErrorSerializesAsOkFalseWithoutResult()
    {
        var remoteError = new RemoteError(ErrorCode.Unavailable, "agent is offline", true);
        var json = JsonSerializer.Serialize(Result.Failure<string>(remoteError), ContractJson.Options);

        Assert.Equal("{\"ok\":false,\"error\":{\"code\":\"unavailable\",\"message\":\"agent is offline\",\"retryable\":true}}", json);
        Assert.DoesNotContain("result", json, StringComparison.Ordinal);
    }
}
