using Hispania.Media.Domain.Identity;

namespace Hispania.Media.Domain.Publication;

public sealed class ReleaseGroup
{
    private readonly HashSet<ReleaseGroupMembership> _memberships = [];

    public ReleaseGroup(ReleaseGroupId id)
    {
        Id = id ?? throw new DomainValidationException("release-group.id.required", "A ReleaseGroup ID is required.");
    }

    public ReleaseGroupId Id { get; }

    public IReadOnlyCollection<ReleaseGroupMembership> Memberships => _memberships.ToArray();

    public ReleaseGroupMembership AddRelease(ReleaseId releaseId)
    {
        var membership = new ReleaseGroupMembership(releaseId);
        if (!_memberships.Add(membership))
        {
            throw new DomainValidationException("release-group.membership.duplicate", "A ReleaseGroup cannot contain the same Release twice.");
        }

        return membership;
    }
}
