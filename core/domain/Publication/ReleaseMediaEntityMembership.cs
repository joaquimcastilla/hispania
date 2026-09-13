using Hispania.Media.Domain.Identity;

namespace Hispania.Media.Domain.Publication;

public sealed record ReleaseMediaEntityMembership
{
    public ReleaseMediaEntityMembership(MediaEntityId mediaEntityId)
    {
        MediaEntityId = mediaEntityId ?? throw new DomainValidationException("release-membership.media-entity-id.required", "A Release membership must reference a MediaEntity.");
    }

    public MediaEntityId MediaEntityId { get; }
}
