using System.Security.Cryptography;
using System.Text.Json;
using Rc.Agent.Configuration;
using Rc.Agent.Jobs;
using Rc.Agent.Persistence;
using Rc.Contracts;

namespace Rc.Agent.Updates;

public interface IAgentUpdateApplier
{
    Task<string> ApplyAsync(string packagePath, CancellationToken cancellationToken = default);
}

public sealed class UpdateService : IUpdateServiceV1, IDisposable
{
    private static readonly string[] RequiredPackageFiles =
    [
        "Install-RemoteController.ps1",
        "Update-RemoteController.ps1",
        "Rc.Agent.exe",
        "Rc.PrivilegedBroker.exe",
        "Rc.TaskHost.exe",
        "Rc.UiAgent.exe",
        "Rc.UiTestApp.exe",
        "Rc.InteractiveTestApp.exe",
        "Rc.Cli.exe",
    ];

    private readonly AgentStateStore stateStore;
    private readonly AgentOptions options;
    private readonly IAgentUpdateApplier applier;
    private readonly string updatesRoot;
    private readonly SemaphoreSlim gate = new(1, 1);

    public UpdateService(AgentStateStore stateStore, AgentOptions options, IAgentUpdateApplier applier)
    {
        this.stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.applier = applier ?? throw new ArgumentNullException(nameof(applier));
        updatesRoot = Path.Combine(stateStore.DataRoot, "updates");
    }

