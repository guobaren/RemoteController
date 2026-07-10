using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Rc.Agent.Persistence;

namespace Rc.Agent.Security;

public sealed record PairingCoordinatorOptions
{
    public TimeSpan InvitationLifetime { get; init; } = TimeSpan.FromMinutes(2);

    public int MaxFailedAttempts { get; init; } = 3;

    public int OneTimeCodeLength { get; init; } = 10;

    internal void Validate()
    {
        if (InvitationLifetime <= TimeSpan.Zero || InvitationLifetime > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(InvitationLifetime));
        }

        if (MaxFailedAttempts is < 1 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxFailedAttempts));
        }

        if (OneTimeCodeLength is < 6 or > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(OneTimeCodeLength));
        }
    }
}

public sealed class PairingEndpoint
{
    private readonly byte[] addressBytes;

    public PairingEndpoint(IPAddress address, int port)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        addressBytes = address.MapToIPv6().GetAddressBytes();
        Port = port;
    }

    public IPAddress Address => new(addressBytes);

    public int Port { get; }

    internal byte[] CanonicalAddressBytes => addressBytes.ToArray();

    /// <summary>
    /// Parses the manual pairing fallback address. Host names are intentionally not
    /// accepted because pairing transcripts bind to a concrete IP endpoint.
    /// </summary>
    public static bool TryParse(string? value, [NotNullWhen(true)] out PairingEndpoint? endpoint)
    {
        endpoint = null;
        if (string.IsNullOrWhiteSpace(value) || !IPEndPoint.TryParse(value, out var parsed))
        {
            return false;
        }

        try
        {
            endpoint = new PairingEndpoint(parsed.Address, parsed.Port);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}

/// <summary>
/// Local-only pairing material. The one-time code must never be put into discovery
/// broadcasts or regular RPC payloads.
/// </summary>
public sealed class PairingInvitation
{
    private readonly byte[] agentCertificateFingerprint;
    private readonly byte[] agentSpkiFingerprint;

    internal PairingInvitation(
        Guid pairingId,
        string oneTimeCode,
        string agentDeviceId,
        PairingEndpoint agentEndpoint,
        byte[] agentCertificateFingerprint,
        byte[] agentSpkiFingerprint,
        DateTimeOffset expiresAtUtc)
    {
        PairingId = pairingId;
        OneTimeCode = oneTimeCode;
        AgentDeviceId = agentDeviceId;
        AgentEndpoint = agentEndpoint;
        this.agentCertificateFingerprint = agentCertificateFingerprint.ToArray();
        this.agentSpkiFingerprint = agentSpkiFingerprint.ToArray();
        ExpiresAtUtc = expiresAtUtc;
    }

    public Guid PairingId { get; }

    public string OneTimeCode { get; }

    public string AgentDeviceId { get; }

    public PairingEndpoint AgentEndpoint { get; }

    public byte[] AgentCertificateFingerprint => agentCertificateFingerprint.ToArray();

    public byte[] AgentSpkiFingerprint => agentSpkiFingerprint.ToArray();

    public DateTimeOffset ExpiresAtUtc { get; }
}

/// <summary>
/// Public metadata which both participants hash with <see cref="PairingTranscript"/>.
/// </summary>
public sealed class PairingBinding
{
    private readonly byte[] agentCertificateFingerprint;
    private readonly byte[] agentSpkiFingerprint;
    private readonly byte[] controllerCertificateFingerprint;
    private readonly byte[] controllerSpkiFingerprint;

    internal PairingBinding(
        Guid pairingId,
        string agentDeviceId,
        string controllerId,
        PairingEndpoint agentEndpoint,
        byte[] agentCertificateFingerprint,
        byte[] agentSpkiFingerprint,
        byte[] controllerCertificateFingerprint,
        byte[] controllerSpkiFingerprint)
    {
        PairingId = pairingId;
        AgentDeviceId = agentDeviceId;
        ControllerId = controllerId;
        AgentEndpoint = agentEndpoint;
        this.agentCertificateFingerprint = agentCertificateFingerprint.ToArray();
        this.agentSpkiFingerprint = agentSpkiFingerprint.ToArray();
        this.controllerCertificateFingerprint = controllerCertificateFingerprint.ToArray();
        this.controllerSpkiFingerprint = controllerSpkiFingerprint.ToArray();
    }

    public Guid PairingId { get; }

    public string AgentDeviceId { get; }

    public string ControllerId { get; }

    public PairingEndpoint AgentEndpoint { get; }

    public byte[] AgentCertificateFingerprint => agentCertificateFingerprint.ToArray();

    public byte[] AgentSpkiFingerprint => agentSpkiFingerprint.ToArray();

    public byte[] ControllerCertificateFingerprint => controllerCertificateFingerprint.ToArray();

    public byte[] ControllerSpkiFingerprint => controllerSpkiFingerprint.ToArray();
}

public sealed class PairingCompletionProof
{
    private readonly byte[] confirmationMac;
    private readonly byte[] certificateSignature;

    public PairingCompletionProof(byte[] confirmationMac, byte[] certificateSignature)
    {
        ArgumentNullException.ThrowIfNull(confirmationMac);
        ArgumentNullException.ThrowIfNull(certificateSignature);
        this.confirmationMac = confirmationMac.ToArray();
        this.certificateSignature = certificateSignature.ToArray();
    }

    public byte[] ConfirmationMac => confirmationMac.ToArray();

    public byte[] CertificateSignature => certificateSignature.ToArray();

    public static PairingCompletionProof Create(
        PairingPakeResult sessionResult,
        PairingBinding binding,
        ECDsa controllerPrivateKey)
    {
        ArgumentNullException.ThrowIfNull(sessionResult);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(controllerPrivateKey);

        var payload = PairingConfirmation.BuildPayload(binding);
        var sessionKey = sessionResult.SessionKey;
        try
        {
            using var mac = new HMACSHA256(sessionKey);
            return new PairingCompletionProof(
                mac.ComputeHash(payload),
                controllerPrivateKey.SignData(payload, HashAlgorithmName.SHA256));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            CryptographicOperations.ZeroMemory(sessionKey);
        }
    }
}

/// <summary>
/// Fixed-length-prefixed binary transcript builder. It avoids ambiguity from manually
/// concatenated strings and canonicalizes IPv4 endpoints to IPv4-mapped IPv6.
/// </summary>
public static class PairingTranscript
{
    public const int ProtocolVersion = 1;

    private static readonly byte[] DomainSeparator = Encoding.UTF8.GetBytes("rc/pairing/transcript/v1");

    public static byte[] ComputeHash(PairingBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        using var stream = new MemoryStream();
        WriteField(stream, DomainSeparator);
        WriteUInt32(stream, ProtocolVersion);
        WriteField(stream, binding.PairingId.ToByteArray());
        WriteString(stream, binding.AgentDeviceId);
        WriteString(stream, binding.ControllerId);
        WriteField(stream, binding.AgentEndpoint.CanonicalAddressBytes);
        WriteUInt16(stream, checked((ushort)binding.AgentEndpoint.Port));
        WriteFingerprint(stream, binding.AgentCertificateFingerprint);
        WriteFingerprint(stream, binding.AgentSpkiFingerprint);
        WriteFingerprint(stream, binding.ControllerCertificateFingerprint);
        WriteFingerprint(stream, binding.ControllerSpkiFingerprint);
        return SHA256.HashData(stream.ToArray());
    }

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value.Normalize(NormalizationForm.FormC));
        try
        {
            WriteField(stream, bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void WriteFingerprint(Stream stream, byte[] value)
    {
        if (value.Length != 32)
        {
            throw new ArgumentException("A pairing fingerprint must be SHA-256 sized.");
        }

        WriteField(stream, value);
    }

    private static void WriteField(Stream stream, byte[] value)
    {
        WriteUInt32(stream, checked((uint)value.Length));
        stream.Write(value);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        stream.Write(bytes);
    }
}

/// <summary>
/// Owns short-lived, in-memory invitation state and persists only a successful controller pin.
/// </summary>
public sealed class PairingCoordinator : IDisposable
{
    private const int MaxControllerCertificateBytes = 16 * 1024;
    private const string OneTimeCodeAlphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
    private readonly AgentStateStore stateStore;
    private readonly AgentCertificateManager certificateManager;
    private readonly PairingCoordinatorOptions options;
    private readonly TimeProvider timeProvider;
    private readonly ConcurrentDictionary<Guid, PairingState> invitations = new();
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly ITimer cleanupTimer;
    private bool disposed;

    public PairingCoordinator(
        AgentStateStore stateStore,
        AgentCertificateManager certificateManager,
        PairingCoordinatorOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        this.stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        this.certificateManager = certificateManager ?? throw new ArgumentNullException(nameof(certificateManager));
        this.options = options ?? new PairingCoordinatorOptions();
        this.options.Validate();
        this.timeProvider = timeProvider ?? TimeProvider.System;
        cleanupTimer = this.timeProvider.CreateTimer(
            static state => ((PairingCoordinator)state!).PurgeExpired(),
            this,
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(15));
    }

    public int ActiveInvitationCount => invitations.Count;

    public async Task<PairingInvitation> CreateInvitationAsync(
        PairingEndpoint agentEndpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agentEndpoint);
        ThrowIfDisposed();
        await lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            PurgeExpired();
            await ThrowIfPairedAsync(cancellationToken);
            if (!invitations.IsEmpty)
            {
                throw new InvalidOperationException("A pairing invitation is already active.");
            }

            using var identity = await certificateManager.GetOrCreateAsync(cancellationToken);
            var pairingId = Guid.NewGuid();
            var expiresAtUtc = timeProvider.GetUtcNow().Add(options.InvitationLifetime);
            var code = CreateOneTimeCode(options.OneTimeCodeLength);
            var certificateFingerprint = SHA256.HashData(identity.Certificate.RawData);
            var spkiFingerprint = CertificateFingerprints.GetSpkiSha256(identity.Certificate);
            var state = new PairingState(
                pairingId,
                code,
                identity.DeviceId,
                agentEndpoint,
                certificateFingerprint,
                spkiFingerprint,
                expiresAtUtc,
                options.MaxFailedAttempts);
            if (!invitations.TryAdd(pairingId, state))
            {
                state.Dispose();
                throw new InvalidOperationException("Unable to create the pairing invitation.");
            }

            return new PairingInvitation(
                pairingId,
                new string(code),
                identity.DeviceId,
                agentEndpoint,
                certificateFingerprint,
                spkiFingerprint,
                expiresAtUtc);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async Task<PairingBinding> BindControllerAsync(
        Guid pairingId,
        string controllerId,
        byte[] controllerCertificate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(controllerCertificate);
        ThrowIfDisposed();
        var normalizedId = NormalizeControllerId(controllerId);
        var normalizedCertificate = ValidateControllerCertificate(controllerCertificate);

        await lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            PurgeExpired();
            await ThrowIfPairedAsync(cancellationToken);
            var state = GetActiveInvitation(pairingId);
            lock (state.Sync)
            {
                state.ThrowIfUnavailable(timeProvider.GetUtcNow());
                if (state.Binding is not null)
                {
                    throw new InvalidOperationException("The pairing invitation is already bound to a controller.");
                }

                state.Bind(normalizedId, normalizedCertificate);
                return state.Binding!;
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public PairingPakeRound1 CreateAgentRound1(Guid pairingId) =>
        UsePake(pairingId, static state => state.Session!.CreateRound1());

    public void ReceiveControllerRound1(Guid pairingId, PairingPakeRound1 round1) =>
        UsePake(pairingId, state =>
        {
            state.Session!.ReceiveRound1(round1);
            return true;
        });

    public PairingPakeRound2 CreateAgentRound2(Guid pairingId) =>
        UsePake(pairingId, static state => state.Session!.CreateRound2());

    public void ReceiveControllerRound2(Guid pairingId, PairingPakeRound2 round2) =>
        UsePake(pairingId, state =>
        {
            state.Session!.ReceiveRound2(round2);
            return true;
        });

    public PairingPakeRound3 CreateAgentRound3(Guid pairingId) =>
        UsePake(pairingId, static state => state.Session!.CreateRound3());

    public void ReceiveControllerRound3(Guid pairingId, PairingPakeRound3 round3) =>
        UsePake(pairingId, state =>
        {
            state.Session!.ReceiveRound3(round3);
            return true;
        });

    public async Task<PairedController> CompleteAsync(
        Guid pairingId,
        PairingCompletionProof proof,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proof);
        ThrowIfDisposed();
        var state = GetActiveInvitation(pairingId);
        PairedController pairedController;

        lock (state.Sync)
        {
            state.ThrowIfUnavailable(timeProvider.GetUtcNow());
            if (state.Completing)
            {
                throw new InvalidOperationException("The pairing invitation is already completing.");
            }

            try
            {
                using var result = state.Session!.GetResult();
                ValidateCompletionProof(state, result, proof);
            }
            catch (CryptographicException)
            {
                RegisterFailedAttempt(state);
                throw;
            }

            state.Completing = true;
            pairedController = new PairedController(
                state.Binding!.ControllerId,
                state.ControllerCertificate!,
                timeProvider.GetUtcNow());
        }

        try
        {
            await lifecycleGate.WaitAsync(cancellationToken);
            try
            {
                PurgeExpired();
                if (!await stateStore.TrySavePairedControllerIfNoneAsync(pairedController, cancellationToken))
                {
                    throw new InvalidOperationException("A controller is already paired with this agent.");
                }

                RemoveInvitation(pairingId, state);
                return pairedController;
            }
            finally
            {
                lifecycleGate.Release();
            }
        }
        catch
        {
            lock (state.Sync)
            {
                if (!state.Disposed)
                {
                    state.Completing = false;
                }
            }

            throw;
        }
    }

    /// <summary>Removes the controller pin and invalidates every pending invitation.</summary>
    public async Task UnpairAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            foreach (var entry in invitations)
            {
                RemoveInvitation(entry.Key, entry.Value);
            }

            await stateStore.RemovePairedControllerAsync(cancellationToken);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    /// <summary>Explicit sweep hook for deterministic tests and host shutdown paths.</summary>
    public void PurgeExpired()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var entry in invitations)
        {
            if (entry.Value.ExpiresAtUtc <= now)
            {
                RemoveInvitation(entry.Key, entry.Value);
            }
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        cleanupTimer.Dispose();
        foreach (var entry in invitations)
        {
            RemoveInvitation(entry.Key, entry.Value);
        }

        lifecycleGate.Dispose();
    }

    private T UsePake<T>(Guid pairingId, Func<PairingState, T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ThrowIfDisposed();
        PurgeExpired();
        var state = GetActiveInvitation(pairingId);
        lock (state.Sync)
        {
            state.ThrowIfUnavailable(timeProvider.GetUtcNow());
            if (state.Completing)
            {
                throw new InvalidOperationException("The pairing invitation is already completing.");
            }

            if (state.Session is null)
            {
                throw new InvalidOperationException("The pairing invitation has not been bound to a controller.");
            }

            try
            {
                return action(state);
            }
            catch (CryptographicException)
            {
                RegisterFailedAttempt(state);
                throw;
            }
        }
    }

    private void RegisterFailedAttempt(PairingState state)
    {
        state.FailedAttempts++;
        state.Session?.Dispose();
        state.Session = null;
        if (state.FailedAttempts >= state.MaxFailedAttempts)
        {
            RemoveInvitation(state.PairingId, state);
            return;
        }

        state.ResetSession();
    }

    private PairingState GetActiveInvitation(Guid pairingId)
    {
        if (pairingId == Guid.Empty || !invitations.TryGetValue(pairingId, out var state))
        {
            throw new InvalidOperationException("The pairing invitation is unknown, expired, or has already been used.");
        }

        return state;
    }

    private void RemoveInvitation(Guid pairingId, PairingState expected)
    {
        if (invitations.TryRemove(new KeyValuePair<Guid, PairingState>(pairingId, expected)))
        {
            lock (expected.Sync)
            {
                expected.Dispose();
            }
        }
    }

    private async Task ThrowIfPairedAsync(CancellationToken cancellationToken)
    {
        if (await stateStore.GetPairedControllerAsync(cancellationToken) is not null)
        {
            throw new InvalidOperationException("This agent already has a paired controller. Unpair locally before pairing another controller.");
        }
    }

    private static char[] CreateOneTimeCode(int length)
    {
        var code = new char[length];
        for (var index = 0; index < code.Length; index++)
        {
            code[index] = OneTimeCodeAlphabet[RandomNumberGenerator.GetInt32(OneTimeCodeAlphabet.Length)];
        }

        return code;
    }

    private static string NormalizeControllerId(string controllerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(controllerId);
        var normalized = controllerId.Normalize(NormalizationForm.FormC);
        if (normalized.Length > 128 || normalized.Any(char.IsControl))
        {
            throw new ArgumentOutOfRangeException(nameof(controllerId));
        }

        return normalized;
    }

    private static byte[] ValidateControllerCertificate(byte[] suppliedCertificate)
    {
        if (suppliedCertificate.Length is 0 or > MaxControllerCertificateBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(suppliedCertificate));
        }

        try
        {
            using var certificate = new X509Certificate2(suppliedCertificate);
            using var publicKey = certificate.GetECDsaPublicKey();
            if (publicKey is null || publicKey.KeySize != 256)
            {
                throw new CryptographicException("The controller certificate must contain a P-256 ECDSA public key.");
            }

            var keyUsage = certificate.Extensions.OfType<X509KeyUsageExtension>().SingleOrDefault();
            if (keyUsage is null || !keyUsage.KeyUsages.HasFlag(X509KeyUsageFlags.DigitalSignature))
            {
                throw new CryptographicException("The controller certificate must allow digital signatures.");
            }

            var usages = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().SingleOrDefault();
            if (usages is null || !usages.EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value == "1.3.6.1.5.5.7.3.2"))
            {
                throw new CryptographicException("The controller certificate must allow TLS client authentication.");
            }

            if (certificate.Extensions.OfType<X509BasicConstraintsExtension>().Any(extension => extension.CertificateAuthority))
            {
                throw new CryptographicException("The controller certificate must not be a certificate authority.");
            }

            var now = DateTimeOffset.UtcNow;
            if (certificate.NotBefore.ToUniversalTime() > now.AddMinutes(5) || certificate.NotAfter.ToUniversalTime() <= now)
            {
                throw new CryptographicException("The controller certificate is not currently valid.");
            }

            return certificate.Export(X509ContentType.Cert);
        }
        catch (CryptographicException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            throw new CryptographicException("The controller certificate is invalid.", exception);
        }
    }

    private static void ValidateCompletionProof(PairingState state, PairingPakeResult result, PairingCompletionProof proof)
    {
        var payload = PairingConfirmation.BuildPayload(state.Binding!);
        var sessionKey = result.SessionKey;
        var confirmationMac = proof.ConfirmationMac;
        var certificateSignature = proof.CertificateSignature;
        try
        {
            if (confirmationMac.Length != 32)
            {
                throw new CryptographicException("The controller completion MAC is invalid.");
            }

            using var mac = new HMACSHA256(sessionKey);
            var expectedMac = mac.ComputeHash(payload);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(expectedMac, confirmationMac))
                {
                    throw new CryptographicException("The controller completion MAC does not match this pairing session.");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(expectedMac);
            }

            using var certificate = new X509Certificate2(state.ControllerCertificate!);
            using var publicKey = certificate.GetECDsaPublicKey()
                ?? throw new CryptographicException("The controller certificate has no ECDSA public key.");
            if (!publicKey.VerifyData(payload, certificateSignature, HashAlgorithmName.SHA256))
            {
                throw new CryptographicException("The controller did not prove possession of its client certificate private key.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            CryptographicOperations.ZeroMemory(sessionKey);
            CryptographicOperations.ZeroMemory(confirmationMac);
            CryptographicOperations.ZeroMemory(certificateSignature);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    private sealed class PairingState : IDisposable
    {
        private char[] oneTimeCode;

        public PairingState(
            Guid pairingId,
            char[] oneTimeCode,
            string agentDeviceId,
            PairingEndpoint agentEndpoint,
            byte[] agentCertificateFingerprint,
            byte[] agentSpkiFingerprint,
            DateTimeOffset expiresAtUtc,
            int maxFailedAttempts)
        {
            PairingId = pairingId;
            this.oneTimeCode = oneTimeCode;
            AgentDeviceId = agentDeviceId;
            AgentEndpoint = agentEndpoint;
            AgentCertificateFingerprint = agentCertificateFingerprint;
            AgentSpkiFingerprint = agentSpkiFingerprint;
            ExpiresAtUtc = expiresAtUtc;
            MaxFailedAttempts = maxFailedAttempts;
        }

        public object Sync { get; } = new();

        public Guid PairingId { get; }

        public string AgentDeviceId { get; }

        public PairingEndpoint AgentEndpoint { get; }

        public byte[] AgentCertificateFingerprint { get; }

        public byte[] AgentSpkiFingerprint { get; }

        public DateTimeOffset ExpiresAtUtc { get; }

        public int MaxFailedAttempts { get; }

        public int FailedAttempts { get; set; }

        public PairingBinding? Binding { get; private set; }

        public byte[]? ControllerCertificate { get; private set; }

        public JpakePairingSession? Session { get; set; }

        public bool Completing { get; set; }

        public bool Disposed { get; private set; }

        public void Bind(string controllerId, byte[] controllerCertificate)
        {
            using var certificate = new X509Certificate2(controllerCertificate);
            Binding = new PairingBinding(
                PairingId,
                AgentDeviceId,
                controllerId,
                AgentEndpoint,
                AgentCertificateFingerprint,
                AgentSpkiFingerprint,
                SHA256.HashData(certificate.RawData),
                CertificateFingerprints.GetSpkiSha256(certificate));
            ControllerCertificate = controllerCertificate.ToArray();
            ResetSession();
        }

        public void ResetSession()
        {
            var transcriptHash = PairingTranscript.ComputeHash(Binding!);
            try
            {
                Session = new JpakePairingSession(PairingId, PairingPakeRole.Agent, oneTimeCode, transcriptHash);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(transcriptHash);
            }
        }

        public void ThrowIfUnavailable(DateTimeOffset now)
        {
            if (Disposed || ExpiresAtUtc <= now)
            {
                throw new InvalidOperationException("The pairing invitation is unknown, expired, or has already been used.");
            }
        }

        public void Dispose()
        {
            if (Disposed)
            {
                return;
            }

            Disposed = true;
            Session?.Dispose();
            Session = null;
            Array.Clear(oneTimeCode);
            oneTimeCode = [];
            if (ControllerCertificate is not null)
            {
                CryptographicOperations.ZeroMemory(ControllerCertificate);
                ControllerCertificate = null;
            }

            CryptographicOperations.ZeroMemory(AgentCertificateFingerprint);
            CryptographicOperations.ZeroMemory(AgentSpkiFingerprint);
        }
    }
}

/// <summary>
/// Ignores the Windows/system trust chain. mTLS authorization succeeds only for the
/// exact controller certificate DER stored after PAKE completion.
/// </summary>
public static class PairedControllerCertificateValidator
{
    public static bool Matches(PairedController? pairedController, X509Certificate? presentedCertificate)
    {
        if (pairedController is null || presentedCertificate is null)
        {
            return false;
        }

        var expected = pairedController.Certificate;
        byte[]? actual = null;
        try
        {
            actual = presentedCertificate.Export(X509ContentType.Cert);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (CryptographicException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expected);
            if (actual is not null)
            {
                CryptographicOperations.ZeroMemory(actual);
            }
        }
    }
}

internal static class CertificateFingerprints
{
    public static byte[] GetSpkiSha256(X509Certificate2 certificate)
    {
        using var publicKey = certificate.GetECDsaPublicKey()
            ?? throw new CryptographicException("The certificate does not contain an ECDSA public key.");
        var subjectPublicKeyInfo = publicKey.ExportSubjectPublicKeyInfo();
        try
        {
            return SHA256.HashData(subjectPublicKeyInfo);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(subjectPublicKeyInfo);
        }
    }
}

internal static class PairingConfirmation
{
    private static readonly byte[] DomainSeparator = Encoding.UTF8.GetBytes("rc/pairing/controller-confirm/v1");

    public static byte[] BuildPayload(PairingBinding binding)
    {
        var transcriptHash = PairingTranscript.ComputeHash(binding);
        try
        {
            var payload = new byte[DomainSeparator.Length + transcriptHash.Length];
            Buffer.BlockCopy(DomainSeparator, 0, payload, 0, DomainSeparator.Length);
            Buffer.BlockCopy(transcriptHash, 0, payload, DomainSeparator.Length, transcriptHash.Length);
            return payload;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(transcriptHash);
        }
    }
}
