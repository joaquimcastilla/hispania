using Hispania.Media.Domain.Enrichment;
using Hispania.Media.Domain.Identity;

namespace Hispania.Metadata.Contracts;

public enum MetadataRefreshStatus { Succeeded, Failed }

public sealed record MetadataRefreshState(
    EnrichmentTarget Target,
    ExternalIdentifierProvider Provider,
    ExternalIdentifier? ExternalIdentifier,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? LastSuccessAt,
    string MappingVersion,
    string? PayloadFingerprint,
    MetadataRefreshStatus Status);

public sealed record MetadataEnrichmentSnapshot(
    IReadOnlyCollection<ExternalIdentifierAssociation> Associations,
    IReadOnlyCollection<MetadataAssertion> Assertions,
    IReadOnlyCollection<ArtworkReference> ArtworkReferences,
    IReadOnlyCollection<MetadataRefreshState> RefreshStates);

public sealed record MetadataEnrichmentTargetSnapshot(
    EnrichmentTarget Target,
    IReadOnlyCollection<MetadataAssertion> Assertions,
    IReadOnlyCollection<ArtworkReference> ArtworkReferences);

public sealed record MetadataEnrichmentWrite(
    IReadOnlyCollection<ExternalIdentifierAssociation> Associations,
    IReadOnlyCollection<MetadataAssertion> Assertions,
    IReadOnlyCollection<ArtworkReference> ArtworkReferences,
    MetadataRefreshState? RefreshState);

public sealed record ExternalIdentifierConflict(ExternalIdentifier Identifier, WorkId ExistingWorkId, ExternalIdentifierAssociationId ExistingAssociationId, WorkId AttemptedWorkId, ExternalIdentifierAssociationId AttemptedAssociationId);
public sealed record MovieMetadataConfirmationResult(bool Confirmed, ExternalIdentifierConflict? Conflict = null);

public sealed record RichMetadataWrite(EnrichmentTarget Target, NormalizedMovieRichMetadata Metadata, MetadataProvenance Provenance);
public sealed record RichMetadataSourceSnapshot(MetadataProvenanceOrigin Origin, string? ProviderCode, DateTimeOffset ObservedAt, IReadOnlyCollection<string> Genres, IReadOnlyCollection<string> Tags, IReadOnlyCollection<NormalizedStudio> Studios, IReadOnlyCollection<NormalizedPerson> People, IReadOnlyCollection<NormalizedCredit> Credits);
public sealed record RichMetadataSnapshot(EnrichmentTarget Target, IReadOnlyCollection<string> Genres, IReadOnlyCollection<string> Tags, IReadOnlyCollection<NormalizedStudio> Studios, IReadOnlyCollection<NormalizedPerson> People, IReadOnlyCollection<NormalizedCredit> Credits, IReadOnlyCollection<RichMetadataSourceSnapshot> Sources = null!);
public sealed record PersonLibraryCredit(Guid PersonId, WorkId WorkId, string PersonName, string CreditType, string? Character, string? Job, int? Ordinal, MetadataProvenanceOrigin Origin, string? ProviderCode, DateTimeOffset ObservedAt);
public sealed record PersonMetadataSnapshot(NormalizedPerson Person, DateTimeOffset? ProfileObservedAt);
public enum RichMetadataCollectionType { Genres, Tags, Studios }
public sealed record EffectiveRichCollectionProjection<T>(IReadOnlyCollection<T> Effective, IReadOnlyCollection<T> Manual, IReadOnlyCollection<RichMetadataSourceSnapshot> Sources, bool Locked);

