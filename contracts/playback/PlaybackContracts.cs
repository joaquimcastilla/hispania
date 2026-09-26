using Hispania.Media.Domain.Identity;
using Hispania.Media.Domain.Sources;

namespace Hispania.Playback.Contracts;

public sealed record DirectPlayPlan(
    MediaEntityId MediaEntityId,
    MediaSourceId MediaSourceId,
    TechnicalRepresentationId VideoTechnicalRepresentationId,
    TechnicalRepresentationId AudioTechnicalRepresentationId);

public abstract record PlaybackPlanningResult;

public sealed record DirectPlayPlanned(DirectPlayPlan Plan) : PlaybackPlanningResult;

public sealed record PlaybackMediaNotFound(MediaEntityId MediaEntityId) : PlaybackPlanningResult;

public sealed record DirectPlayUnavailable(MediaEntityId MediaEntityId) : PlaybackPlanningResult;

public interface IPlaybackPlanner
{
    PlaybackPlanningResult Plan(MediaEntityId mediaEntityId);
}

public interface ICompatibleMediaTranscoderV1
{
    string ContractId => "hispania.playback.compatible-transcoder";
    Version ContractVersion => new(1, 0);
    ValueTask<CompatibleTranscodeResult> CreateCompatibleRepresentationAsync(MediaEntityId mediaEntityId, CancellationToken cancellationToken = default);
}

public abstract record CompatibleTranscodeResult;
/// <summary>A stable, opaque reference to generated content. It never contains a host-local path.</summary>
public sealed record TranscodedContentHandle(string Value)
{
    public static TranscodedContentHandle FromCacheKey(string key) => new(key);
}

public sealed record CompatibleTranscodeReady(TranscodedContentHandle Content, string ContentType, long Length) : CompatibleTranscodeResult;
public sealed record CompatibleTranscodeUnavailable : CompatibleTranscodeResult;

/// <summary>Playback-owned delivery contract for generated representations.</summary>
public interface ITranscodedContentStore
{
    ValueTask<ReadableMediaContent?> OpenReadAsync(TranscodedContentHandle content, CancellationToken cancellationToken = default);
}

public interface ITranscodingCapabilityHealthV1
{
    string ContractId => "hispania.playback.transcoding-health";
    Version ContractVersion => new(1, 0);
    TranscodingCapabilityHealthSnapshot GetSnapshot();
}

public sealed record TranscodingCapabilityHealthSnapshot(
    string Status,
    int ActiveJobs,
    long CompletedJobs,
    long FailedJobs,
    long RetryCount,
    long CancelledJobs,
    DateTimeOffset? LastSuccessUtc,
    DateTimeOffset? LastFailureUtc,
    string? LastFailureCode);

public abstract record ByteRangeRequest
{
    public sealed record Full : ByteRangeRequest;

    public sealed record From : ByteRangeRequest
    {
        public From(long start)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(start);

            Start = start;
        }

        public long Start { get; }
    }

    public sealed record Closed : ByteRangeRequest
    {
        public Closed(long start, long end)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(start);
            ArgumentOutOfRangeException.ThrowIfNegative(end);

            Start = start;
            End = end;
        }

        public long Start { get; }

        public long End { get; }
    }

    public sealed record Suffix : ByteRangeRequest
    {
        public Suffix(long length)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);

            Length = length;
        }

        public long Length { get; }
    }
}

public interface ISourceContentAccessor
{
    ValueTask<SourceContentAccessResult> OpenReadAsync(MediaSource source, CancellationToken cancellationToken = default);
}

public sealed record SourceContentFacts(long Length, DateTimeOffset? LastModifiedUtc);

public sealed class ReadableMediaContent : IAsyncDisposable
{
    private readonly ReadOnlyMediaSourceContent? owner;

    public ReadableMediaContent(Stream stream, SourceContentFacts facts)
    {
        Stream = stream ?? throw new ArgumentNullException(nameof(stream));
        Facts = facts ?? throw new ArgumentNullException(nameof(facts));
        if (!stream.CanRead || stream.CanWrite || facts.Length < 0) throw new ArgumentException("Playback content must be readable, read-only and have a valid length.");
    }

    public ReadableMediaContent(ReadOnlyMediaSourceContent inner)
        : this(inner.Content, new SourceContentFacts(inner.Facts.Length, inner.Facts.LastModifiedUtc)) => owner = inner;

    public Stream Stream { get; }
    public long Length => Facts.Length;
    public bool CanSeek => Stream.CanSeek;
    public SourceContentFacts Facts { get; }
    public ValueTask DisposeAsync() => owner?.DisposeAsync() ?? Stream.DisposeAsync();
}

public abstract record SourceContentAccessResult;
public sealed record SourceContentOpened(ReadableMediaContent Content) : SourceContentAccessResult;
public sealed record SourceContentUnavailable : SourceContentAccessResult;
public sealed record SourceContentUnsupported : SourceContentAccessResult;
public sealed record SourceContentInaccessible : SourceContentAccessResult;

public abstract record DirectPlayExecutionResult : IAsyncDisposable
{
    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return DisposeAsyncCore();
    }

    protected virtual ValueTask DisposeAsyncCore() => ValueTask.CompletedTask;
}

public sealed record FullContent(ReadableMediaContent Content, long TotalLength) : DirectPlayExecutionResult
{
    protected override async ValueTask DisposeAsyncCore()
    {
        await Content.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsyncCore().ConfigureAwait(false);
    }
}

public sealed record PartialContent(ReadableMediaContent Content, long TotalLength, long Start, long End, long SelectedLength) : DirectPlayExecutionResult
{
    protected override async ValueTask DisposeAsyncCore()
    {
        await Content.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsyncCore().ConfigureAwait(false);
    }
}

public sealed record RangeNotSatisfiable(long TotalLength) : DirectPlayExecutionResult;

public sealed record RangeNotSupported : DirectPlayExecutionResult;

public sealed record SourceUnavailable : DirectPlayExecutionResult;

public sealed record SourceChanged : DirectPlayExecutionResult;

public sealed record PlanNoLongerValid : DirectPlayExecutionResult;

public interface IDirectPlayExecutor
{
    ValueTask<DirectPlayExecutionResult> ExecuteAsync(DirectPlayPlan plan, ByteRangeRequest range, CancellationToken cancellationToken = default);
}
