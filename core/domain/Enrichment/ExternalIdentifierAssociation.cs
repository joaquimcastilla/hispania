using Hispania.Media.Domain.Identity;

namespace Hispania.Media.Domain.Enrichment;

public sealed class ExternalIdentifierAssociation
{
    public ExternalIdentifierAssociation(
        ExternalIdentifierAssociationId id,
        ExternalIdentifier identifier,
        EnrichmentTarget target,
        ExternalIdentifierAssociationStatus status)
    {
        Id = id ?? throw new DomainValidationException("external-identifier-association.id.required", "An ExternalIdentifierAssociation ID is required.");
        Identifier = identifier ?? throw new DomainValidationException("external-identifier-association.identifier.required", "An ExternalIdentifierAssociation identifier is required.");
        Target = target ?? throw new DomainValidationException("external-identifier-association.target.required", "An ExternalIdentifierAssociation target is required.");

        if (!Enum.IsDefined(status))
        {
            throw new DomainValidationException("external-identifier-association.status.invalid", "An ExternalIdentifierAssociation must use a defined status.");
        }

        Status = status;
    }

    public ExternalIdentifierAssociationId Id { get; }

    public ExternalIdentifier Identifier { get; }

    public EnrichmentTarget Target { get; }

    public ExternalIdentifierAssociationStatus Status { get; }
}
