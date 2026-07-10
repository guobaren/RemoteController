using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rc.Contracts;

public static class ContractJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
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

public sealed record ResultEnvelope<T>(bool Succeeded, T? Value, RemoteError? Error);

public static class Result
{
    public static ResultEnvelope<T> Success<T>(T value) => new(true, value, null);

    public static ResultEnvelope<T> Failure<T>(RemoteError error) => new(false, default, error);
}
