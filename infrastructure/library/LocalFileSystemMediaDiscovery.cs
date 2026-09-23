using System.Runtime.CompilerServices;
using Hispania.Library.Contracts;

namespace Hispania.Library.LocalFileSystem;

/// <summary>
/// Discovers supported local video containers beneath configured roots. Reparse-point files and directories are never followed or emitted.
/// </summary>
public sealed class LocalFileSystemMediaDiscovery : IMediaDiscovery
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mkv", ".avi" };
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

                    if (!SupportedExtensions.Contains(Path.GetExtension(entry)))
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
        RootPathResolver.EnsureAccessibleRoot(root);
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
