using Hispania.Media.Domain.Enrichment;
using Hispania.Media.Domain.Identity;

namespace Hispania.Metadata.Contracts;

public enum EffectiveMetadataResolutionReason
{
    Selected,
    NoEligibleAssertion,
    NoEligibleArtworkReference
}

public sealed record EffectiveMetadataValue<T>(
    T? Value,
    MetadataAssertionId? WinnerAssertionId,
    MetadataProvenance? SourceProvenance,
    string PolicyVersion,
    EffectiveMetadataResolutionReason ResolutionReason,
    int EligibleCandidateCount);

public sealed record EffectiveArtworkSelection(
    ArtworkRole Role,
    ArtworkLocator? Locator,
    ArtworkReferenceId? WinnerArtworkReferenceId,
    MetadataProvenance? SourceProvenance,
    string PolicyVersion,
    EffectiveMetadataResolutionReason ResolutionReason,
    int EligibleCandidateCount);

public sealed record EffectiveMetadataProjection(
    EnrichmentTarget Target,
    string PolicyVersion,
    EffectiveMetadataValue<string> Title,
    EffectiveMetadataValue<string> OriginalTitle,
    EffectiveMetadataValue<string> Description,
    EffectiveMetadataValue<DateOnly?> ReleaseDate,
    EffectiveArtworkSelection Poster,
    EffectiveArtworkSelection Backdrop,
    EffectiveArtworkSelection Logo);

public interface IEffectiveMetadataResolver
{
    EffectiveMetadataProjection Resolve(
        EnrichmentTarget target,
        IReadOnlyCollection<MetadataAssertion> assertions,
        IReadOnlyCollection<ArtworkReference> artworkReferences);
}

public interface IEffectiveMetadataProjectionReader
{
    ValueTask<EffectiveMetadataProjection> ReadAsync(EnrichmentTarget target, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<EffectiveMetadataProjection>> ReadManyAsync(IReadOnlyCollection<EnrichmentTarget> targets, CancellationToken cancellationToken = default);
}
