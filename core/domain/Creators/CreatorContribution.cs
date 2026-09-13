using Hispania.Media.Domain.Identity;

namespace Hispania.Media.Domain.Creators;

public sealed class CreatorContribution
{
    internal CreatorContribution(
        CreatorContributionId id,
        CreatorId creatorId,
        ContributionTarget target,
        ContributionRole role)
    {
        Id = id ?? throw new DomainValidationException("creator-contribution.id.required", "A CreatorContribution ID is required.");
        CreatorId = creatorId ?? throw new DomainValidationException("creator-contribution.creator-id.required", "A CreatorContribution must reference a Creator.");
        Target = target ?? throw new DomainValidationException("creator-contribution.target.required", "A CreatorContribution target is required.");
        Role = role ?? throw new DomainValidationException("creator-contribution.role.required", "A CreatorContribution role is required.");
    }

    public CreatorContributionId Id { get; }

    public CreatorId CreatorId { get; }

    public ContributionTarget Target { get; }

    public ContributionRole Role { get; }
}
