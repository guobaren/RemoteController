using System.Security.Cryptography;

namespace Rc.Agent.Security;

public interface ISecretProtector
{
    byte[] Protect(ReadOnlySpan<byte> sensitiveBytes);

    byte[] Unprotect(ReadOnlySpan<byte> protectedBytes);
}

public sealed class DpapiSecretProtector : ISecretProtector
{
    public byte[] Protect(ReadOnlySpan<byte> sensitiveBytes) =>
        ProtectedData.Protect(sensitiveBytes.ToArray(), optionalEntropy: null, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(ReadOnlySpan<byte> protectedBytes) =>
        ProtectedData.Unprotect(protectedBytes.ToArray(), optionalEntropy: null, DataProtectionScope.CurrentUser);
}