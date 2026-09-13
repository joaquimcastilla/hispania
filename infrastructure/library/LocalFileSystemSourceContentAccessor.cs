using Hispania.Library.Contracts;
using Hispania.Playback.Contracts;

namespace Hispania.Library.LocalFileSystem;

public sealed class LocalFileSystemSourceContentAccessor : ISourceContentAccessor
{
    private readonly LocalLibraryRootConfiguration _configuration;

    public LocalFileSystemSourceContentAccessor(LocalLibraryRootConfiguration configuration) =>
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

    public ValueTask<SourceContentAccessResult> OpenReadAsync(
        Hispania.Media.Domain.Sources.MediaSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();

        if (!LibrarySourceLocator.TryGetFileReference(source.Locator, out var file) || file is null)
        {
            return ValueTask.FromResult<SourceContentAccessResult>(new SourceContentUnsupported());
        }

        var root = _configuration.Roots.SingleOrDefault(item => string.Equals(item.RootKey, file.RootKey, StringComparison.Ordinal));
        if (root is null)
        {
            return ValueTask.FromResult<SourceContentAccessResult>(new SourceContentUnavailable());
        }

        try
        {
            RootPathResolver.EnsureAccessibleRoot(root);
            var path = RootPathResolver.ResolveCandidatePath(root, file.RelativePath);
            if (!File.Exists(path) || RootPathResolver.IsReparsePoint(path))
            {
                return ValueTask.FromResult<SourceContentAccessResult>(new SourceContentUnavailable());
            }

            var info = new FileInfo(path);
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65_536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var facts = new SourceContentFacts(info.Length, info.LastWriteTimeUtc);
            return ValueTask.FromResult<SourceContentAccessResult>(new SourceContentOpened(new ReadableMediaContent(stream, facts)));
        }
        catch (DirectoryNotFoundException)
        {
            return ValueTask.FromResult<SourceContentAccessResult>(new SourceContentUnavailable());
        }
        catch (FileNotFoundException)
        {
            return ValueTask.FromResult<SourceContentAccessResult>(new SourceContentUnavailable());
        }
        catch (UnauthorizedAccessException)
        {
            return ValueTask.FromResult<SourceContentAccessResult>(new SourceContentInaccessible());
        }
        catch (IOException)
        {
            return ValueTask.FromResult<SourceContentAccessResult>(new SourceContentUnavailable());
        }
        catch (ArgumentException)
        {
            return ValueTask.FromResult<SourceContentAccessResult>(new SourceContentInaccessible());
        }
    }
}
