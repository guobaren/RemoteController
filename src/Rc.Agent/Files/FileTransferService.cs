using System.Security.Cryptography;
using Rc.Agent.Configuration;
using Rc.Agent.Persistence;
using Rc.Contracts;

namespace Rc.Agent.Files;

public sealed class FileTransferService : IDisposable
{
    private readonly AgentStateStore store;
    private readonly AgentOptions options;
    private readonly SafeFileRoot paths;
    private readonly SemaphoreSlim mutationGate = new(1, 1);

    public FileTransferService(AgentStateStore store, AgentOptions? options = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.options = options ?? new AgentOptions();
        paths = new SafeFileRoot(this.options.FileRoot);
    }

    public async Task<FileListResponse> ListAsync(FileListRequest request, CancellationToken cancellationToken = default)
    {
        var root = paths.Resolve(request.RootPath);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(request.RootPath);
        var mode = request.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var entries = Directory.EnumerateFileSystemEntries(root, "*", mode)
            .Select(GetMetadata).OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        return await Task.FromResult(new FileListResponse(entries));
    }

    public Task<FileStatResponse> StatAsync(FileStatRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new FileStatResponse(GetMetadata(paths.Resolve(request.Path))));

    public async Task<FileReadResponse> ReadAsync(FileReadRequest request, CancellationToken cancellationToken = default)
    {
        if (request.MaximumBytes > options.MaximumTransferChunkBytes) throw new ArgumentOutOfRangeException(nameof(request));
        var path = paths.Resolve(request.Path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, true);
        if (request.Offset > stream.Length) throw new ArgumentOutOfRangeException(nameof(request));
        stream.Position = request.Offset;
        var data = new byte[Math.Min(request.MaximumBytes, checked((int)(stream.Length - request.Offset)))];
        await stream.ReadExactlyAsync(data, cancellationToken);
        return new FileReadResponse(new FileRangeChunk(paths.ToDisplayPath(path), request.Offset, data, request.Offset + data.Length >= stream.Length));
    }

