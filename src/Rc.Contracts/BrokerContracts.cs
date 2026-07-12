using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Rc.Contracts;

public sealed record BrokerLaunchRequest(
    int ProtocolVersion,
    string RequestId,
    DateTimeOffset IssuedAtUtc,
    byte[] Nonce,
    TaskLaunchRequest Launch,
    byte[] AuthenticationTag);

public sealed record BrokerLaunchResponse(
    bool Accepted,
    TaskRuntimeStatus? Status,
    RemoteError? Error = null);

public static class BrokerRequestAuthentication
{
    public const int ProtocolVersion = 1;
    public static readonly TimeSpan MaximumClockSkew = TimeSpan.FromMinutes(2);

    public static byte[] SignLaunch(
        string requestId,
        DateTimeOffset issuedAtUtc,
        ReadOnlySpan<byte> nonce,
        TaskLaunchRequest launch,
        ReadOnlySpan<byte> secret)
    {
        if (secret.Length < 32)
        {
            throw new ArgumentException("The broker authentication secret must contain at least 32 bytes.", nameof(secret));
        }

        using var hmac = new HMACSHA256(secret.ToArray());
        return hmac.ComputeHash(BuildLaunchPayload(requestId, issuedAtUtc, nonce, launch));
    }

    public static bool VerifyLaunch(BrokerLaunchRequest request, ReadOnlySpan<byte> secret, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ProtocolVersion != ProtocolVersion ||
            string.IsNullOrWhiteSpace(request.RequestId) ||
            request.Nonce is not { Length: >= 16 } ||
            request.AuthenticationTag is not { Length: 32 } ||
            (nowUtc - request.IssuedAtUtc).Duration() > MaximumClockSkew)
        {
            return false;
        }

        var expected = SignLaunch(request.RequestId, request.IssuedAtUtc, request.Nonce, request.Launch, secret);
        return CryptographicOperations.FixedTimeEquals(expected, request.AuthenticationTag);
    }

    private static byte[] BuildLaunchPayload(string requestId, DateTimeOffset issuedAtUtc, ReadOnlySpan<byte> nonce, TaskLaunchRequest launch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentNullException.ThrowIfNull(launch);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(ProtocolVersion);
        writer.Write(requestId);
        writer.Write(issuedAtUtc.ToUnixTimeMilliseconds());
        writer.Write(nonce.Length);
        writer.Write(nonce);
        var launchJson = JsonSerializer.SerializeToUtf8Bytes(launch, ContractJson.Options);
        writer.Write(launchJson.Length);
        writer.Write(launchJson);
        writer.Flush();
        return stream.ToArray();
    }
}
