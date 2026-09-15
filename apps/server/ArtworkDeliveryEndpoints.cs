using Hispania.Media.Domain.Identity;
using Hispania.Media.Domain.Enrichment;
using Hispania.Metadata.Contracts;

namespace Hispania.Server;

public static class ArtworkDeliveryEndpoints
{
    public static IEndpointRouteBuilder MapArtworkDeliveryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/artwork/{artworkReferenceId}", GetAsync);
        return endpoints;
    }

    private static async Task GetAsync(
        HttpContext context,
        string artworkReferenceId,
        IMetadataEnrichmentStore store,
        IEnumerable<IArtworkContentResolver> resolvers)
    {
        if (!Guid.TryParse(artworkReferenceId, out var value) || value == Guid.Empty)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (!TryParseVariant(context.Request.Query["variant"], out var variant))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var artwork = await store.GetArtworkReferenceByIdAsync(new ArtworkReferenceId(value), context.RequestAborted).ConfigureAwait(false);
        if (artwork is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var resolver = resolvers.SingleOrDefault(candidate => string.Equals(candidate.Scheme, artwork.Locator.Scheme, StringComparison.Ordinal));
        if (resolver is null)
        {
            context.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
            return;
        }

        var result = await resolver.ResolveAsync(artwork.Locator, variant, context.RequestAborted).ConfigureAwait(false);
        switch (result)
        {
            case ArtworkContentResult.Success success:
                await using (success.Lifetime.ConfigureAwait(false))
                {
                    context.Response.StatusCode = StatusCodes.Status200OK;
                    context.Response.ContentType = success.ContentType;
                    if (success.ContentLength is long length) context.Response.ContentLength = length;
                    context.Response.Headers.CacheControl = "public, max-age=86400";
                    try
                    {
                        await success.Content.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                    {
                        // The client ended the response; its cancellation is not a server error.
                    }
                }
                return;
            case ArtworkContentResult.NotFound:
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            case ArtworkContentResult.UnsupportedScheme:
                context.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
                return;
            case ArtworkContentResult.UpstreamUnavailable:
            case ArtworkContentResult.InvalidResponse:
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                return;
            case ArtworkContentResult.Timeout:
                context.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
                return;
            case ArtworkContentResult.Cancelled:
                if (!context.RequestAborted.IsCancellationRequested) context.Response.StatusCode = StatusCodes.Status499ClientClosedRequest;
                return;
            default:
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                return;
        }
    }

    private static bool TryParseVariant(string? input, out ArtworkDeliveryVariant variant)
    {
        switch (input)
        {
            case null:
            case "":
            case "poster-card":
                variant = ArtworkDeliveryVariant.PosterCard;
                return true;
            case "backdrop-detail":
                variant = ArtworkDeliveryVariant.BackdropDetail;
                return true;
            default:
                variant = default;
                return false;
        }
    }
}

internal static class ArtworkDeliveryUrl
{
    internal static string? For(EffectiveArtworkSelection selection, ArtworkDeliveryVariant variant) => selection.WinnerArtworkReferenceId is { } id
        ? $"/api/v1/artwork/{id.Value}?variant={variant switch { ArtworkDeliveryVariant.PosterCard => "poster-card", ArtworkDeliveryVariant.BackdropDetail => "backdrop-detail", _ => throw new ArgumentOutOfRangeException(nameof(variant)) }}"
        : null;
}
