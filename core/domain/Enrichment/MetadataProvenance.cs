namespace Hispania.Media.Domain.Enrichment;

public sealed record MetadataProvenance
{
    public MetadataProvenance(
        MetadataProvenanceOrigin origin,
        DateTimeOffset observedAt,
        ExternalIdentifierProvider? provider = null,
        ExternalIdentifier? relatedExternalIdentifier = null,
        decimal? confidence = null)
    {
        if (!Enum.IsDefined(origin))
        {
            throw new DomainValidationException("metadata-provenance.origin.invalid", "Metadata provenance must use a defined origin.");
        }

        if (origin == MetadataProvenanceOrigin.ExternalProvider && provider is null)
        {
            throw new DomainValidationException("metadata-provenance.provider.required", "External provider metadata provenance requires a provider.");
        }

        if (origin is MetadataProvenanceOrigin.Manual or MetadataProvenanceOrigin.LocalProcess && provider is not null)
        {
            throw new DomainValidationException("metadata-provenance.provider.unexpected", "Manual and local process metadata provenance cannot declare an external provider.");
        }

        if (confidence is < 0m or > 1m)
        {
            throw new DomainValidationException("metadata-provenance.confidence.invalid", "Metadata provenance confidence must be between zero and one.");
        }

        Origin = origin;
        ObservedAt = observedAt;
        Provider = provider;
        RelatedExternalIdentifier = relatedExternalIdentifier;
        Confidence = confidence;
    }

    public MetadataProvenanceOrigin Origin { get; }

    public ExternalIdentifierProvider? Provider { get; }

    public ExternalIdentifier? RelatedExternalIdentifier { get; }

    public DateTimeOffset ObservedAt { get; }

    public decimal? Confidence { get; }
}
