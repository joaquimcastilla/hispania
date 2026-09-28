namespace Hispania.Media.Domain.Sources;

/// <summary>Versioned read-only source content boundary shared by media applications and source providers.</summary>
public interface IMediaSourceContentAccessV1
{
    string ContractId => "hispania.media.source-content-access";

    Version ContractVersion => new(1, 0);

    ValueTask<MediaSourceContentResult> OpenMediaSourceReadAsync(MediaSource source, CancellationToken cancellationToken = default);
}

public sealed record MediaSourceContentFacts(long Length, DateTimeOffset? LastModifiedUtc);

public sealed class ReadOnlyMediaSourceContent(Stream content, MediaSourceContentFacts facts) : IAsyncDisposable
{
    public Stream Content { get; } = content ?? throw new ArgumentNullException(nameof(content));
    public MediaSourceContentFacts Facts { get; } = facts ?? throw new ArgumentNullException(nameof(facts));
    public bool CanSeek => Content.CanSeek;
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

public abstract record MediaSourceContentResult;
public sealed record MediaSourceContentOpened(ReadOnlyMediaSourceContent Content) : MediaSourceContentResult;
public sealed record MediaSourceContentUnavailable : MediaSourceContentResult;
public sealed record MediaSourceContentUnsupported : MediaSourceContentResult;
public sealed record MediaSourceContentInaccessible : MediaSourceContentResult;
