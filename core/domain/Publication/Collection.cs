using Hispania.Media.Domain.Identity;
using System.Diagnostics.CodeAnalysis;

namespace Hispania.Media.Domain.Publication;

[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Collection is the accepted Media Domain aggregate name.")]
public sealed class Collection
{
    private readonly Dictionary<CollectionMembershipId, CollectionMembership> _memberships = [];
    private readonly HashSet<CollectionPosition> _positions = [];

    public Collection(CollectionId id)
    {
        Id = id ?? throw new DomainValidationException("collection.id.required", "A Collection ID is required.");
    }

    public CollectionId Id { get; }

    public IReadOnlyCollection<CollectionMembership> Memberships => _memberships.Values.OrderBy(membership => membership.Position.Value).ToArray();

    public CollectionMembership AddMembership(
        CollectionMembershipId id,
        MediaEntityId mediaEntityId,
        CollectionPosition position)
    {
        if (id is null)
        {
            throw new DomainValidationException("collection-membership.id.required", "A CollectionMembership ID is required.");
        }

        if (_memberships.ContainsKey(id))
        {
            throw new DomainValidationException("collection.membership.id.duplicate", "A Collection cannot contain the same CollectionMembership twice.");
        }

        if (position is null)
        {
            throw new DomainValidationException("collection-membership.position.required", "A CollectionMembership position is required.");
        }

        var membership = new CollectionMembership(id, Id, mediaEntityId, position);

        if (!_positions.Add(position))
        {
            throw new DomainValidationException("collection.membership.position.duplicate", "A Collection position can be used only once.");
        }

        _memberships.Add(id, membership);
        return membership;
    }
}
