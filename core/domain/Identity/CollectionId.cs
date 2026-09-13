namespace Hispania.Media.Domain.Identity;

public sealed record CollectionId
{
    public CollectionId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new DomainValidationException("collection-id.empty", "A Collection ID cannot be empty.");
        }

        Value = value;
    }

    public Guid Value { get; }

    public static CollectionId New() => new(Guid.CreateVersion7());
}
