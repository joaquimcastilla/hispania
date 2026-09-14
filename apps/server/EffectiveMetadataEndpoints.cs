using Hispania.Library.Contracts;
using Hispania.Media.Domain.Enrichment;
using Hispania.Media.Domain.Identity;
using Hispania.Metadata.Contracts;

namespace Hispania.Server;

public static class EffectiveMetadataEndpoints
{
    public static IEndpointRouteBuilder MapEffectiveMetadataEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/media/{mediaEntityId}/metadata", GetAsync);
        return endpoints;
    }

    private static async Task<IResult> GetAsync(Guid mediaEntityId, IMediaCatalogReader catalog, IEffectiveMetadataProjectionReader reader, CancellationToken cancellationToken)
    {
        if (mediaEntityId == Guid.Empty) return Results.NotFound();
        var entry = catalog.GetById(new MediaEntityId(mediaEntityId));
        if (entry is null) return Results.NotFound();

        var workId = entry.Media.Work.Id;
        var projection = await reader.ReadAsync(EnrichmentTarget.For(workId), cancellationToken);
        return Results.Ok(EffectiveMediaMetadataResponse.From(entry.Id, workId, projection));
    }
}

public sealed record EffectiveMediaMetadataResponse(
    Guid MediaEntityId,
    Guid WorkId,
    string PolicyVersion,
    EffectiveMetadataFieldResponse<string> Title,
    EffectiveMetadataFieldResponse<string> OriginalTitle,
    EffectiveMetadataFieldResponse<string> Description,
    EffectiveMetadataFieldResponse<DateOnly?> ReleaseDate,
    EffectiveArtworkResponse Artwork)
{
    internal static EffectiveMediaMetadataResponse From(MediaEntityId mediaEntityId, WorkId workId, EffectiveMetadataProjection projection) => new(
        mediaEntityId.Value,
        workId.Value,
        projection.PolicyVersion,
        EffectiveMetadataFieldResponse<string>.From(projection.Title),
        EffectiveMetadataFieldResponse<string>.From(projection.OriginalTitle),
        EffectiveMetadataFieldResponse<string>.From(projection.Description),
        EffectiveMetadataFieldResponse<DateOnly?>.From(projection.ReleaseDate),
        new EffectiveArtworkResponse(
            EffectiveArtworkItemResponse.From(projection.Poster),
            EffectiveArtworkItemResponse.From(projection.Backdrop),
            EffectiveArtworkItemResponse.From(projection.Logo)));
}

public sealed record EffectiveMetadataFieldResponse<T>(
    T? Value,
    Guid? WinnerAssertionId,
    string? Origin,
    string? Provider,
    EffectiveExternalIdentifierResponse? ExternalIdentifier,
    DateTimeOffset? ObservedAt,
    decimal? Confidence,
    string ResolutionReason)
{
    internal static EffectiveMetadataFieldResponse<T> From(EffectiveMetadataValue<T> value) => new(
        value.Value,
        value.WinnerAssertionId?.Value,
        value.SourceProvenance?.Origin.ToString(),
        value.SourceProvenance?.Provider?.Value,
        EffectiveExternalIdentifierResponse.From(value.SourceProvenance?.RelatedExternalIdentifier),
        value.SourceProvenance?.ObservedAt,
        value.SourceProvenance?.Confidence,
        value.ResolutionReason.ToString());
}

public sealed record EffectiveArtworkResponse(
    EffectiveArtworkItemResponse Poster,
    EffectiveArtworkItemResponse Backdrop,
    EffectiveArtworkItemResponse Logo);

public sealed record EffectiveArtworkItemResponse(
    string Role,
    string? LocatorScheme,
    string? LocatorValue,
    Guid? WinnerArtworkReferenceId,
    string? Origin,
    string? Provider,
    EffectiveExternalIdentifierResponse? ExternalIdentifier,
    DateTimeOffset? ObservedAt,
    decimal? Confidence,
    string ResolutionReason)
{
    internal static EffectiveArtworkItemResponse From(EffectiveArtworkSelection selection) => new(
        selection.Role.Code,
        selection.Locator?.Scheme,
        selection.Locator?.OpaqueValue,
        selection.WinnerArtworkReferenceId?.Value,
        selection.SourceProvenance?.Origin.ToString(),
        selection.SourceProvenance?.Provider?.Value,
        EffectiveExternalIdentifierResponse.From(selection.SourceProvenance?.RelatedExternalIdentifier),
        selection.SourceProvenance?.ObservedAt,
        selection.SourceProvenance?.Confidence,
        selection.ResolutionReason.ToString());
}

public sealed record EffectiveExternalIdentifierResponse(string Provider, string Namespace, string Value)
{
    internal static EffectiveExternalIdentifierResponse? From(ExternalIdentifier? identifier) => identifier is null
        ? null
        : new(identifier.Provider.Value, identifier.Namespace.Value, identifier.Value);
}
