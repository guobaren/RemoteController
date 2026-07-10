using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rc.Contracts;

public static class ContractJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) },
    };
}

public enum ErrorCode
{
    Unknown,
    InvalidRequest,
    Unauthenticated,
    Unauthorized,
    NotFound,
    Conflict,
    Unavailable,
    Timeout,
    Internal,
    Cancelled,
    FailedPrecondition,
    ResourceExhausted,
}

public sealed record RemoteError(ErrorCode Code, string Message, bool Retryable);

public sealed class ResultEnvelope<T>
{
    [JsonConstructor]
    public ResultEnvelope(bool ok, T? result, RemoteError? error)
    {
        if (ok && error is not null)
        {
            throw new ArgumentException("A successful result cannot contain an error.", nameof(error));
        }

        if (!ok && result is not null)
        {
            throw new ArgumentException("A failed result cannot contain a result.", nameof(result));
        }

        if (!ok && error is null)
        {
            throw new ArgumentNullException(nameof(error), "A failed result must contain an error.");
        }

        Ok = ok;
        Result = result;
        Error = error;
    }

    [JsonPropertyName("ok")]
    public bool Ok { get; }

    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public T? Result { get; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RemoteError? Error { get; }
}

public static class Result
{
    public static ResultEnvelope<T> Success<T>(T value) => new(true, value, null);

    public static ResultEnvelope<T> Failure<T>(RemoteError error) => new(false, default, error);
}
