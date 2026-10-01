using Hispania.Library.Contracts;
using Hispania.Media.Domain.Enrichment;
using Hispania.Media.Domain.Identity;
using Hispania.Metadata.Contracts;

namespace Hispania.Server;

public static class PersonMetadataEndpoints
{
    public static IEndpointRouteBuilder MapPersonMetadataEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/people/{personId:guid}", GetAsync)
            .AddEndpointFilter(AuthorizationEndpointFilters.RequirePermission(Hispania.Library.Contracts.MediaPermissionIds.LibraryRead, allowLegacyAdminBearer: false));
        return endpoints;
    }

    private static async Task<IResult> GetAsync(Guid personId, IRichMetadataStore store, IMediaCatalogReader catalog, IEffectiveMetadataProjectionReader metadata, IServiceProvider services, CancellationToken cancellationToken)
    {
        var provider = services.GetService<IPersonMetadataProvider>();
        if (personId == Guid.Empty) return Results.NotFound();
        var snapshot = await store.GetPersonAsync(personId, cancellationToken);
        if (snapshot is null) return Results.NotFound();
        var profile = snapshot.Person;
        var profileObservedAt = snapshot.ProfileObservedAt;
        if (provider is not null && (snapshot.ProfileObservedAt is null || DateTimeOffset.UtcNow - snapshot.ProfileObservedAt > TimeSpan.FromDays(30)))
        {
            try
            {
                profile = await provider.GetPersonAsync(snapshot.Person.ProviderId, cancellationToken) ?? snapshot.Person;
                profileObservedAt = DateTimeOffset.UtcNow;
                await store.SetPersonProfileAsync(personId, profile, profileObservedAt.Value, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { /* Retain the last locally stored identity and filmography if TMDB is unavailable. */ }
        }
        var credits = await store.GetCreditsForPersonAsync(personId, cancellationToken);
        var works = catalog.GetAll().Where(x => credits.Any(credit => credit.WorkId == x.Media.Work.Id)).ToArray();
        var projections = works.Length == 0 ? [] : await metadata.ReadManyAsync(works.Select(x => EnrichmentTarget.For(x.Media.Work.Id)).Distinct().ToArray(), cancellationToken);
        var projectionByWork = projections.ToDictionary(x => x.Target.WorkId!.Value);
        var filmography = works.Select(entry =>
        {
            projectionByWork.TryGetValue(entry.Media.Work.Id.Value, out var projection);
            var poster = projection is null ? null : EffectiveMediaMetadataResponse.From(entry.Id, entry.Media.Work.Id, projection).Artwork.Poster.DeliveryUrl;
            return new PersonFilmographyItem(entry.Id.Value, entry.DisplayName, projection?.Title.Value ?? entry.DisplayName, projection?.ReleaseDate.Value?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), poster);
        }).ToArray();
        return Results.Ok(new PersonMetadataResponse(profile.InternalPersonId ?? personId, profile.ProviderCode, profile.ProviderId, profile.Name, profile.ProfileArtworkLocator, profile.Biography, profile.Birthday, profile.Deathday, profile.PlaceOfBirth, profileObservedAt, filmography));
    }
}

public sealed record PersonMetadataResponse(Guid PersonId, string? ProviderCode, string ProviderId, string Name, string? ProfileArtworkLocator, string? Biography, DateOnly? Birthday, DateOnly? Deathday, string? PlaceOfBirth, DateTimeOffset? ProfileObservedAt, IReadOnlyCollection<PersonFilmographyItem> Filmography);
public sealed record PersonFilmographyItem(Guid MediaId, string DisplayName, string Title, string? ReleaseDate, string? PosterUrl);
