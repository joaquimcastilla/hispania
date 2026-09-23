using Hispania.Media.Domain.Enrichment;
using Hispania.Media.Domain.Identity;
using Hispania.Metadata.Contracts;
using Microsoft.Extensions.Logging;

namespace Hispania.Metadata.Application;

public sealed class MovieMetadataApplicationService(IMovieMetadataProvider provider, IMetadataEnrichmentStore store, IRichMetadataStore? richStore = null, IMetadataSettingsStore? settingsStore = null, ILogger<MovieMetadataApplicationService>? logger = null, IPersonMetadataProvider? personProvider = null)
{
    private static readonly Action<ILogger, Guid, Guid, string, string, string, Exception?> LogIdentifierConflict = LoggerMessage.Define<Guid, Guid, string, string, string>(LogLevel.Warning, new EventId(1, nameof(ExternalIdentifierConflict)), "External identifier confirmation conflict. ExistingWorkId={ExistingWorkId} AttemptedWorkId={AttemptedWorkId} Provider={Provider} Namespace={Namespace} Identifier={Identifier}");
    public ValueTask<MovieProviderResult> SearchMovieMetadataAsync(MovieSearchRequest request, CancellationToken cancellationToken = default) => provider.SearchAsync(request, cancellationToken);

    public async ValueTask<bool> SelectMovieMetadataCandidateAsync(WorkId workId, ExternalIdentifier identifier, DateTimeOffset observedAt, decimal? confidence = null, CancellationToken cancellationToken = default)
    {
        EnsureProviderIdentifier(identifier);
        await EnsureWorkExistsAsync(workId, cancellationToken);
        var target = EnrichmentTarget.For(workId);
        var existing = (await store.LoadAsync(cancellationToken)).Associations.SingleOrDefault(association => association.Target == target && association.Identifier == identifier);
        var provenance = new MetadataProvenance(MetadataProvenanceOrigin.ExternalProvider, observedAt, provider.Descriptor.ProviderCode, identifier, confidence);
        if (existing is not null)
        {
            if (existing.CurrentStatus != ExternalIdentifierAssociationStatus.Rejected || existing.LifecycleEntries[^1].Reason?.StartsWith("external-identifier-conflict|", StringComparison.Ordinal) != true) return false;
            existing.ReopenRejectedIdentifierConflict(new ExternalIdentifierAssociationLifecycleEntry(ExternalIdentifierAssociationStatus.Candidate, observedAt, provenance, "Manually reopened after identifier conflict review."));
            return await store.ApplyAsync(new MetadataEnrichmentWrite([existing], [], [], null), cancellationToken);
        }
        var association = new ExternalIdentifierAssociation(ExternalIdentifierAssociationId.New(), identifier, target, new ExternalIdentifierAssociationLifecycleEntry(ExternalIdentifierAssociationStatus.Candidate, observedAt, provenance));
        return await store.ApplyAsync(new MetadataEnrichmentWrite([association], [], [], null), cancellationToken);
    }

    public async ValueTask<MovieMetadataConfirmationResult> ConfirmMovieMetadataCandidateAsync(ExternalIdentifierAssociationId associationId, DateTimeOffset occurredAt, CancellationToken cancellationToken = default)
    {
        var snapshot = await store.LoadAsync(cancellationToken);
        var association = snapshot.Associations.Single(x => x.Id == associationId);
        EnsureWorkTarget(association);
        EnsureProviderIdentifier(association.Identifier);
        await EnsureWorkExistsAsync(association.Target.WorkId!, cancellationToken);
        if (association.CurrentStatus == ExternalIdentifierAssociationStatus.Confirmed) return new(false);
        if (association.CurrentStatus != ExternalIdentifierAssociationStatus.Candidate) throw new InvalidOperationException("Only a candidate association can be confirmed.");
        if (snapshot.Associations.Any(other => other.Id != association.Id && other.Target == association.Target && other.Identifier.Provider == association.Identifier.Provider && other.Identifier.Namespace == association.Identifier.Namespace && other.CurrentStatus == ExternalIdentifierAssociationStatus.Confirmed)) throw new InvalidOperationException("A different movie identifier is already confirmed for this Work.");
        var existing = await store.FindConfirmedAssociationAsync(association.Identifier, cancellationToken);
        if (existing is not null && existing.Target.WorkId != association.Target.WorkId)
        {
            var conflict = await store.ConfirmAssociationAsync(association, occurredAt, cancellationToken);
            LogConflict(conflict);
            return conflict;
        }
        var result = await store.ConfirmAssociationAsync(association, occurredAt, cancellationToken);
        LogConflict(result);
        return result;
    }

