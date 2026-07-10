namespace Rc.Contracts;

public sealed record FileManifest(string RootPath, IReadOnlyList<FileManifestEntry> Entries);

public sealed record FileManifestEntry(
    string RelativePath,
    long Length,
    DateTimeOffset LastWriteTimeUtc,
    string? Sha256);

public sealed record FileManifestRequest(string RootPath);

public sealed record FileManifestResponse(FileManifest Manifest);

public sealed record FileReadRequest(string Path, long Offset, int Length);

public sealed record FileReadResponse(ByteChunk Chunk);

public sealed record FileWriteRequest(string Path, ByteChunk Chunk, bool Overwrite);

public sealed record FileWriteResponse(long BytesWritten);
