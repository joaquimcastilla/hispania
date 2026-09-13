namespace Hispania.Media.Domain.Identity;

public sealed record TechnicalRepresentationId
{
    public TechnicalRepresentationId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new DomainValidationException("technical-representation-id.empty", "A TechnicalRepresentation ID cannot be empty.");
        }

        Value = value;
    }

    public Guid Value { get; }

    public static TechnicalRepresentationId New() => new(Guid.CreateVersion7());
}
