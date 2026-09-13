using Hispania.Media.Domain.Identity;

namespace Hispania.Media.Domain.Publication;

public sealed class Release
{
    private readonly HashSet<ReleaseMediaEntityMembership> _memberships = [];

    public Release(ReleaseId id)
    {
        Id = id ?? throw new DomainValidationException("release.id.required", "A Release ID is required.");
    }

    public ReleaseId Id { get; }

    public IReadOnlyCollection<ReleaseMediaEntityMembership> Memberships => _memberships.ToArray();

    public ReleaseMediaEntityMembership AddMediaEntity(MediaEntityId mediaEntityId)
    {
        var membership = new ReleaseMediaEntityMembership(mediaEntityId);
        if (!_memberships.Add(membership))
        {
            throw new DomainValidationException("release.membership.duplicate", "A Release cannot contain the same MediaEntity twice.");
        }

        return membership;
    }
}
