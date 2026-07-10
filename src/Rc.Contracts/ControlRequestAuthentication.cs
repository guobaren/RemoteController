using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Rc.Contracts;

/// <summary>
/// Canonical signing and verification for privileged control requests. The agent
/// certificate pin protects the transport; this proof binds the command to the
/// controller identity that was established during J-PAKE pairing.
/// </summary>
public static class ControlRequestAuthentication
{
    private static readonly byte[] DomainSeparator = "RemoteController/exec_once/v1"u8.ToArray();

    public static byte[] SignExecuteOnce(
        string agentDeviceId,
        string controllerId,
        ExecRequest execution,
        ECDsa privateKey)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        var payloadHash = ComputeExecuteOncePayloadHash(agentDeviceId, controllerId, execution);
        try
        {
            return privateKey.SignHash(payloadHash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payloadHash);
        }
    }

    public static bool VerifyExecuteOnce(
        string agentDeviceId,
        string controllerId,
        ExecRequest execution,
        ReadOnlySpan<byte> signature,
        ECDsa publicKey)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        var payloadHash = ComputeExecuteOncePayloadHash(agentDeviceId, controllerId, execution);
        try
        {
            return publicKey.VerifyHash(payloadHash, signature);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payloadHash);
        }
    }

    private static byte[] ComputeExecuteOncePayloadHash(string agentDeviceId, string controllerId, ExecRequest execution)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentDeviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(controllerId);
        ArgumentNullException.ThrowIfNull(execution);

        var executionJson = JsonSerializer.SerializeToUtf8Bytes(execution, ContractJson.Options);
        try
        {
            using var stream = new MemoryStream();
            WriteField(stream, DomainSeparator);
            WriteField(stream, Encoding.UTF8.GetBytes(agentDeviceId));
            WriteField(stream, Encoding.UTF8.GetBytes(controllerId));
            WriteField(stream, executionJson);
            return SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(executionJson);
        }
    }

    private static void WriteField(Stream stream, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        stream.Write(length);
        stream.Write(value);
    }
}