    public async ValueTask<MovieProviderResult> FetchAndApplyMovieMetadataAsync(ExternalIdentifierAssociationId associationId, CancellationToken cancellationToken = default)
    {
        var snapshot = await store.LoadAsync(cancellationToken);
        var association = snapshot.Associations.Single(x => x.Id == associationId);
        EnsureWorkTarget(association);
        EnsureProviderIdentifier(association.Identifier);
        await EnsureWorkExistsAsync(association.Target.WorkId!, cancellationToken);
        if (association.CurrentStatus != ExternalIdentifierAssociationStatus.Confirmed) return new MovieProviderUnsupported("Movie metadata can only be fetched for a confirmed association.");
        var result = await provider.GetMovieAsync(association.Identifier, cancellationToken);
        if (result is not MovieProviderSuccess<NormalizedMovieDocument> success)
        {
            await store.ApplyAsync(new MetadataEnrichmentWrite([], [], [], Refresh(association.Target, association.Identifier, DateTimeOffset.UtcNow, null, MetadataRefreshStatus.Failed)), cancellationToken);
            return result;
        }
        var document = success.Value;
        if (document.PrimaryExternalIdentifier != association.Identifier || document.MappingVersion != provider.Descriptor.MappingVersion)
        {
            await store.ApplyAsync(new MetadataEnrichmentWrite([], [], [], Refresh(association.Target, association.Identifier, DateTimeOffset.UtcNow, null, MetadataRefreshStatus.Failed)), cancellationToken);
            return new MovieProviderInvalidResponse("The document does not match its selected association.");
        }
        var provenance = new MetadataProvenance(MetadataProvenanceOrigin.ExternalProvider, document.ObservedAt, provider.Descriptor.ProviderCode, association.Identifier);
        var settings = settingsStore is null ? null : await settingsStore.GetSettingsAsync(association.Target.WorkId!, cancellationToken);
        var locked = settings?.LockItemMetadata == true ? new HashSet<string>(MovieMetadataProfile.EditableFields, StringComparer.Ordinal) : (settingsStore is null ? [] : (await settingsStore.GetLocksAsync(association.Target.WorkId!, cancellationToken)).Where(x => x.IsLocked).Select(x => x.FieldCode).ToHashSet(StringComparer.Ordinal));
        var editable = document with { Fields = document.Fields.Where(x => !locked.Contains(x.Field.Code)).ToArray(), RichMetadata = await FilterLockedRichMetadataAsync(association.Target, document.RichMetadata, locked, provider.Descriptor.ProviderCode.Value, cancellationToken) };
        var assertions = BuildAssertionDelta(snapshot, association, editable, provenance);
        var artwork = BuildArtworkDelta(snapshot, association, document, provenance);
        var secondaryAssociations = document.SecondaryExternalIdentifiers
            .Where(identifier => snapshot.Associations.All(existing =>
                existing.Target != association.Target || existing.Identifier != identifier))
            .Where(identifier => snapshot.Associations.All(existing =>
                existing.Identifier != identifier || existing.CurrentStatus != ExternalIdentifierAssociationStatus.Confirmed || existing.Target == association.Target))
            .Select(identifier => new ExternalIdentifierAssociation(
                ExternalIdentifierAssociationId.New(), identifier, association.Target,
                new ExternalIdentifierAssociationLifecycleEntry(ExternalIdentifierAssociationStatus.Confirmed, document.ObservedAt,
                    new MetadataProvenance(MetadataProvenanceOrigin.ExternalProvider, document.ObservedAt, provider.Descriptor.ProviderCode, identifier))))
            .ToArray();
        if (richStore is not null && editable.RichMetadata is not null)
        {
            await richStore.ApplyAsync(new RichMetadataWrite(association.Target, editable.RichMetadata, provenance), cancellationToken);
            if (personProvider is not null && !locked.Contains("people"))
                await EnrichNewCastProfilesAsync(association.Target, editable.RichMetadata, cancellationToken);
        }
        await store.ApplyAsync(new MetadataEnrichmentWrite(secondaryAssociations, assertions, artwork, Refresh(association.Target, association.Identifier, document.ObservedAt, document.PayloadFingerprint, MetadataRefreshStatus.Succeeded)), cancellationToken);
        return result;
    }

