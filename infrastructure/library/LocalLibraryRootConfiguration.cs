using Hispania.Library.Contracts;

namespace Hispania.Library.LocalFileSystem;

public sealed class LocalLibraryRootConfiguration
{
    private readonly ConfiguredLocalLibraryRoot[] _roots;

    public LocalLibraryRootConfiguration(IEnumerable<LocalLibraryRoot> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);

        _roots = roots.Select(CreateConfiguredRoot).ToArray();
        if (_roots.Length == 0)
        {
            throw new ArgumentException("At least one local library root must be configured.", nameof(roots));
        }

        var duplicateRootKey = _roots
            .GroupBy(root => root.RootKey, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicateRootKey is not null)
        {
            throw new ArgumentException($"The local library root key '{duplicateRootKey.Key}' is configured more than once.", nameof(roots));
        }
    }

    internal IReadOnlyList<ConfiguredLocalLibraryRoot> Roots => _roots;

    private static ConfiguredLocalLibraryRoot CreateConfiguredRoot(LocalLibraryRoot root)
    {
        ArgumentNullException.ThrowIfNull(root);
        _ = new LibraryFileReference(root.RootKey, "validation.mp4");

        if (string.IsNullOrWhiteSpace(root.PhysicalPath))
        {
            throw new ArgumentException("A local library root physical path is required.", nameof(root));
        }

        return new ConfiguredLocalLibraryRoot(root.RootKey, RootPathResolver.CanonicalizeRoot(root.PhysicalPath));
    }
}

internal sealed record ConfiguredLocalLibraryRoot(string RootKey, string CanonicalPhysicalPath);
