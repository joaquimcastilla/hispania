using Hispania.Library.Contracts;

namespace Hispania.Library.LocalFileSystem;

internal static class RootPathResolver
{
    public static string CanonicalizeRoot(string physicalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalPath);

        var fullPath = Path.GetFullPath(physicalPath);
        var root = Path.GetPathRoot(fullPath);
        return string.Equals(fullPath, root, PathComparison)
            ? fullPath
            : Path.TrimEndingDirectorySeparator(fullPath);
    }

    public static string ResolveCandidatePath(ConfiguredLocalLibraryRoot root, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        if (Path.IsPathRooted(relativePath) || Path.IsPathFullyQualified(relativePath) || HasDrivePrefix(relativePath))
        {
            throw new ArgumentException("A local library candidate path must be relative to its configured root.", nameof(relativePath));
        }

        _ = new LibraryFileReference(root.RootKey, relativePath);
        var candidatePath = Path.GetFullPath(relativePath, root.CanonicalPhysicalPath);
        EnsureContained(root.CanonicalPhysicalPath, candidatePath);
        return candidatePath;
    }

    public static string GetContainedRelativePath(ConfiguredLocalLibraryRoot root, string candidatePath)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);

        var canonicalCandidatePath = Path.GetFullPath(candidatePath);
        EnsureContained(root.CanonicalPhysicalPath, canonicalCandidatePath);
        var relativePath = Path.GetRelativePath(root.CanonicalPhysicalPath, canonicalCandidatePath);
        _ = new LibraryFileReference(root.RootKey, relativePath);
        return relativePath;
    }

    private static void EnsureContained(string canonicalRoot, string canonicalCandidatePath)
    {
        var relativePath = Path.GetRelativePath(canonicalRoot, canonicalCandidatePath);
        if (Path.IsPathRooted(relativePath) || relativePath == ".." || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, PathComparison) || relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, PathComparison))
        {
            throw new ArgumentException("The local library candidate path resolves outside its configured root.", nameof(canonicalCandidatePath));
        }
    }

    private static bool HasDrivePrefix(string value) =>
        value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':';

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
