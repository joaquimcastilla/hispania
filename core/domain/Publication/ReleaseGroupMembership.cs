using Hispania.Media.Domain.Identity;

namespace Hispania.Media.Domain.Publication;

public sealed record ReleaseGroupMembership
{
    public ReleaseGroupMembership(ReleaseId releaseId)
    {
        ReleaseId = releaseId ?? throw new DomainValidationException("release-group-membership.release-id.required", "A ReleaseGroup membership must reference a Release.");
    }

    public ReleaseId ReleaseId { get; }
}
