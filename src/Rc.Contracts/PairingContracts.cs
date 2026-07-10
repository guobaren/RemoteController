namespace Rc.Contracts;

public sealed class PairRequest
{
    private readonly byte[] clientCertificateRequest;

    public PairRequest(string clientName, byte[] clientCertificateRequest)
    {
        ClientName = clientName;
        this.clientCertificateRequest = clientCertificateRequest.ToArray();
    }

    public string ClientName { get; }

    public byte[] ClientCertificateRequest => clientCertificateRequest.ToArray();
}

public sealed class PairResponse
{
    private readonly byte[] clientCertificate;
    private readonly byte[] certificateAuthority;

    public PairResponse(string deviceId, byte[] clientCertificate, byte[] certificateAuthority)
    {
        DeviceId = deviceId;
        this.clientCertificate = clientCertificate.ToArray();
        this.certificateAuthority = certificateAuthority.ToArray();
    }

    public string DeviceId { get; }

    public byte[] ClientCertificate => clientCertificate.ToArray();

    public byte[] CertificateAuthority => certificateAuthority.ToArray();
}