    public async ValueTask<UpdateStatusResponse> StartAsync(UpdateStartRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateManifest(request.Manifest);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = GetSessionPath(request.UpdateId);
            if (File.Exists(path))
            {
                var existing = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
                if (!ManifestEquals(existing.Manifest, request.Manifest))
                {
                    throw new InvalidOperationException("The update ID is already associated with a different package manifest.");
                }
                return ToResponse(existing);
            }

            Directory.CreateDirectory(GetPayloadDirectory(request.UpdateId));
            var now = DateTimeOffset.UtcNow;
            var session = new PersistedUpdateSession(request.UpdateId, request.Manifest, UpdateState.Receiving, 0,
                request.Manifest.Files.Sum(file => file.Length), null, null, now);
            await WriteAsync(session, cancellationToken).ConfigureAwait(false);
            return ToResponse(session);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<UpdateStatusResponse> WriteChunkAsync(UpdateWriteChunkRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Data.Length < 1 || request.Data.Length > options.MaximumUpdateChunkBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(request), $"An update chunk must contain 1 to {options.MaximumUpdateChunkBytes} bytes.");
        }
        if (!IsSha256(request.Sha256) || !string.Equals(Convert.ToHexString(SHA256.HashData(request.Data)), request.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The update chunk SHA-256 does not match its data.");
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = await ReadAsync(GetSessionPath(request.UpdateId), cancellationToken).ConfigureAwait(false);
            if (session.State != UpdateState.Receiving)
            {
                throw new InvalidOperationException("The update package is no longer accepting data.");
            }

            var relativePath = NormalizeRelativePath(request.RelativePath);
            var expected = session.Manifest.Files.SingleOrDefault(file => string.Equals(file.RelativePath, relativePath, StringComparison.Ordinal));
            if (expected is null)
            {
                throw new InvalidOperationException("The update chunk path is not present in the package manifest.");
            }
            if (request.Offset < 0 || request.Offset + request.Data.Length > expected.Length)
            {
                throw new InvalidOperationException("The update chunk is outside the declared file bounds.");
            }

            var path = GetPayloadPath(request.UpdateId, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var existingLength = File.Exists(path) ? new FileInfo(path).Length : 0;
            if (existingLength == request.Offset)
            {
                await using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
                stream.Position = request.Offset;
                await stream.WriteAsync(request.Data, cancellationToken).ConfigureAwait(false);
                session = session with { ReceivedBytes = checked(session.ReceivedBytes + request.Data.Length), UpdatedAtUtc = DateTimeOffset.UtcNow };
                await WriteAsync(session, cancellationToken).ConfigureAwait(false);
                return ToResponse(session);
            }
            if (existingLength == request.Offset + request.Data.Length && await MatchesExistingChunkAsync(path, request, cancellationToken).ConfigureAwait(false))
            {
                return ToResponse(session);
            }
            throw new InvalidOperationException("The update chunk offset does not match the staged package.");
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<UpdateStatusResponse> CompleteAsync(UpdateCompleteRequest request, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = await ReadAsync(GetSessionPath(request.UpdateId), cancellationToken).ConfigureAwait(false);
            if (session.State != UpdateState.Receiving)
            {
                return ToResponse(session);
            }

            try
            {
                foreach (var file in session.Manifest.Files)
                {
                    var path = GetPayloadPath(request.UpdateId, file.RelativePath);
                    if (!File.Exists(path) || new FileInfo(path).Length != file.Length ||
                        !string.Equals(await HashFileAsync(path, cancellationToken).ConfigureAwait(false), file.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException($"The staged update file '{file.RelativePath}' does not match the package manifest.");
                    }
                }

                var jobId = await applier.ApplyAsync(GetPayloadDirectory(request.UpdateId), cancellationToken).ConfigureAwait(false);
                session = session with
                {
                    State = UpdateState.Applying,
                    InstallationJobId = jobId,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                };
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                session = session with { State = UpdateState.Failed, FailureMessage = exception.Message, UpdatedAtUtc = DateTimeOffset.UtcNow };
            }

            await WriteAsync(session, cancellationToken).ConfigureAwait(false);
            return ToResponse(session);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<UpdateStatusResponse> GetStatusAsync(UpdateStatusRequest request, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = await ReadAsync(GetSessionPath(request.UpdateId), cancellationToken).ConfigureAwait(false);
            if (session.State == UpdateState.Applying && session.InstallationJobId is { } jobId)
            {
                var job = await stateStore.GetJobSnapshotAsync(jobId, cancellationToken).ConfigureAwait(false);
                if (job is { State: JobState.Exited, ExitCode: 0 })
                {
                    session = session with { State = UpdateState.Succeeded, UpdatedAtUtc = DateTimeOffset.UtcNow };
                    await WriteAsync(session, cancellationToken).ConfigureAwait(false);
                }
                else if (job is { State: JobState.Exited } or { State: JobState.FailedToStart } or { State: JobState.Cancelled } or { State: JobState.InterruptedByReboot } or { State: JobState.HostCrashed })
                {
                    session = session with
                    {
                        State = UpdateState.Failed,
                        FailureMessage = job.Error?.Message ?? $"The update installation task ended in state {job.State} with exit code {job.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "n/a"}.",
                        UpdatedAtUtc = DateTimeOffset.UtcNow,
                    };
                    await WriteAsync(session, cancellationToken).ConfigureAwait(false);
                }
            }
            return ToResponse(session);
        }
        finally
        {
            gate.Release();
        }
    }

    private void ValidateManifest(UpdatePackageManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!string.Equals(manifest.Product, "RemoteController", StringComparison.Ordinal) || !Version.TryParse(manifest.Version, out var requestedVersion))
        {
            throw new ArgumentException("The update manifest product or version is invalid.", nameof(manifest));
        }
        var currentVersion = typeof(UpdateService).Assembly.GetName().Version;
        if (currentVersion is not null && requestedVersion < currentVersion)
        {
            throw new InvalidOperationException($"The update package version {requestedVersion} is older than the running Agent version {currentVersion}.");
        }
        if (manifest.Files.Count is < 1 or > 64 || manifest.Files.Select(file => NormalizeRelativePath(file.RelativePath)).Distinct(StringComparer.Ordinal).Count() != manifest.Files.Count)
        {
            throw new ArgumentException("The update manifest file list is invalid.", nameof(manifest));
        }
        var total = 0L;
        foreach (var file in manifest.Files)
        {
            NormalizeRelativePath(file.RelativePath);
            if (file.Length < 0 || !IsSha256(file.Sha256))
            {
                throw new ArgumentException("The update manifest contains an invalid file entry.", nameof(manifest));
            }
            total = checked(total + file.Length);
        }
        if (total > options.MaximumUpdatePackageBytes || RequiredPackageFiles.Any(required => !manifest.Files.Any(file => string.Equals(file.RelativePath, required, StringComparison.OrdinalIgnoreCase))))
        {
            throw new InvalidOperationException("The update package is too large or does not contain the required RemoteController release files.");
        }
    }

    private static bool ManifestEquals(UpdatePackageManifest first, UpdatePackageManifest second) =>
        string.Equals(first.Product, second.Product, StringComparison.Ordinal) &&
        string.Equals(first.Version, second.Version, StringComparison.Ordinal) &&
        first.Files.SequenceEqual(second.Files);

    private string GetSessionPath(Guid updateId) => Path.Combine(GetSessionDirectory(updateId), "update-state.json");

    private string GetPayloadDirectory(Guid updateId) => Path.Combine(GetSessionDirectory(updateId), "payload");

    private string GetSessionDirectory(Guid updateId) => Path.Combine(updatesRoot, updateId.ToString("N"));

    private string GetPayloadPath(Guid updateId, string relativePath)
    {
        var root = GetPayloadDirectory(updateId);
        var full = Path.GetFullPath(Path.Combine(root, NormalizeRelativePath(relativePath).Replace('/', Path.DirectorySeparatorChar)));
        var rootWithSeparator = Path.GetFullPath(root + Path.DirectorySeparatorChar);
        if (!full.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The update package path escapes the staging directory.");
        }
        return full;
    }

    private static string NormalizeRelativePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = path.Replace('\\', '/');
        if (Path.IsPathRooted(normalized) || normalized.Split('/').Any(part => part is "" or "." or "..") || normalized.Contains(':'))
        {
            throw new ArgumentException("Update package paths must be non-empty relative paths.", nameof(path));
        }
        return normalized;
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    private static async Task<bool> MatchesExistingChunkAsync(string path, UpdateWriteChunkRequest request, CancellationToken cancellationToken)
    {
        var existing = new byte[request.Data.Length];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        stream.Position = request.Offset;
        await stream.ReadExactlyAsync(existing, cancellationToken).ConfigureAwait(false);
        return CryptographicOperations.FixedTimeEquals(existing, request.Data);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static UpdateStatusResponse ToResponse(PersistedUpdateSession session) => new(
        session.UpdateId, session.State, session.Manifest.Version, session.ReceivedBytes, session.TotalBytes,
        session.InstallationJobId, session.FailureMessage, session.UpdatedAtUtc);

    private static async Task<PersistedUpdateSession> ReadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new KeyNotFoundException("The update session was not found.");
        }
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, useAsync: true);
        return await JsonSerializer.DeserializeAsync<PersistedUpdateSession>(stream, ContractJson.Options, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The persisted update session is invalid.");
    }

    private async Task WriteAsync(PersistedUpdateSession session, CancellationToken cancellationToken)
    {
        var path = GetSessionPath(session.UpdateId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 16 * 1024, useAsync: true))
        {
            await JsonSerializer.SerializeAsync(stream, session, ContractJson.Options, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        File.Move(temporaryPath, path, overwrite: true);
    }

    public void Dispose() => gate.Dispose();

    private sealed record PersistedUpdateSession(
        Guid UpdateId,
        UpdatePackageManifest Manifest,
        UpdateState State,
        long ReceivedBytes,
        long TotalBytes,
        string? InstallationJobId,
        string? FailureMessage,
        DateTimeOffset UpdatedAtUtc);
}

internal sealed class TaskRegistryUpdateApplier : IAgentUpdateApplier
{
    private readonly ManagedTaskRegistry taskRegistry;
    private readonly string dataRoot;
    private readonly string installPath;
    private readonly int tcpPort;

    public TaskRegistryUpdateApplier(ManagedTaskRegistry taskRegistry, string dataRoot, string installPath, int tcpPort)
    {
        this.taskRegistry = taskRegistry;
        this.dataRoot = dataRoot;
        this.installPath = installPath;
        this.tcpPort = tcpPort;
    }

    public async Task<string> ApplyAsync(string packagePath, CancellationToken cancellationToken = default)
    {
        var updater = Path.Combine(packagePath, "Update-RemoteController.ps1");
        if (!File.Exists(updater))
        {
            throw new FileNotFoundException("The staged update package has no update script.", updater);
        }
        var command = $"& {QuotePowerShell(updater)} -SourcePath {QuotePowerShell(packagePath)} -InstallPath {QuotePowerShell(installPath)} -DataRoot {QuotePowerShell(dataRoot)} -TcpPort {tcpPort}";
        var status = await taskRegistry.StartAsync(
            ExecRequest.ForShellWithIdentity(ShellKind.PowerShell, command, ExecutionIdentity.ElevatedBroker, packagePath),
            cancellationToken).ConfigureAwait(false);
        return status.Job.JobId;
    }

    private static string QuotePowerShell(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
