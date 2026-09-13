using Hispania.Media.Domain.Identity;

namespace Hispania.Media.Domain.Publication;

public sealed class CollectionMembership
{
    internal CollectionMembership(
        CollectionMembershipId id,
        CollectionId collectionId,
        MediaEntityId mediaEntityId,
        CollectionPosition position)
    {
        Id = id ?? throw new DomainValidationException("collection-membership.id.required", "A CollectionMembership ID is required.");
        CollectionId = collectionId ?? throw new DomainValidationException("collection-membership.collection-id.required", "A CollectionMembership must reference a Collection.");
        MediaEntityId = mediaEntityId ?? throw new DomainValidationException("collection-membership.media-entity-id.required", "A CollectionMembership must reference a MediaEntity.");
        Position = position ?? throw new DomainValidationException("collection-membership.position.required", "A CollectionMembership position is required.");
    }

    public CollectionMembershipId Id { get; }

    public CollectionId CollectionId { get; }

    public MediaEntityId MediaEntityId { get; }

    public CollectionPosition Position { get; }
}
