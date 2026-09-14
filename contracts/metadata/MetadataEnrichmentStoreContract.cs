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

/// <summary>Durable boundary for Media 2E enrichment and its operational refresh state.</summary>
public interface IMetadataEnrichmentStore
{
    ValueTask<bool> WorkExistsAsync(WorkId workId, CancellationToken cancellationToken = default);

    ValueTask<MetadataEnrichmentSnapshot> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Loads only the enrichment facts required for one target projection.</summary>
    ValueTask<MetadataEnrichmentTargetSnapshot> LoadForTargetAsync(EnrichmentTarget target, CancellationToken cancellationToken = default);

    /// <returns>false when an equivalent refresh payload was already applied.</returns>
    ValueTask<bool> ApplyAsync(MetadataEnrichmentWrite write, CancellationToken cancellationToken = default);
}