    private async ValueTask EnrichNewCastProfilesAsync(EnrichmentTarget target, NormalizedMovieRichMetadata movie, CancellationToken cancellationToken)
    {
        var castIds = movie.Credits.Where(x => x.CreditType.Equals("cast", StringComparison.OrdinalIgnoreCase) || x.CreditType.Equals("actor", StringComparison.OrdinalIgnoreCase) || x.CreditType.Equals("actress", StringComparison.OrdinalIgnoreCase)).Select(x => x.ProviderPersonId).ToHashSet(StringComparer.Ordinal);
        if (castIds.Count == 0) return;
        var people = (await richStore!.LoadAsync(target, cancellationToken)).People.Where(x => x.ProviderCode == provider.Descriptor.ProviderCode.Value && castIds.Contains(x.ProviderId) && x.InternalPersonId.HasValue).ToArray();
        using var concurrency = new SemaphoreSlim(3, 3);
        await Task.WhenAll(people.Select(async person =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stored = await richStore.GetPersonAsync(person.InternalPersonId!.Value, cancellationToken);
            if (stored?.ProfileObservedAt is { } at && DateTimeOffset.UtcNow - at < TimeSpan.FromDays(30)) return;
            await concurrency.WaitAsync(cancellationToken);
            try
            {
                NormalizedPerson? profile = null;
                try { profile = await personProvider!.GetPersonAsync(person.ProviderId, cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch { /* Keep the movie import usable if one person profile is unavailable. */ }
                await richStore.SetPersonProfileAsync(person.InternalPersonId.Value, profile ?? person, DateTimeOffset.UtcNow, cancellationToken);
            }
            finally { concurrency.Release(); }
        }));
    }

    private async ValueTask<NormalizedMovieRichMetadata?> FilterLockedRichMetadataAsync(EnrichmentTarget target, NormalizedMovieRichMetadata? incoming, HashSet<string> locked, string providerCode, CancellationToken cancellationToken)
    {
        if (incoming is null) return null;
        if (richStore is null || !locked.Any(x => x is "genre" or "tag" or "studio" or "people")) return incoming;
        var current = await richStore.LoadAsync(target, cancellationToken); var source = current.Sources.FirstOrDefault(x => x.ProviderCode == providerCode);
        if (source is null) return incoming;
        return incoming with
        {
            Genres = locked.Contains("genre") ? source.Genres : incoming.Genres,
            Tags = locked.Contains("tag") ? source.Tags : incoming.Tags,
            Studios = locked.Contains("studio") ? source.Studios : incoming.Studios,
            People = locked.Contains("people") ? source.People : incoming.People,
            Credits = locked.Contains("people") ? source.Credits : incoming.Credits
        };
    }

    public async ValueTask<bool> IsMovieMetadataRefreshNeededAsync(ExternalIdentifierAssociationId associationId, CancellationToken cancellationToken = default)
    {
        var snapshot = await store.LoadAsync(cancellationToken);
        var association = snapshot.Associations.Single(x => x.Id == associationId);
        EnsureWorkTarget(association);
        EnsureProviderIdentifier(association.Identifier);
        await EnsureWorkExistsAsync(association.Target.WorkId!, cancellationToken);
        return !snapshot.RefreshStates.Any(state =>
            state.Target == association.Target &&
            state.Provider == provider.Descriptor.ProviderCode &&
            state.ExternalIdentifier == association.Identifier &&
            state.MappingVersion == provider.Descriptor.MappingVersion &&
            state.Status == MetadataRefreshStatus.Succeeded);
    }

    private static List<MetadataAssertion> BuildAssertionDelta(MetadataEnrichmentSnapshot snapshot, ExternalIdentifierAssociation association, NormalizedMovieDocument document, MetadataProvenance defaultProvenance)
    {
        var result = new List<MetadataAssertion>();
        foreach (var field in document.Fields.Where(MovieMetadataProfile.Supports))
        {
            var provenance = field.Confidence is null ? defaultProvenance : new MetadataProvenance(MetadataProvenanceOrigin.ExternalProvider, document.ObservedAt, defaultProvenance.Provider, association.Identifier, field.Confidence);
            var existing = snapshot.Assertions.Where(assertion => assertion.Target == association.Target && assertion.Field.Code == field.Field.Code && assertion.Provenance.Provider == defaultProvenance.Provider && assertion.Provenance.RelatedExternalIdentifier == association.Identifier && assertion.Status == MetadataAssertionStatus.Active).ToArray();
            if (existing.Any(assertion => assertion.Value == field.Value)) continue;
            var successor = new MetadataAssertion(MetadataAssertionId.New(), association.Target, field.Field, field.Value, provenance, MetadataAssertionStatus.Active);
            result.Add(successor);
            result.AddRange(existing.Select(assertion => new MetadataAssertion(assertion.Id, assertion.Target, assertion.Field, assertion.Value, assertion.Provenance, MetadataAssertionStatus.Superseded, successor.Id)));
        }
        return result;
    }

    private static ArtworkReference[] BuildArtworkDelta(MetadataEnrichmentSnapshot snapshot, ExternalIdentifierAssociation association, NormalizedMovieDocument document, MetadataProvenance defaultProvenance) =>
        document.Artwork.Where(MovieMetadataProfile.Supports)
            .Where(artwork => !snapshot.ArtworkReferences.Any(existing => existing.Target == association.Target && existing.Role.Code == artwork.Role.Code && existing.Locator.Scheme == artwork.Locator.Scheme && existing.Locator.OpaqueValue == artwork.Locator.OpaqueValue && existing.Provenance.Provider == defaultProvenance.Provider && existing.Provenance.RelatedExternalIdentifier == association.Identifier))
            .Select(artwork => new ArtworkReference(ArtworkReferenceId.New(), association.Target, artwork.Role, artwork.Locator, artwork.Confidence is null ? defaultProvenance : new MetadataProvenance(MetadataProvenanceOrigin.ExternalProvider, document.ObservedAt, defaultProvenance.Provider, association.Identifier, artwork.Confidence)))
            .ToArray();

    private async ValueTask EnsureWorkExistsAsync(WorkId workId, CancellationToken cancellationToken)
    {
        if (!await store.WorkExistsAsync(workId, cancellationToken)) throw new InvalidOperationException("Movie metadata requires an existing Work.");
    }

    private static void EnsureWorkTarget(ExternalIdentifierAssociation association)
    {
        if (association.Target.Kind != EnrichmentTargetKind.Work || association.Target.WorkId is null) throw new InvalidOperationException("Movie metadata requires a Work target.");
    }

    private void LogConflict(MovieMetadataConfirmationResult result)
    {
        if (result.Conflict is not { } conflict) return;
        if (logger is not null) LogIdentifierConflict(logger, conflict.ExistingWorkId.Value, conflict.AttemptedWorkId.Value, conflict.Identifier.Provider.Value, conflict.Identifier.Namespace.Value, conflict.Identifier.Value, null);
    }

    private void EnsureProviderIdentifier(ExternalIdentifier identifier)
    {
        if (identifier.Provider != provider.Descriptor.ProviderCode || !provider.Descriptor.SupportedNamespaces.Contains(identifier.Namespace)) throw new InvalidOperationException("The identifier is not supported by the selected provider.");
    }
    private MetadataRefreshState Refresh(EnrichmentTarget target, ExternalIdentifier id, DateTimeOffset at, string? fingerprint, MetadataRefreshStatus status) => new(target, provider.Descriptor.ProviderCode, id, at, status == MetadataRefreshStatus.Succeeded ? at : null, provider.Descriptor.MappingVersion, fingerprint, status);
}

public static class MovieMetadataProfile
{
    internal static readonly HashSet<string> EditableFields = [
        "title", "original-title", "original-language", "description", "tagline", "release-date",
        "community-rating", "content-rating", "runtime-minutes", "genre", "studio", "keyword", "original-aspect-ratio", "three-d-format", "imdb-id", "tmdb-collection-id"
    ];
    private static readonly HashSet<string> Roles = ["poster", "backdrop", "logo"];
    private static readonly HashSet<string> Fields = EditableFields;
    public static bool Supports(NormalizedMetadataField field) => Fields.Contains(field.Field.Code);
    public static bool Supports(NormalizedArtworkReference artwork) => Roles.Contains(artwork.Role.Code);
}
