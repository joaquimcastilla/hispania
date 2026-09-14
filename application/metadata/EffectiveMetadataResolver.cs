using Hispania.Media.Domain.Enrichment;
using Hispania.Metadata.Contracts;

namespace Hispania.Metadata.Application;

public sealed class EffectiveMetadataResolver : IEffectiveMetadataResolver
{
    public const string PolicyVersion = "effective-metadata-v0.1";

    public EffectiveMetadataProjection Resolve(EnrichmentTarget target, IReadOnlyCollection<MetadataAssertion> assertions, IReadOnlyCollection<ArtworkReference> artworkReferences)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(assertions);
        ArgumentNullException.ThrowIfNull(artworkReferences);

        return new EffectiveMetadataProjection(
            target,
            PolicyVersion,
            ResolveText(target, "title", assertions),
            ResolveText(target, "original-title", assertions),
            ResolveText(target, "description", assertions),
            ResolveDate(target, "release-date", assertions),
            ResolveArtwork(target, "poster", artworkReferences),
            ResolveArtwork(target, "backdrop", artworkReferences),
            ResolveArtwork(target, "logo", artworkReferences));
    }

    private static EffectiveMetadataValue<string> ResolveText(EnrichmentTarget target, string field, IReadOnlyCollection<MetadataAssertion> assertions)
    {
        var eligible = Order(assertions.Where(x => x.Target == target && x.Field.Code == field && x.Status == MetadataAssertionStatus.Active && x.Value.Kind == MetadataValueKind.Text)).ToArray();
        var winner = eligible.FirstOrDefault();
        return winner is null
            ? new(null, null, null, PolicyVersion, EffectiveMetadataResolutionReason.NoEligibleAssertion, 0)
            : new(winner.Value.TextValue, winner.Id, winner.Provenance, PolicyVersion, EffectiveMetadataResolutionReason.Selected, eligible.Length);
    }

    private static EffectiveMetadataValue<DateOnly?> ResolveDate(EnrichmentTarget target, string field, IReadOnlyCollection<MetadataAssertion> assertions)
    {
        var eligible = Order(assertions.Where(x => x.Target == target && x.Field.Code == field && x.Status == MetadataAssertionStatus.Active && x.Value.Kind == MetadataValueKind.Date)).ToArray();
        var winner = eligible.FirstOrDefault();
        return winner is null
            ? new(null, null, null, PolicyVersion, EffectiveMetadataResolutionReason.NoEligibleAssertion, 0)
            : new(winner.Value.DateValue, winner.Id, winner.Provenance, PolicyVersion, EffectiveMetadataResolutionReason.Selected, eligible.Length);
    }

    private static EffectiveArtworkSelection ResolveArtwork(EnrichmentTarget target, string role, IReadOnlyCollection<ArtworkReference> artwork)
    {
        var eligible = Order(artwork.Where(x => x.Target == target && x.Role.Code == role)).ToArray();
        var winner = eligible.FirstOrDefault();
        return winner is null
            ? new(new ArtworkRole(role), null, null, null, PolicyVersion, EffectiveMetadataResolutionReason.NoEligibleArtworkReference, 0)
            : new(winner.Role, winner.Locator, winner.Id, winner.Provenance, PolicyVersion, EffectiveMetadataResolutionReason.Selected, eligible.Length);
    }

    private static IOrderedEnumerable<MetadataAssertion> Order(IEnumerable<MetadataAssertion> source) => source
        .OrderByDescending(x => OriginRank(x.Provenance.Origin))
        .ThenByDescending(x => x.Provenance.ObservedAt.UtcTicks)
        .ThenBy(x => ProviderKey(x.Provenance), StringComparer.Ordinal)
        .ThenBy(x => IdentifierKey(x.Provenance), StringComparer.Ordinal)
        .ThenBy(x => x.Id.Value.ToString("N"), StringComparer.Ordinal);

    private static IOrderedEnumerable<ArtworkReference> Order(IEnumerable<ArtworkReference> source) => source
        .OrderByDescending(x => OriginRank(x.Provenance.Origin))
        .ThenByDescending(x => x.Provenance.ObservedAt.UtcTicks)
        .ThenBy(x => ProviderKey(x.Provenance), StringComparer.Ordinal)
        .ThenBy(x => IdentifierKey(x.Provenance), StringComparer.Ordinal)
        .ThenBy(x => x.Id.Value.ToString("N"), StringComparer.Ordinal);

    private static int OriginRank(MetadataProvenanceOrigin origin) => origin switch
    {
        MetadataProvenanceOrigin.Manual => 3,
        MetadataProvenanceOrigin.LocalProcess => 2,
        MetadataProvenanceOrigin.ExternalProvider => 1,
        _ => throw new InvalidOperationException("Unsupported metadata provenance origin.")
    };

    private static string ProviderKey(MetadataProvenance provenance) => provenance.Provider?.Value ?? string.Empty;

    private static string IdentifierKey(MetadataProvenance provenance) => provenance.RelatedExternalIdentifier is { } identifier
        ? string.Concat(identifier.Provider.Value, "\u001f", identifier.Namespace.Value, "\u001f", identifier.Value)
        : string.Empty;
}

public sealed class EffectiveMetadataProjectionReader(IMetadataEnrichmentStore store, IEffectiveMetadataResolver resolver) : IEffectiveMetadataProjectionReader
{
    public async ValueTask<EffectiveMetadataProjection> ReadAsync(EnrichmentTarget target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var snapshot = await store.LoadForTargetAsync(target, cancellationToken);
        return resolver.Resolve(target, snapshot.Assertions, snapshot.ArtworkReferences);
    }
}
