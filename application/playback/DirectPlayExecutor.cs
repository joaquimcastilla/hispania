using Hispania.Library.Contracts;
using Hispania.Media.Domain.TechnicalRepresentations;
using Hispania.Playback.Contracts;

namespace Hispania.Playback.Application;

public sealed class DirectPlayExecutor : IDirectPlayExecutor
{
    private readonly IMediaCatalogReader _catalog;
    private readonly ISourceContentAccessor _accessor;

    public DirectPlayExecutor(IMediaCatalogReader catalog, ISourceContentAccessor accessor)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
    }

    public async ValueTask<DirectPlayExecutionResult> ExecuteAsync(DirectPlayPlan plan, ByteRangeRequest range, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(range);
        cancellationToken.ThrowIfCancellationRequested();

        var entry = _catalog.GetById(plan.MediaEntityId);
        if (entry is null || !IsStillValid(entry, plan))
        {
            return new PlanNoLongerValid();
        }

        var opened = await _accessor.OpenReadAsync(entry.Media.Source, cancellationToken).ConfigureAwait(false);
        if (opened is not SourceContentOpened { Content: var content })
        {
            return new SourceUnavailable();
        }

        if (!content.CanSeek || !SnapshotMatches(entry.Snapshot, content.Facts))
        {
            await content.DisposeAsync().ConfigureAwait(false);
            return new SourceChanged();
        }

        var resolution = ResolveRange(range, content.Length);
        if (resolution is RangeResolution.NotSatisfiable)
        {
            await content.DisposeAsync().ConfigureAwait(false);
            return new RangeNotSatisfiable(content.Length);
        }

        if (resolution is RangeResolution.Full)
        {
            return new FullContent(content, content.Length);
        }

        var partial = (RangeResolution.Partial)resolution;
        try
        {
            content.Stream.Seek(partial.Start, SeekOrigin.Begin);
            var bounded = new ReadableMediaContent(
                new BoundedReadStream(content.Stream, partial.Length),
                content.Facts);
            return new PartialContent(bounded, content.Length, partial.Start, partial.End, partial.Length);
        }
        catch (IOException)
        {
            await content.DisposeAsync().ConfigureAwait(false);
            return new SourceUnavailable();
        }
        catch (NotSupportedException)
        {
            await content.DisposeAsync().ConfigureAwait(false);
            return new SourceUnavailable();
        }
    }

    private static bool IsStillValid(MediaCatalogEntry entry, DirectPlayPlan plan)
    {
        if (entry.Id != plan.MediaEntityId || entry.Media.Source.Id != plan.MediaSourceId)
        {
            return false;
        }

        var video = entry.Media.VideoAsset.TechnicalRepresentations.SingleOrDefault(item => item.Id == plan.VideoTechnicalRepresentationId);
        var audio = entry.Media.AudioAsset.TechnicalRepresentations.SingleOrDefault(item => item.Id == plan.AudioTechnicalRepresentationId);
        return video is not null && audio is not null && DirectPlayPlanner.IsH264(video.Profile) && DirectPlayPlanner.IsAac(audio.Profile) &&
               DirectPlayPlanner.FindSharedMp4Source(video, audio, plan.MediaSourceId) == plan.MediaSourceId;
    }

    private static bool SnapshotMatches(LibrarySourceSnapshot snapshot, SourceContentFacts current) =>
        snapshot.ByteLength is long byteLength && snapshot.LastModifiedUtc is DateTimeOffset lastModified &&
        byteLength == current.Length && lastModified == current.LastModifiedUtc;

    private static RangeResolution ResolveRange(ByteRangeRequest range, long length) => range switch
    {
        ByteRangeRequest.Full => new RangeResolution.Full(),
        ByteRangeRequest.From from when from.Start >= length => new RangeResolution.NotSatisfiable(),
        ByteRangeRequest.From from => new RangeResolution.Partial(from.Start, length - 1),
        ByteRangeRequest.Closed closed when closed.End < closed.Start || closed.Start >= length => new RangeResolution.NotSatisfiable(),
        ByteRangeRequest.Closed closed => new RangeResolution.Partial(closed.Start, Math.Min(closed.End, length - 1)),
        ByteRangeRequest.Suffix when length == 0 => new RangeResolution.NotSatisfiable(),
        ByteRangeRequest.Suffix suffix when suffix.Length >= length => new RangeResolution.Partial(0, length - 1),
        ByteRangeRequest.Suffix suffix => new RangeResolution.Partial(length - suffix.Length, length - 1),
        _ => throw new ArgumentOutOfRangeException(nameof(range))
    };

    private abstract record RangeResolution
    {
        public sealed record Full : RangeResolution;

        public sealed record NotSatisfiable : RangeResolution;

        public sealed record Partial(long Start, long End) : RangeResolution
        {
            public long Length => checked(End - Start + 1);
        }
    }
}
