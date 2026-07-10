using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Rc.Cli.Security;

public sealed class ControllerIdentity : IDisposable
{
    private const string IdentityFileName = "controller-identity.dpapi";
    private readonly X509Certificate2 certificate;

    private ControllerIdentity(string controllerId, X509Certificate2 certificate)
    {
        ControllerId = controllerId;
        this.certificate = certificate;
    }

    public string ControllerId { get; }

    public byte[] Certificate => certificate.Export(X509ContentType.Cert);

    public ECDsa GetPrivateKey() => certificate.GetECDsaPrivateKey()
        ?? throw new CryptographicException("The controller identity does not have an ECDSA private key.");

    public static async Task<ControllerIdentity> LoadOrCreateAsync(
        string controllerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(controllerId);
        var root = Environment.GetEnvironmentVariable("RC_CONTROLLER_DATA_ROOT")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteController");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, IdentityFileName);

        if (File.Exists(path))
        {
            var protectedPayload = await File.ReadAllBytesAsync(path, cancellationToken);
            var payload = ProtectedData.Unprotect(protectedPayload, optionalEntropy: null, DataProtectionScope.CurrentUser);
            try
            {
                var saved = JsonSerializer.Deserialize<SavedIdentity>(payload)
                    ?? throw new CryptographicException("The saved controller identity is invalid.");
                var restoredCertificate = new X509Certificate2(
                    saved.Pkcs12,
                    string.Empty,
                    X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
                return new ControllerIdentity(saved.ControllerId, restoredCertificate);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(payload);
            }
        }

        using var privateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(
            $"CN=RemoteController Controller {controllerId}", privateKey, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.2") }, true));
        var now = DateTimeOffset.UtcNow;
        using var generated = request.CreateSelfSigned(now.AddMinutes(-1), now.AddYears(2));
        var pkcs12 = generated.Export(X509ContentType.Pkcs12);
        var savedIdentity = new SavedIdentity(controllerId, pkcs12);
        var serialized = JsonSerializer.SerializeToUtf8Bytes(savedIdentity);
        try
        {
            var protectedPayload = ProtectedData.Protect(serialized, optionalEntropy: null, DataProtectionScope.CurrentUser);
            try
            {
                await File.WriteAllBytesAsync(path, protectedPayload, cancellationToken);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedPayload);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(serialized);
            CryptographicOperations.ZeroMemory(pkcs12);
        }

        var loadedCertificate = new X509Certificate2(
            generated.Export(X509ContentType.Pkcs12),
            string.Empty,
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
        return new ControllerIdentity(controllerId, loadedCertificate);
    }

    public void Dispose() => certificate.Dispose();

    private sealed record SavedIdentity(string ControllerId, byte[] Pkcs12);
}