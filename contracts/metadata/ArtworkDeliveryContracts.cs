using Hispania.Media.Domain.Enrichment;

namespace Hispania.Metadata.Contracts;

public enum ArtworkDeliveryVariant { PosterCard, BackdropDetail }

public abstract record ArtworkContentResult
{
    public sealed record Success(Stream Content, string ContentType, long? ContentLength, IAsyncDisposable Lifetime) : ArtworkContentResult;
    public sealed record NotFound : ArtworkContentResult;
    public sealed record UnsupportedScheme : ArtworkContentResult;
    public sealed record UpstreamUnavailable : ArtworkContentResult;
    public sealed record Timeout : ArtworkContentResult;
    public sealed record InvalidResponse : ArtworkContentResult;
    public sealed record Cancelled : ArtworkContentResult;
}

public interface IArtworkContentResolver
{
    string Scheme { get; }

    ValueTask<ArtworkContentResult> ResolveAsync(ArtworkLocator locator, ArtworkDeliveryVariant variant, CancellationToken cancellationToken = default);
}
