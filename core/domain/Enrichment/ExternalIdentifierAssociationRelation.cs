using Hispania.Media.Domain.Identity;

namespace Hispania.Media.Domain.Enrichment;

public sealed record ExternalIdentifierAssociationRelation
{
    public ExternalIdentifierAssociationRelation(
        ExternalIdentifierAssociationId relatedAssociationId,
        ExternalIdentifierAssociationRelationKind kind,
        MetadataProvenance evidence,
        string? reason = null)
    {
        RelatedAssociationId = relatedAssociationId ?? throw new DomainValidationException("external-identifier-association.relation.related-association-id.required", "An ExternalIdentifierAssociation relation requires a related association ID.");

        if (!Enum.IsDefined(kind))
        {
            throw new DomainValidationException("external-identifier-association.relation.kind.invalid", "An ExternalIdentifierAssociation relation must use a defined kind.");
        }

        Evidence = evidence ?? throw new DomainValidationException("external-identifier-association.relation.evidence.required", "An ExternalIdentifierAssociation relation requires evidence.");
        Reason = ExternalIdentifierAssociationLifecycleEntry.ValidateReason(reason, "external-identifier-association.relation.reason");
        Kind = kind;
    }

    public ExternalIdentifierAssociationId RelatedAssociationId { get; }

    public ExternalIdentifierAssociationRelationKind Kind { get; }

    public MetadataProvenance Evidence { get; }

    public string? Reason { get; }
}
