using Hispania.Library.Contracts;
using Hispania.Media.Domain.Enrichment;
using Hispania.Media.Domain.Identity;
using Hispania.Metadata.Application;
using Hispania.Metadata.Contracts;
using Hispania.Metadata.Tmdb;

namespace Hispania.Server;

public static class TmdbMovieMetadataEndpoints
{
    public static IEndpointRouteBuilder MapTmdbMovieMetadataEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/media/{mediaEntityId}/metadata/tmdb/search", SearchAsync);
        endpoints.MapPost("/api/v1/media/{mediaEntityId}/metadata/tmdb/select", SelectAsync);
        endpoints.MapPost("/api/v1/media/{mediaEntityId}/metadata/tmdb/confirm", ConfirmAsync);
        endpoints.MapPost("/api/v1/media/{mediaEntityId}/metadata/tmdb/refresh", RefreshAsync);
        endpoints.MapGet("/api/v1/media/{mediaEntityId}/metadata/tmdb", StateAsync);
        return endpoints;
    }

    private static async Task<IResult> SearchAsync(Guid mediaEntityId, string? query, int? year, IMediaCatalogReader catalog, IServiceProvider services, CancellationToken cancellationToken)
    {
        var entry = FindEntry(mediaEntityId, catalog);
        if (entry is null) return Results.NotFound();
        var application = services.GetService<MovieMetadataApplicationService>();
        if (application is null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        var signals = MovieLocalSearchSignalParser.Parse(string.IsNullOrWhiteSpace(query) ? entry.DisplayName : query);
        var result = await application.SearchMovieMetadataAsync(new MovieSearchRequest(signals.Query, year ?? signals.Year, null), cancellationToken);
        return result is MovieProviderSuccess<IReadOnlyCollection<MovieSearchCandidate>> success
            ? Results.Ok(success.Value.Select(CandidateResponse.From))
            : ProviderFailure(result);
    }

    private static async Task<IResult> SelectAsync(Guid mediaEntityId, TmdbMovieSelectionRequest request, IMediaCatalogReader catalog, IServiceProvider services, CancellationToken cancellationToken)
    {
        var entry = FindEntry(mediaEntityId, catalog);
        if (entry is null) return Results.NotFound();
        var application = services.GetService<MovieMetadataApplicationService>();
        if (application is null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        var identifier = Identifier(request.TmdbMovieId);
        try { await application.SelectMovieMetadataCandidateAsync(entry.Media.Work.Id, identifier, DateTimeOffset.UtcNow, cancellationToken: cancellationToken); }
        catch (InvalidOperationException) { return Results.BadRequest(); }
        return await StateAsync(mediaEntityId, catalog, services, cancellationToken);
    }

    private static async Task<IResult> ConfirmAsync(Guid mediaEntityId, TmdbMovieSelectionRequest request, IMediaCatalogReader catalog, IServiceProvider services, CancellationToken cancellationToken)
    {
        var entry = FindEntry(mediaEntityId, catalog);
        if (entry is null) return Results.NotFound();
        var application = services.GetService<MovieMetadataApplicationService>();
        if (application is null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        var association = await AssociationAsync(entry.Media.Work.Id, Identifier(request.TmdbMovieId), services, cancellationToken);
        if (association is null) return Results.NotFound();
        MovieMetadataConfirmationResult confirmation;
        try { confirmation = await application.ConfirmMovieMetadataCandidateAsync(association.Id, DateTimeOffset.UtcNow, cancellationToken); }
        catch (InvalidOperationException) { return Results.Conflict(); }
        if (confirmation.Conflict is not null) return Results.Conflict(confirmation.Conflict);
        return await StateAsync(mediaEntityId, catalog, services, cancellationToken);
    }

    private static async Task<IResult> RefreshAsync(Guid mediaEntityId, IMediaCatalogReader catalog, IServiceProvider services, CancellationToken cancellationToken)
    {
        var entry = FindEntry(mediaEntityId, catalog);
        if (entry is null) return Results.NotFound();
        var application = services.GetService<MovieMetadataApplicationService>();
        if (application is null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        var confirmed = (await services.GetRequiredService<IMetadataEnrichmentStore>().LoadAsync(cancellationToken)).Associations.SingleOrDefault(association => association.Target == EnrichmentTarget.For(entry.Media.Work.Id) && IsTmdbMovie(association.Identifier) && association.CurrentStatus == ExternalIdentifierAssociationStatus.Confirmed);
        if (confirmed is null) return Results.Conflict();
        var result = await application.FetchAndApplyMovieMetadataAsync(confirmed.Id, cancellationToken);
        return result is MovieProviderSuccess<NormalizedMovieDocument> ? await StateAsync(mediaEntityId, catalog, services, cancellationToken) : ProviderFailure(result);
    }

    private static async Task<IResult> StateAsync(Guid mediaEntityId, IMediaCatalogReader catalog, IServiceProvider services, CancellationToken cancellationToken)
    {
        var entry = FindEntry(mediaEntityId, catalog);
        if (entry is null) return Results.NotFound();
        var store = services.GetRequiredService<IMetadataEnrichmentStore>();
        var target = EnrichmentTarget.For(entry.Media.Work.Id);
        var snapshot = await store.LoadAsync(cancellationToken);
        var associations = snapshot.Associations.Where(association => association.Target == target && IsTmdbMovie(association.Identifier)).ToArray();
        var candidate = associations.SingleOrDefault(association => association.CurrentStatus == ExternalIdentifierAssociationStatus.Candidate);
        var confirmed = associations.SingleOrDefault(association => association.CurrentStatus == ExternalIdentifierAssociationStatus.Confirmed);
        var refresh = confirmed is null ? null : snapshot.RefreshStates.Where(state => state.Target == target && state.Provider == TmdbMovieMetadataProvider.Provider && state.ExternalIdentifier == confirmed.Identifier).OrderByDescending(state => state.LastAttemptAt).FirstOrDefault();
        return Results.Ok(new TmdbIdentificationStateResponse("tmdb", AssociationResponse.From(candidate), AssociationResponse.From(confirmed), RefreshResponse.From(refresh)));
    }

    private static async ValueTask<ExternalIdentifierAssociation?> AssociationAsync(WorkId workId, ExternalIdentifier identifier, IServiceProvider services, CancellationToken cancellationToken) => (await services.GetRequiredService<IMetadataEnrichmentStore>().LoadAsync(cancellationToken)).Associations.SingleOrDefault(association => association.Target == EnrichmentTarget.For(workId) && association.Identifier == identifier);
    private static MediaCatalogEntry? FindEntry(Guid mediaEntityId, IMediaCatalogReader catalog) => mediaEntityId == Guid.Empty ? null : catalog.GetById(new MediaEntityId(mediaEntityId));
    private static ExternalIdentifier Identifier(string value) => new(TmdbMovieMetadataProvider.Provider, TmdbMovieMetadataProvider.MovieNamespace, value);
    private static bool IsTmdbMovie(ExternalIdentifier identifier) => identifier.Provider == TmdbMovieMetadataProvider.Provider && identifier.Namespace == TmdbMovieMetadataProvider.MovieNamespace;
    private static IResult ProviderFailure(MovieProviderResult result) => result switch { MovieProviderUnavailable or MovieProviderTimeout => Results.StatusCode(StatusCodes.Status503ServiceUnavailable), MovieProviderUnsupported => Results.NotFound(), _ => Results.BadRequest() };
}

public sealed record TmdbMovieSelectionRequest(string TmdbMovieId);
public sealed record ExternalIdentifierResponse(string Provider, string Namespace, string Value) { internal static ExternalIdentifierResponse From(ExternalIdentifier identifier) => new(identifier.Provider.Value, identifier.Namespace.Value, identifier.Value); }
public sealed record CandidateResponse(string Provider, ExternalIdentifierResponse ExternalIdentifier, string Title, string? OriginalTitle, DateOnly? ReleaseDate) { internal static CandidateResponse From(MovieSearchCandidate candidate) => new(candidate.ExternalIdentifier.Provider.Value, ExternalIdentifierResponse.From(candidate.ExternalIdentifier), candidate.Title, candidate.OriginalTitle, candidate.ReleaseDate); }
public sealed record AssociationResponse(ExternalIdentifierResponse ExternalIdentifier, string Status) { internal static AssociationResponse? From(ExternalIdentifierAssociation? association) => association is null ? null : new(ExternalIdentifierResponse.From(association.Identifier), association.CurrentStatus.ToString()); }
public sealed record RefreshResponse(string Status, DateTimeOffset? LastAttempt, DateTimeOffset? LastSuccess) { internal static RefreshResponse? From(MetadataRefreshState? state) => state is null ? null : new(state.Status.ToString(), state.LastAttemptAt, state.LastSuccessAt); }
public sealed record TmdbIdentificationStateResponse(string Provider, AssociationResponse? Candidate, AssociationResponse? Confirmed, RefreshResponse? Refresh);
