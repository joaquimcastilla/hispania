using Hispania.Media.Domain.Enrichment;
using Hispania.Media.Domain.Identity;
using Hispania.Metadata.Contracts;

namespace Hispania.Metadata.Application;

public sealed class MovieMetadataApplicationService(IMovieMetadataProvider provider, IMetadataEnrichmentStore store)
{
    public ValueTask<MovieProviderResult> SearchMovieMetadataAsync(MovieSearchRequest request, CancellationToken cancellationToken = default) => provider.SearchAsync(request, cancellationToken);

    public async ValueTask<bool> SelectMovieMetadataCandidateAsync(WorkId workId, ExternalIdentifier identifier, DateTimeOffset observedAt, decimal? confidence = null, CancellationToken cancellationToken = default)
    {
        EnsureProviderIdentifier(identifier);
        await EnsureWorkExistsAsync(workId, cancellationToken);
        var provenance = new MetadataProvenance(MetadataProvenanceOrigin.ExternalProvider, observedAt, provider.Descriptor.ProviderCode, identifier, confidence);
        var association = new ExternalIdentifierAssociation(ExternalIdentifierAssociationId.New(), identifier, EnrichmentTarget.For(workId), new ExternalIdentifierAssociationLifecycleEntry(ExternalIdentifierAssociationStatus.Candidate, observedAt, provenance));
        return await store.ApplyAsync(new MetadataEnrichmentWrite([association], [], [], null), cancellationToken);
    }

    public async ValueTask<bool> ConfirmMovieMetadataCandidateAsync(ExternalIdentifierAssociationId associationId, DateTimeOffset occurredAt, CancellationToken cancellationToken = default)
    {
        var snapshot = await store.LoadAsync(cancellationToken);
        var association = snapshot.Associations.Single(x => x.Id == associationId);
        EnsureWorkTarget(association);
        EnsureProviderIdentifier(association.Identifier);
        await EnsureWorkExistsAsync(association.Target.WorkId!, cancellationToken);
        if (association.CurrentStatus != ExternalIdentifierAssociationStatus.Candidate) throw new InvalidOperationException("Only a candidate association can be confirmed.");
        var evidence = new MetadataProvenance(MetadataProvenanceOrigin.ExternalProvider, occurredAt, provider.Descriptor.ProviderCode, association.Identifier);
        association.TransitionTo(new ExternalIdentifierAssociationLifecycleEntry(ExternalIdentifierAssociationStatus.Confirmed, occurredAt, evidence));
        return await store.ApplyAsync(new MetadataEnrichmentWrite([association], [], [], null), cancellationToken);
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
        var assertions = BuildAssertionDelta(snapshot, association, document, provenance);
        var artwork = BuildArtworkDelta(snapshot, association, document, provenance);
        await store.ApplyAsync(new MetadataEnrichmentWrite([], assertions, artwork, Refresh(association.Target, association.Identifier, document.ObservedAt, document.PayloadFingerprint, MetadataRefreshStatus.Succeeded)), cancellationToken);
        return result;
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

    private void EnsureProviderIdentifier(ExternalIdentifier identifier)
    {
        if (identifier.Provider != provider.Descriptor.ProviderCode || !provider.Descriptor.SupportedNamespaces.Contains(identifier.Namespace)) throw new InvalidOperationException("The identifier is not supported by the selected provider.");
    }
    private MetadataRefreshState Refresh(EnrichmentTarget target, ExternalIdentifier id, DateTimeOffset at, string? fingerprint, MetadataRefreshStatus status) => new(target, provider.Descriptor.ProviderCode, id, at, status == MetadataRefreshStatus.Succeeded ? at : null, provider.Descriptor.MappingVersion, fingerprint, status);
}

public static class MovieMetadataProfile
{
    private static readonly HashSet<string> Fields = ["title", "original-title", "description", "release-date"];
    private static readonly HashSet<string> Roles = ["poster", "backdrop", "logo"];
    public static bool Supports(NormalizedMetadataField field) => Fields.Contains(field.Field.Code);
    public static bool Supports(NormalizedArtworkReference artwork) => Roles.Contains(artwork.Role.Code);
}
