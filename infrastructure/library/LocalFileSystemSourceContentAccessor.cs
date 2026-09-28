using Hispania.Library.Contracts;
using Hispania.Playback.Contracts;
using Hispania.Media.Domain.Sources;

namespace Hispania.Library.LocalFileSystem;

public sealed class LocalFileSystemSourceContentAccessor : ISourceContentAccessor, IMediaSourceContentAccessV1
{
    private readonly LocalLibraryRootConfiguration _configuration;

    public LocalFileSystemSourceContentAccessor(LocalLibraryRootConfiguration configuration) =>
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

    public async ValueTask<SourceContentAccessResult> OpenReadAsync(MediaSource source, CancellationToken cancellationToken = default)
    {
        var result = await OpenMediaSourceReadAsync(source, cancellationToken).ConfigureAwait(false);
        return result switch
        {
            MediaSourceContentOpened opened => new SourceContentOpened(new ReadableMediaContent(opened.Content)),
            MediaSourceContentUnavailable => new SourceContentUnavailable(),
            MediaSourceContentUnsupported => new SourceContentUnsupported(),
            MediaSourceContentInaccessible => new SourceContentInaccessible(),
            _ => throw new InvalidOperationException("Unknown media source result.")
        };
    }

    public ValueTask<MediaSourceContentResult> OpenMediaSourceReadAsync(MediaSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();

        if (!LibrarySourceLocator.TryGetFileReference(source.Locator, out var file) || file is null)
        {
            return ValueTask.FromResult<MediaSourceContentResult>(new MediaSourceContentUnsupported());
        }

        var root = _configuration.Roots.SingleOrDefault(item => string.Equals(item.RootKey, file.RootKey, StringComparison.Ordinal));
        if (root is null)
        {
            return ValueTask.FromResult<MediaSourceContentResult>(new MediaSourceContentUnavailable());
        }

        try
        {
            RootPathResolver.EnsureAccessibleRoot(root);
            var path = RootPathResolver.ResolveCandidatePath(root, file.RelativePath);
            if (!File.Exists(path) || RootPathResolver.IsReparsePoint(path))
            {
                return ValueTask.FromResult<MediaSourceContentResult>(new MediaSourceContentUnavailable());
            }

            var info = new FileInfo(path);
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65_536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var facts = new MediaSourceContentFacts(info.Length, info.LastWriteTimeUtc);
            return ValueTask.FromResult<MediaSourceContentResult>(new MediaSourceContentOpened(new ReadOnlyMediaSourceContent(stream, facts)));
        }
        catch (DirectoryNotFoundException)
        {
            return ValueTask.FromResult<MediaSourceContentResult>(new MediaSourceContentUnavailable());
        }
        catch (FileNotFoundException)
        {
            return ValueTask.FromResult<MediaSourceContentResult>(new MediaSourceContentUnavailable());
        }
        catch (UnauthorizedAccessException)
        {
            return ValueTask.FromResult<MediaSourceContentResult>(new MediaSourceContentInaccessible());
        }
        catch (IOException)
        {
            return ValueTask.FromResult<MediaSourceContentResult>(new MediaSourceContentUnavailable());
        }
        catch (ArgumentException)
        {
            return ValueTask.FromResult<MediaSourceContentResult>(new MediaSourceContentInaccessible());
        }
    }

}
