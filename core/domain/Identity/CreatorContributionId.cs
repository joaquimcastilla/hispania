namespace Hispania.Media.Domain.Identity;

public sealed record CreatorContributionId
{
    public CreatorContributionId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new DomainValidationException("creator-contribution-id.empty", "A CreatorContribution ID cannot be empty.");
        }

        Value = value;
    }

    public Guid Value { get; }

    public static CreatorContributionId New() => new(Guid.CreateVersion7());
}
