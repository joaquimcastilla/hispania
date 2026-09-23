using Hispania.Library.Contracts;
using Hispania.Media.Domain.Enrichment;
using Hispania.Metadata.Contracts;
using Microsoft.Extensions.Logging;

namespace Hispania.Metadata.Application;

public sealed class MovieMetadataAutoEnrichmentService(
    IMediaCatalogReader catalog,
    IMovieMetadataProvider provider,
    IMetadataEnrichmentStore store,
    MovieMetadataApplicationService application,
    IMovieMetadataAutoMatcher matcher,
    ILogger<MovieMetadataAutoEnrichmentService>? logger = null)
{
    private static readonly Action<ILogger, Guid, Guid, Exception?> LogItemFailure = LoggerMessage.Define<Guid, Guid>(LogLevel.Error, new EventId(1, "MovieEnrichmentItemFailed"), "Automatic movie enrichment failed. WorkId={WorkId} MediaId={MediaId}");
    private static readonly Action<ILogger, Guid, Guid, Guid, string, string, string, Exception?> LogItemConflict = LoggerMessage.Define<Guid, Guid, Guid, string, string, string>(LogLevel.Warning, new EventId(2, "MovieEnrichmentIdentifierConflict"), "Automatic movie enrichment skipped conflicting metadata. WorkId={WorkId} MediaId={MediaId} ExistingWorkId={ExistingWorkId} Provider={Provider} Namespace={Namespace} Identifier={Identifier}");
    public Task<MovieMetadataEnrichmentRunResult> EnrichCatalogAsync(CancellationToken cancellationToken = default) => EnrichCatalogAsync(false, cancellationToken);

    /// <summary>Refreshes already identified movies too; movies without a confirmed provider id are matched as usual.</summary>
    public Task<MovieMetadataEnrichmentRunResult> RefreshEntireCatalogAsync(CancellationToken cancellationToken = default) => EnrichCatalogAsync(true, cancellationToken);

    /// <summary>Queries new works and refreshes confirmed works when this provider's mapping is stale or has no successful refresh.</summary>
    public Task<MovieMetadataEnrichmentRunResult> EnrichUnidentifiedCatalogAsync(CancellationToken cancellationToken = default) => EnrichCatalogAsync(false, true, cancellationToken);

    public async Task<MovieMetadataEnrichmentRunResult> EnrichCatalogAsync(bool forceRefresh, CancellationToken cancellationToken = default)
        => await EnrichCatalogAsync(forceRefresh, onlyUnidentified: false, cancellationToken);

    private async Task<MovieMetadataEnrichmentRunResult> EnrichCatalogAsync(bool forceRefresh, bool onlyUnidentified, CancellationToken cancellationToken)
    {
        var snapshot = await store.LoadAsync(cancellationToken);
        var failures = 0;
        var conflicts = 0;
        var itemsWithoutMetadata = new List<MovieMetadataItemIssue>();
        var entries = catalog.GetAll();
        if (onlyUnidentified)
            entries = entries.Where(entry =>
            {
                var target = EnrichmentTarget.For(entry.Media.Work.Id);
                var confirmed = snapshot.Associations.FirstOrDefault(x => x.Target == target && x.Identifier.Provider == provider.Descriptor.ProviderCode && x.CurrentStatus == ExternalIdentifierAssociationStatus.Confirmed);
                return confirmed is null || !snapshot.RefreshStates.Any(x => x.Target == target && x.ExternalIdentifier == confirmed.Identifier && x.MappingVersion == provider.Descriptor.MappingVersion && x.Status == MetadataRefreshStatus.Succeeded);
            }).ToArray();
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var issue = await EnrichEntryAsync(entry, snapshot, forceRefresh, cancellationToken);
                if (issue is not null)
                {
                    itemsWithoutMetadata.Add(issue);
                    if (issue.ReasonCode == "identifier-conflict") conflicts++;
                    else if (issue.ReasonCode is "provider-error" or "metadata-fetch-failed") failures++;
                }
                snapshot = await store.LoadAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                failures++;
                itemsWithoutMetadata.Add(Issue(entry, "provider-error", "No se pudo consultar o guardar la metadata."));
                if (logger is not null) LogItemFailure(logger, entry.Media.Work.Id.Value, entry.Id.Value, exception);
            }
        }
        return new MovieMetadataEnrichmentRunResult(entries.Count, conflicts, failures, itemsWithoutMetadata);
    }

    private async Task<MovieMetadataItemIssue?> EnrichEntryAsync(MediaCatalogEntry entry, MetadataEnrichmentSnapshot snapshot, bool forceRefresh, CancellationToken cancellationToken)
    {
            var target = EnrichmentTarget.For(entry.Media.Work.Id);
            var confirmed = snapshot.Associations.FirstOrDefault(x => x.Target == target && x.Identifier.Provider == provider.Descriptor.ProviderCode && x.CurrentStatus == ExternalIdentifierAssociationStatus.Confirmed);
            if (confirmed is not null)
            {
                if (forceRefresh || !snapshot.RefreshStates.Any(x => x.Target == target && x.ExternalIdentifier == confirmed.Identifier && x.MappingVersion == provider.Descriptor.MappingVersion && x.Status == MetadataRefreshStatus.Succeeded))
                {
                    var refreshed = await application.FetchAndApplyMovieMetadataAsync(confirmed.Id, cancellationToken);
                    if (refreshed is not MovieProviderSuccess<NormalizedMovieDocument>) return Issue(entry, "metadata-fetch-failed", "TMDB no pudo entregar la metadata de la identificación confirmada.");
                }
                return null;
            }

            if (snapshot.Associations.Any(x => x.Target == target && x.CurrentStatus == ExternalIdentifierAssociationStatus.Rejected && x.LifecycleEntries[^1].Reason?.StartsWith("external-identifier-conflict|", StringComparison.Ordinal) == true))
                return Issue(entry, "identifier-conflict-existing", "La identificación TMDB está en conflicto y requiere revisión.");

            var signals = MovieLocalSearchSignalParser.Parse(entry.DisplayName);
            var search = await provider.SearchAsync(new MovieSearchRequest(signals.Query, signals.Year, entry.Media.Timeline.Duration?.Value), cancellationToken);
            if (search is not MovieProviderSuccess<IReadOnlyCollection<MovieSearchCandidate>> candidates) return Issue(entry, "provider-error", ProviderDetail(search));
            var matchResult = matcher.Match(signals, candidates.Value);
            if (matchResult is MovieAutoMatchResult.NoMatch) return Issue(entry, "no-match", "El proveedor no devolvió una coincidencia exacta de título y año.");
            if (matchResult is MovieAutoMatchResult.Ambiguous) return Issue(entry, "ambiguous", "El proveedor devolvió varias coincidencias posibles.");
            var match = (MovieAutoMatchResult.Confirmed)matchResult;

            if (snapshot.Associations.Any(x => x.Target == target && x.Identifier == match.Candidate.ExternalIdentifier && x.CurrentStatus is ExternalIdentifierAssociationStatus.Rejected or ExternalIdentifierAssociationStatus.Invalidated))
                return Issue(entry, "no-match", "La coincidencia anterior fue rechazada o invalidada.");

            await application.SelectMovieMetadataCandidateAsync(entry.Media.Work.Id, match.Candidate.ExternalIdentifier, DateTimeOffset.UtcNow, cancellationToken: cancellationToken);
            var afterSelection = await store.LoadAsync(cancellationToken);
            var association = afterSelection.Associations.Single(x => x.Target == target && x.Identifier == match.Candidate.ExternalIdentifier);
            var confirmation = await application.ConfirmMovieMetadataCandidateAsync(association.Id, DateTimeOffset.UtcNow, cancellationToken);
            if (confirmation.Conflict is not null)
            {
                if (logger is not null) LogItemConflict(logger, confirmation.Conflict.AttemptedWorkId.Value, entry.Id.Value, confirmation.Conflict.ExistingWorkId.Value, match.Candidate.ExternalIdentifier.Provider.Value, match.Candidate.ExternalIdentifier.Namespace.Value, match.Candidate.ExternalIdentifier.Value, null);
                return Issue(entry, "identifier-conflict", "La identificación TMDB ya está asociada a otra película.");
            }
            var applied = await application.FetchAndApplyMovieMetadataAsync(association.Id, cancellationToken);
            return applied is MovieProviderSuccess<NormalizedMovieDocument> ? null : Issue(entry, "metadata-fetch-failed", "TMDB encontró la película, pero no pudo entregar su metadata.");
    }

    private static MovieMetadataItemIssue Issue(MediaCatalogEntry entry, string reasonCode, string detail) => new(Path.GetFileNameWithoutExtension(entry.DisplayName), reasonCode, detail);

    private static string ProviderDetail(MovieProviderResult result) => result switch
    {
        MovieProviderUnavailable or MovieProviderTimeout => "El proveedor de metadata no está disponible temporalmente.",
        MovieProviderUnsupported => "El proveedor rechazó la solicitud o la identificación.",
        MovieProviderInvalidResponse => "El proveedor devolvió una respuesta no válida.",
        _ => "El proveedor no pudo completar la búsqueda."
    };
}

public sealed record MovieMetadataItemIssue(string DisplayName, string ReasonCode, string Detail);
public sealed record MovieMetadataEnrichmentRunResult(int ProcessedItems, int Conflicts, int Failures, IReadOnlyCollection<MovieMetadataItemIssue> ItemsWithoutMetadata);
