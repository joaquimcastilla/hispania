namespace Hispania.Media.Domain.Identity;

public sealed record ExternalIdentifierAssociationId
{
    public ExternalIdentifierAssociationId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new DomainValidationException("external-identifier-association-id.empty", "An ExternalIdentifierAssociation ID cannot be empty.");
        }

        Value = value;
    }

    public Guid Value { get; }

    public static ExternalIdentifierAssociationId New() => new(Guid.CreateVersion7());
}
