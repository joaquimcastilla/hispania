using System.Runtime.CompilerServices;
using Hispania.Library.Contracts;

namespace Hispania.Library.LocalFileSystem;

/// <summary>
/// Discovers MP4 files beneath configured local roots. Reparse-point files and directories are never followed or emitted.
/// </summary>
public sealed class LocalFileSystemMediaDiscovery : IMediaDiscovery
{
    private readonly LocalLibraryRootConfiguration _configuration;

    public LocalFileSystemMediaDiscovery(LocalLibraryRootConfiguration configuration)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    public async IAsyncEnumerable<DiscoveredMediaCandidate> DiscoverAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var root in _configuration.Roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateRoot(root);

            var directories = new Stack<string>();
            directories.Push(root.CanonicalPhysicalPath);

            while (directories.TryPop(out var directory))
            {
                cancellationToken.ThrowIfCancellationRequested();

                foreach (var entry in GetDirectoryEntries(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    FileAttributes attributes;
                    try
                    {
                        attributes = File.GetAttributes(entry);
                    }
                    catch (IOException)
                    {
                        continue;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        continue;
                    }

                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        directories.Push(entry);
                        continue;
                    }

                    if (!string.Equals(Path.GetExtension(entry), ".mp4", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    DiscoveredMediaCandidate? candidate;
                    try
                    {
                        var relativePath = RootPathResolver.GetContainedRelativePath(root, entry);
                        _ = RootPathResolver.ResolveCandidatePath(root, relativePath);
                        var file = new FileInfo(entry);
                        candidate = new DiscoveredMediaCandidate(
                            new LibraryFileReference(root.RootKey, relativePath),
                            file.Length,
                            file.LastWriteTimeUtc);
                    }
                    catch (IOException)
                    {
                        continue;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        continue;
                    }
                    catch (ArgumentException)
                    {
                        continue;
                    }

                    yield return candidate;
                    await Task.Yield();
                }
            }
        }
    }

    private static void ValidateRoot(ConfiguredLocalLibraryRoot root)
    {
        if (!Directory.Exists(root.CanonicalPhysicalPath))
        {
            throw new DirectoryNotFoundException($"Configured local library root '{root.RootKey}' does not exist.");
        }

        var attributes = File.GetAttributes(root.CanonicalPhysicalPath);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException($"Configured local library root '{root.RootKey}' cannot be a reparse point.");
        }
    }

    private static string[] GetDirectoryEntries(string directory)
    {
        try
        {
            return Directory.GetFileSystemEntries(directory);
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }
}
