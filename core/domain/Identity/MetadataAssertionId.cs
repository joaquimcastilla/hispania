namespace Hispania.Media.Domain.Identity;

public sealed record MetadataAssertionId
{
    public MetadataAssertionId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new DomainValidationException("metadata-assertion-id.empty", "A MetadataAssertion ID cannot be empty.");
        }

        Value = value;
    }

    public Guid Value { get; }

    public static MetadataAssertionId New() => new(Guid.CreateVersion7());
}
