using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Rc.Agent.Persistence;

namespace Rc.Agent.Security;

public sealed class AgentTlsIdentity : IDisposable
{
    public AgentTlsIdentity(string deviceId, X509Certificate2 certificate, DateTimeOffset createdAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentNullException.ThrowIfNull(certificate);

        if (!certificate.HasPrivateKey)
        {
            throw new ArgumentException("The agent TLS certificate must include its private key.", nameof(certificate));
        }

        DeviceId = deviceId;
        Certificate = certificate;
        CreatedAtUtc = createdAtUtc;
        CertificateSha256Fingerprint = Convert.ToHexString(SHA256.HashData(certificate.RawData));
    }

    public string DeviceId { get; }

    public X509Certificate2 Certificate { get; }

    public string CertificateSha256Fingerprint { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public void Dispose() => Certificate.Dispose();
}

/// <summary>
/// Creates the self-signed TLS identity used by the agent and restores it on later starts.
/// The state store protects the private material with DPAPI before it reaches disk.
/// </summary>
public sealed class AgentCertificateManager
{
    private const string TlsServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";
    private readonly AgentStateStore stateStore;

    public AgentCertificateManager(AgentStateStore stateStore)
    {
        this.stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
    }

    public async Task<AgentTlsIdentity> GetOrCreateAsync(CancellationToken cancellationToken = default)
    {
        var persistedIdentity = await stateStore.GetDeviceIdentityAsync(cancellationToken);
        if (persistedIdentity is not null)
        {
            return Restore(persistedIdentity);
        }

        var now = DateTimeOffset.UtcNow;
        var deviceId = Guid.NewGuid().ToString("N");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(
            $"CN=Remote Controller Agent {deviceId}",
            key,
            HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid(TlsServerAuthenticationOid) },
            critical: false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        using var generated = request.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(10));
        var persisted = new DeviceIdentity(
            deviceId,
            generated.Export(X509ContentType.Cert),
            key.ExportPkcs8PrivateKey(),
            now);
        await stateStore.SaveDeviceIdentityAsync(persisted, cancellationToken);

        return Restore(persisted);
    }

    private static AgentTlsIdentity Restore(DeviceIdentity persistedIdentity)
    {
        try
        {
            using var certificate = new X509Certificate2(persistedIdentity.Certificate);
            using var privateKey = ECDsa.Create();
            privateKey.ImportPkcs8PrivateKey(persistedIdentity.PrivateKey, out var bytesRead);
            if (bytesRead != persistedIdentity.PrivateKey.Length || privateKey.KeySize != 256)
            {
                throw new CryptographicException("The persisted agent private key is not a P-256 PKCS#8 key.");
            }

            using var publicKey = certificate.GetECDsaPublicKey();
            if (publicKey is null || publicKey.KeySize != 256)
            {
                throw new CryptographicException("The persisted agent certificate does not contain a P-256 public key.");
            }

            // Schannel cannot use the ephemeral CNG key produced by CopyWithPrivateKey
            // directly for an ECDSA TLS server certificate. Round-trip through a PFX and
            // import it into the current user's persisted key store before giving it to
            // SslStream. The PFX only exists in memory; the durable source of truth stays
            // DPAPI-protected in AgentStateStore.
            using var certificateWithPrivateKey = certificate.CopyWithPrivateKey(privateKey);
            var pkcs12 = certificateWithPrivateKey.Export(X509ContentType.Pkcs12);
            try
            {
                var tlsCertificate = new X509Certificate2(
                    pkcs12,
                    string.Empty,
                    X509KeyStorageFlags.UserKeySet |
                    X509KeyStorageFlags.PersistKeySet |
                    X509KeyStorageFlags.Exportable);
                return new AgentTlsIdentity(persistedIdentity.DeviceId, tlsCertificate, persistedIdentity.CreatedAtUtc);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pkcs12);
            }
        }
        catch (CryptographicException exception)
        {
            throw new InvalidOperationException("The persisted agent TLS identity is invalid and must not be replaced automatically.", exception);
        }
    }
}
