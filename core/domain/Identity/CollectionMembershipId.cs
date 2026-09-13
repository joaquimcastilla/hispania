namespace Hispania.Media.Domain.Identity;

public sealed record CollectionMembershipId
{
    public CollectionMembershipId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new DomainValidationException("collection-membership-id.empty", "A CollectionMembership ID cannot be empty.");
        }

        Value = value;
    }

    public Guid Value { get; }

    public static CollectionMembershipId New() => new(Guid.CreateVersion7());
}