public interface IRichMetadataStore
{
    ValueTask<RichMetadataSnapshot> LoadAsync(EnrichmentTarget target, CancellationToken cancellationToken = default);
    ValueTask ApplyAsync(RichMetadataWrite write, CancellationToken cancellationToken = default);
    ValueTask ClearProviderAsync(EnrichmentTarget target, string providerCode, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyCollection<PersonLibraryCredit>> GetCreditsForPersonAsync(Guid personId, CancellationToken cancellationToken = default);
    ValueTask<PersonMetadataSnapshot?> GetPersonAsync(Guid personId, CancellationToken cancellationToken = default);
    ValueTask SetPersonProfileAsync(Guid personId, NormalizedPerson profile, DateTimeOffset observedAt, CancellationToken cancellationToken = default);
    ValueTask SetManualCollectionAsync(EnrichmentTarget target, RichMetadataCollectionType collectionType, IReadOnlyCollection<string> values, DateTimeOffset observedAt, CancellationToken cancellationToken = default);
    ValueTask ClearManualCollectionAsync(EnrichmentTarget target, RichMetadataCollectionType collectionType, CancellationToken cancellationToken = default);
}

/// <summary>Durable boundary for Media 2E enrichment and its operational refresh state.</summary>
public interface IMetadataEnrichmentStore
{
    ValueTask<bool> WorkExistsAsync(WorkId workId, CancellationToken cancellationToken = default);

    ValueTask<MetadataEnrichmentSnapshot> LoadAsync(CancellationToken cancellationToken = default);

    async ValueTask<ExternalIdentifierAssociation?> FindConfirmedAssociationAsync(ExternalIdentifier identifier, CancellationToken cancellationToken = default) =>
        (await LoadAsync(cancellationToken)).Associations.SingleOrDefault(x => x.Identifier == identifier && x.CurrentStatus == ExternalIdentifierAssociationStatus.Confirmed);

    async ValueTask<MovieMetadataConfirmationResult> ConfirmAssociationAsync(ExternalIdentifierAssociation association, DateTimeOffset occurredAt, CancellationToken cancellationToken = default)
    {
        var owner = await FindConfirmedAssociationAsync(association.Identifier, cancellationToken);
        if (owner?.Target.WorkId is { } ownerWork && ownerWork != association.Target.WorkId)
        {
            var reason = $"external-identifier-conflict|provider={association.Identifier.Provider.Value}|namespace={association.Identifier.Namespace.Value}|identifier={association.Identifier.Value}|existingWorkId={ownerWork.Value:D}|existingAssociationId={owner.Id.Value:D}|attemptedWorkId={association.Target.WorkId!.Value:D}|attemptedAssociationId={association.Id.Value:D}";
            association.TransitionTo(new ExternalIdentifierAssociationLifecycleEntry(ExternalIdentifierAssociationStatus.Rejected, occurredAt, new MetadataProvenance(MetadataProvenanceOrigin.ExternalProvider, occurredAt, association.Identifier.Provider, association.Identifier), reason));
            await ApplyAsync(new MetadataEnrichmentWrite([association], [], [], null), cancellationToken);
            return new(false, new ExternalIdentifierConflict(association.Identifier, ownerWork, owner.Id, association.Target.WorkId!, association.Id));
        }
        association.TransitionTo(new ExternalIdentifierAssociationLifecycleEntry(ExternalIdentifierAssociationStatus.Confirmed, occurredAt, new MetadataProvenance(MetadataProvenanceOrigin.ExternalProvider, occurredAt, association.Identifier.Provider, association.Identifier)));
        await ApplyAsync(new MetadataEnrichmentWrite([association], [], [], null), cancellationToken);
        return new(true);
    }

    /// <summary>Loads only the enrichment facts required for one target projection.</summary>
    ValueTask<MetadataEnrichmentTargetSnapshot> LoadForTargetAsync(EnrichmentTarget target, CancellationToken cancellationToken = default);

    /// <summary>Loads enrichment facts for multiple targets without one store round trip per target.</summary>
    ValueTask<IReadOnlyCollection<MetadataEnrichmentTargetSnapshot>> LoadForTargetsAsync(IReadOnlyCollection<EnrichmentTarget> targets, CancellationToken cancellationToken = default);

    ValueTask<ArtworkReference?> GetArtworkReferenceByIdAsync(ArtworkReferenceId id, CancellationToken cancellationToken = default);

    /// <returns>false when an equivalent refresh payload was already applied.</returns>
    ValueTask<bool> ApplyAsync(MetadataEnrichmentWrite write, CancellationToken cancellationToken = default);
}
