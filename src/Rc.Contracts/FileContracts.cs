namespace Rc.Contracts;

public sealed class FileManifest
{
    public FileManifest(string rootPath, IReadOnlyList<FileManifestEntry> entries)
    {
        RootPath = rootPath;
        Entries = Array.AsReadOnly(entries.ToArray());
    }

    public string RootPath { get; }

    public IReadOnlyList<FileManifestEntry> Entries { get; }
}

public sealed record FileManifestEntry(
    string RelativePath,
    long Length,
    DateTimeOffset LastWriteTimeUtc,
    string? Sha256);

public sealed record FileManifestRequest(string RootPath);

public sealed record FileManifestResponse(FileManifest Manifest);

public sealed record FileReadRequest(string Path, long Offset, int Length);

public sealed record FileReadResponse(FileChunk Chunk);

public sealed record FileWriteRequest(string Path, FileChunk Chunk, bool Overwrite);

public sealed record FileWriteResponse(long BytesWritten);
