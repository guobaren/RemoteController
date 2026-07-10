namespace Rc.Contracts;

public sealed record PairRequest(string ClientName, byte[] ClientCertificateRequest);

public sealed record PairResponse(string DeviceId, byte[] ClientCertificate, byte[] CertificateAuthority);