    public async Task<FileWriteResponse> WriteAsync(FileWriteRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Data.Length > options.MaximumAtomicWriteBytes) throw new ResourceExhaustedException("Atomic write exceeds the configured byte limit.");
        var path = paths.Resolve(request.Path);
        var parent = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(parent);
        if (!request.Overwrite && File.Exists(path)) throw new IOException("The destination already exists.");
        var temporary = Path.Combine(parent, ".rc-write-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, request.Data, cancellationToken);
            File.Move(temporary, path, request.Overwrite);
        }
        finally
        {
            File.Delete(temporary);
        }
        return new FileWriteResponse(GetMetadata(path));
    }

    public async Task<FileManifestResponse> GetManifestAsync(FileManifestRequest request, CancellationToken cancellationToken = default) =>
        new(await BuildManifestAsync(request.RootPath, cancellationToken));

    public async Task<TransferStartResponse> StartTransferAsync(TransferStartRequest request, CancellationToken cancellationToken = default)
    {
        if (request.ChunkSize < 1 || request.ChunkSize > options.MaximumTransferChunkBytes) throw new ArgumentOutOfRangeException(nameof(request));
        FileManifest manifest;
        if (request.Direction == TransferDirection.Download)
        {
            paths.Resolve(request.SourcePath);
            manifest = await BuildManifestAsync(request.SourcePath, cancellationToken);
        }
        else
        {
            paths.Resolve(request.DestinationPath);
            manifest = request.Manifest;
            ValidateManifest(manifest, request.DestinationPath);
        }
        EnsureQuota(manifest);
        var now = DateTimeOffset.UtcNow;
        var snapshot = new TransferSessionSnapshot(
            "transfer-" + Guid.NewGuid().ToString("N"), request.Direction, TransferSessionState.Transferring,
            request.SourcePath, request.DestinationPath, manifest, request.ChunkSize, now, now.Add(options.TransferSessionLifetime));
        await store.SaveTransferSessionAsync(snapshot, cancellationToken);
        return new TransferStartResponse(snapshot);
    }

    public async Task<TransferWriteChunkResponse> WriteChunkAsync(TransferWriteChunkRequest request, CancellationToken cancellationToken = default)
    {
        await mutationGate.WaitAsync(cancellationToken);
        try
        {
            var session = await GetActiveSessionAsync(request.Chunk.TransferSessionId, TransferDirection.Upload, cancellationToken);
            var entry = FindFileEntry(session.Manifest, request.Chunk.RelativePath);
            ValidateChunk(session, entry, request.Chunk.Offset, request.Chunk.Data.Length);
            var hash = Convert.ToHexString(SHA256.HashData(request.Chunk.Data));
            if (!string.Equals(hash, NormalizeHash(request.ChunkSha256), StringComparison.Ordinal)) throw new InvalidDataException("Chunk SHA-256 mismatch.");
            var receipts = session.CompletedChunks.ToList();
            var existing = receipts.FirstOrDefault(item => item.RelativePath == request.Chunk.RelativePath && item.Offset == request.Chunk.Offset);
            if (existing is not null)
            {
                if (existing.Length != request.Chunk.Data.Length || !string.Equals(existing.Sha256, hash, StringComparison.Ordinal)) throw new InvalidDataException("A different chunk is already stored at this offset.");
                return new TransferWriteChunkResponse(session);
            }
            var part = GetPartPath(session.SessionId, request.Chunk.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(part)!);
            await using (var stream = new FileStream(part, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 64 * 1024, true))
            {
                if (stream.Length != entry.Length) stream.SetLength(entry.Length);
                stream.Position = request.Chunk.Offset;
                await stream.WriteAsync(request.Chunk.Data, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            receipts.Add(new TransferChunkReceipt(request.Chunk.RelativePath, request.Chunk.Offset, request.Chunk.Data.Length, hash));
            var completed = session.Manifest.Entries.Where(e => e.Sha256 is not null && HasAllChunks(e, session.ChunkSize, receipts)).Select(e => e.RelativePath).ToArray();
            var updated = Clone(session, completedChunks: receipts, completedPaths: completed);
            await store.SaveTransferSessionAsync(updated, cancellationToken);
            return new TransferWriteChunkResponse(updated);
        }
        finally { mutationGate.Release(); }
    }

    public async Task<TransferReadChunkResponse> ReadChunkAsync(TransferReadChunkRequest request, CancellationToken cancellationToken = default)
    {
        var session = await GetActiveSessionAsync(request.SessionId, TransferDirection.Download, cancellationToken);
        if (request.MaximumBytes > session.ChunkSize || request.MaximumBytes > options.MaximumTransferChunkBytes) throw new ArgumentOutOfRangeException(nameof(request));
        var entry = FindFileEntry(session.Manifest, request.RelativePath);
        ValidateChunk(session, entry, request.Offset, Math.Min(request.MaximumBytes, checked((int)(entry.Length - request.Offset))));
        var source = paths.ResolveRelative(session.SourcePath, request.RelativePath);
        await using var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, true);
        stream.Position = request.Offset;
        var data = new byte[Math.Min(request.MaximumBytes, checked((int)(stream.Length - request.Offset)))];
        await stream.ReadExactlyAsync(data, cancellationToken);
        var hash = Convert.ToHexString(SHA256.HashData(data));
        return new TransferReadChunkResponse(new FileChunk(session.SessionId, request.RelativePath, request.Offset, data, request.Offset + data.Length >= stream.Length), hash);
    }

    public async Task<TransferCompleteResponse> CompleteAsync(TransferCompleteRequest request, CancellationToken cancellationToken = default)
    {
        await mutationGate.WaitAsync(cancellationToken);
        try
        {
            var session = await GetSessionAsync(request.SessionId, cancellationToken);
            if (session.Direction == TransferDirection.Upload)
            {
                if (session.Manifest.Entries.Count == 0) Directory.CreateDirectory(paths.Resolve(session.DestinationPath));
                foreach (var entry in session.Manifest.Entries.Where(e => e.Sha256 is not null))
                {
                    if (!HasAllChunks(entry, session.ChunkSize, session.CompletedChunks)) throw new InvalidOperationException($"File '{entry.RelativePath}' is incomplete.");
                    var part = GetPartPath(session.SessionId, entry.RelativePath);
                    if (entry.Length == 0 && !File.Exists(part))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(part)!);
                        await File.WriteAllBytesAsync(part, [], cancellationToken);
                    }
                    var hash = await HashFileAsync(part, cancellationToken);
                    if (!string.Equals(hash, NormalizeHash(entry.Sha256!), StringComparison.Ordinal)) throw new InvalidDataException($"Final SHA-256 mismatch for '{entry.RelativePath}'.");
                }
                foreach (var directory in session.Manifest.Entries.Where(e => e.Sha256 is null).OrderBy(e => e.RelativePath.Length))
                    Directory.CreateDirectory(paths.ResolveRelative(session.DestinationPath, directory.RelativePath));
                foreach (var entry in session.Manifest.Entries.Where(e => e.Sha256 is not null))
                {
                    var destination = paths.ResolveRelative(session.DestinationPath, entry.RelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Move(GetPartPath(session.SessionId, entry.RelativePath), destination, overwrite: true);
                }
            }
            var completed = Clone(session, state: TransferSessionState.Completed);
            await store.SaveTransferSessionAsync(completed, cancellationToken);
            TryDeleteSessionFiles(session.SessionId);
            return new TransferCompleteResponse(completed);
        }
        finally { mutationGate.Release(); }
    }

    public async Task<TransferStatusResponse> StatusAsync(TransferStatusRequest request, CancellationToken cancellationToken = default) =>
        new(await GetSessionAsync(request.SessionId, cancellationToken));

    public void Dispose() => mutationGate.Dispose();

    private async Task<TransferSessionSnapshot> GetActiveSessionAsync(string id, TransferDirection direction, CancellationToken cancellationToken)
    {
        var session = await GetSessionAsync(id, cancellationToken);
        if (session.Direction != direction || session.State != TransferSessionState.Transferring) throw new InvalidOperationException("The transfer session is not active for this operation.");
        return session;
    }

    private async Task<TransferSessionSnapshot> GetSessionAsync(string id, CancellationToken cancellationToken)
    {
        var session = await store.GetTransferSessionAsync(id, cancellationToken) ?? throw new KeyNotFoundException($"No transfer session exists with ID '{id}'.");
        if (session.ExpiresAtUtc <= DateTimeOffset.UtcNow && session.State == TransferSessionState.Transferring)
        {
            session = Clone(session, state: TransferSessionState.Expired);
            await store.SaveTransferSessionAsync(session, cancellationToken);
            TryDeleteSessionFiles(id);
        }
        return session;
    }

    private async Task<FileManifest> BuildManifestAsync(string rootPath, CancellationToken cancellationToken)
    {
        var full = paths.Resolve(rootPath);
        var entries = new List<FileManifestEntry>();
        if (File.Exists(full))
        {
            var info = new FileInfo(full);
            entries.Add(new FileManifestEntry(string.Empty, info.Length, info.LastWriteTimeUtc, await HashFileAsync(full, cancellationToken)));
        }
        else if (Directory.Exists(full))
        {
            foreach (var dir in Directory.EnumerateDirectories(full, "*", SearchOption.AllDirectories))
                entries.Add(new FileManifestEntry(Path.GetRelativePath(full, dir).Replace('\\', '/'), 0, Directory.GetLastWriteTimeUtc(dir), null));
            foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
            {
                var info = new FileInfo(file);
                entries.Add(new FileManifestEntry(Path.GetRelativePath(full, file).Replace('\\', '/'), info.Length, info.LastWriteTimeUtc, await HashFileAsync(file, cancellationToken)));
            }
        }
        else throw new FileNotFoundException("The file root does not exist.", rootPath);
        var manifest = new FileManifest(rootPath, entries.OrderBy(e => e.RelativePath, StringComparer.Ordinal).ToArray());
        EnsureQuota(manifest);
        return manifest;
    }

    private FileMetadata GetMetadata(string path)
    {
        if (File.Exists(path)) { var f = new FileInfo(path); return new FileMetadata(paths.ToDisplayPath(path), FileEntryKind.File, f.Length, f.LastWriteTimeUtc); }
        if (Directory.Exists(path)) { var d = new DirectoryInfo(path); return new FileMetadata(paths.ToDisplayPath(path), FileEntryKind.Directory, 0, d.LastWriteTimeUtc); }
        throw new FileNotFoundException("The path does not exist.", path);
    }

    private void ValidateManifest(FileManifest manifest, string destinationRoot)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Entries)
        {
            if (!seen.Add(entry.RelativePath)) throw new InvalidDataException("Manifest paths must be unique.");
            paths.ResolveRelative(destinationRoot, entry.RelativePath);
            if (entry.Length < 0 || entry.Sha256 is not null && NormalizeHash(entry.Sha256).Length != 64) throw new InvalidDataException("Manifest entry is invalid.");
        }
    }

    private void EnsureQuota(FileManifest manifest)
    {
        var total = manifest.Entries.Where(e => e.Sha256 is not null).Sum(e => e.Length);
        if (total > options.TransferQuotaBytes) throw new ResourceExhaustedException("Transfer exceeds the configured byte quota.");
    }

    private static FileManifestEntry FindFileEntry(FileManifest manifest, string relativePath) =>
        manifest.Entries.SingleOrDefault(e => e.Sha256 is not null && string.Equals(e.RelativePath, relativePath, StringComparison.Ordinal))
        ?? throw new KeyNotFoundException($"No file entry exists for '{relativePath}'.");

    private static void ValidateChunk(TransferSessionSnapshot session, FileManifestEntry entry, long offset, int length)
    {
        if (offset < 0 || offset > entry.Length || length < 0 || length > session.ChunkSize || offset + length > entry.Length || offset % session.ChunkSize != 0)
            throw new ArgumentOutOfRangeException(nameof(offset), "Chunk offset or length is invalid.");
    }

    private static bool HasAllChunks(FileManifestEntry entry, int chunkSize, IReadOnlyList<TransferChunkReceipt> receipts)
    {
        for (long offset = 0; offset < entry.Length; offset += chunkSize)
        {
            var expected = checked((int)Math.Min(chunkSize, entry.Length - offset));
            if (!receipts.Any(r => r.RelativePath == entry.RelativePath && r.Offset == offset && r.Length == expected)) return false;
        }
        return true;
    }

    private static TransferSessionSnapshot Clone(TransferSessionSnapshot s, TransferSessionState? state = null, IReadOnlyList<TransferChunkReceipt>? completedChunks = null, IReadOnlyList<string>? completedPaths = null) =>
        new(s.SessionId, s.Direction, state ?? s.State, s.SourcePath, s.DestinationPath, s.Manifest, s.ChunkSize, s.CreatedAtUtc, s.ExpiresAtUtc,
            completedPaths ?? s.CompletedRelativePaths, completedChunks ?? s.CompletedChunks);

    private string GetPartPath(string sessionId, string relativePath)
    {
        var name = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(relativePath))) + ".part";
        return Path.Combine(store.DataRoot, "transfers", sessionId, name);
    }

    private void TryDeleteSessionFiles(string id)
    {
        var path = Path.Combine(store.DataRoot, "transfers", id);
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch (IOException) { }
    }

    private static string NormalizeHash(string hash) => hash.Replace(":", string.Empty, StringComparison.Ordinal).Trim().ToUpperInvariant();
    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }
}