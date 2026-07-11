namespace Rc.Agent.Files;

public sealed class SafeFileRoot
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };
    private readonly string rootPrefix;

    public SafeFileRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Directory.CreateDirectory(Root);
        rootPrefix = Root + Path.DirectorySeparatorChar;
    }

    public string Root { get; }

    public string Resolve(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var validationPath = Path.IsPathFullyQualified(path) ? path[(Path.GetPathRoot(path)?.Length ?? 0)..] : path;
        ValidateSegments(validationPath);
        var full = Path.GetFullPath(Path.IsPathFullyQualified(path) ? path : Path.Combine(Root, path));
        if (!string.Equals(full, Root, StringComparison.OrdinalIgnoreCase) && !full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("The path is outside the configured file root.");
        }
        RejectReparsePoints(full);
        return full;
    }

    public string ResolveRelative(string rootPath, string relativePath)
    {
        var basePath = Resolve(rootPath);
        if (string.IsNullOrEmpty(relativePath))
        {
            return basePath;
        }
        if (Path.IsPathFullyQualified(relativePath))
        {
            throw new UnauthorizedAccessException("Manifest paths must be relative.");
        }
        ValidateSegments(relativePath);
        var combined = Path.GetFullPath(Path.Combine(basePath, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = basePath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("The manifest path escapes its transfer root.");
        }
        RejectReparsePoints(combined);
        return combined;
    }

    public string ToDisplayPath(string fullPath) => Path.GetRelativePath(Root, fullPath).Replace('\\', '/');

    private static void ValidateSegments(string path)
    {
        var trimmed = path.Replace('\\', '/');
        foreach (var segment in trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == "..") throw new UnauthorizedAccessException("Parent traversal is not allowed.");
            if (segment == ".") continue;
            if (segment.Contains(':', StringComparison.Ordinal) || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new ArgumentException("The path contains an unsafe segment.", nameof(path));
            }
            var stem = segment.TrimEnd('.', ' ').Split('.')[0];
            if (ReservedNames.Contains(stem))
            {
                throw new ArgumentException("Windows device names are not valid file paths.", nameof(path));
            }
        }
    }

    private void RejectReparsePoints(string fullPath)
    {
        var current = fullPath;
        while (current.Length >= Root.Length && !string.Equals(current, Root, StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new UnauthorizedAccessException("Reparse points are not allowed in file paths.");
                }
            }
            current = Path.GetDirectoryName(current) ?? Root;
        }
    }
}