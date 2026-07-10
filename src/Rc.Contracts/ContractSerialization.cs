using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rc.Contracts;

public static class ContractJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters =
        {
            new ResultEnvelopeJsonConverterFactory(),
            new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false),
        },
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
    private ResultEnvelope(bool ok, bool hasResult, T? result, RemoteError? error)
    {
        if (ok && error is not null)
        {
            throw new ArgumentException("A successful result cannot contain an error.", nameof(error));
        }

        if (ok && !hasResult)
        {
            throw new ArgumentException("A successful result must contain a result.", nameof(hasResult));
        }

        if (!ok && hasResult)
        {
            throw new ArgumentException("A failed result cannot contain a result.", nameof(result));
        }

        if (!ok && error is null)
        {
            throw new ArgumentNullException(nameof(error), "A failed result must contain an error.");
        }

        Ok = ok;
        HasResult = hasResult;
        Result = result;
        Error = error;
    }

    [JsonPropertyName("ok")]
    public bool Ok { get; }

    [JsonIgnore]
    public bool HasResult { get; }

    public T? Result { get; }

    public RemoteError? Error { get; }

    internal static ResultEnvelope<T> FromWire(bool ok, bool hasResult, T? result, RemoteError? error) =>
        new(ok, hasResult, result, error);
}

public static class Result
{
    public static ResultEnvelope<T> Success<T>(T value) => ResultEnvelope<T>.FromWire(true, true, value, null);

    public static ResultEnvelope<T> Failure<T>(RemoteError error) => ResultEnvelope<T>.FromWire(false, false, default, error);
}

internal sealed class ResultEnvelopeJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(ResultEnvelope<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var resultType = typeToConvert.GetGenericArguments()[0];
        var converterType = typeof(ResultEnvelopeJsonConverter<>).MakeGenericType(resultType);
        return (JsonConverter)Activator.CreateInstance(converterType)!;
    }

    private sealed class ResultEnvelopeJsonConverter<T> : JsonConverter<ResultEnvelope<T>>
    {
        public override ResultEnvelope<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw new JsonException("A result envelope must be an object.");
            }

            bool? ok = null;
            var hasResult = false;
            T? result = default;
            RemoteError? error = null;

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    throw new JsonException("Invalid result envelope property.");
                }

                var propertyName = reader.GetString();
                if (!reader.Read())
                {
                    throw new JsonException("Invalid result envelope property.");
                }

                switch (propertyName)
                {
                    case "ok":
                        ok = reader.GetBoolean();
                        break;
                    case "result":
                        result = JsonSerializer.Deserialize<T>(ref reader, options);
                        hasResult = true;
                        break;
                    case "error":
                        error = JsonSerializer.Deserialize<RemoteError>(ref reader, options);
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }

            if (ok is null)
            {
                throw new JsonException("A result envelope must contain ok.");
            }

            return ResultEnvelope<T>.FromWire(ok.Value, hasResult, result, error);
        }

        public override void Write(Utf8JsonWriter writer, ResultEnvelope<T> value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", value.Ok);

            if (value.HasResult)
            {
                writer.WritePropertyName("result");
                JsonSerializer.Serialize(writer, value.Result, options);
            }

            if (value.Error is not null)
            {
                writer.WritePropertyName("error");
                JsonSerializer.Serialize(writer, value.Error, options);
            }

            writer.WriteEndObject();
        }
    }
}
