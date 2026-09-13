using Hispania.Media.Domain.Identity;

namespace Hispania.Media.Domain.Creators;

public sealed class Creator
{
    private readonly Dictionary<CreatorContributionId, CreatorContribution> _contributions = [];

    public Creator(CreatorId id, CreatorKind kind)
    {
        Id = id ?? throw new DomainValidationException("creator.id.required", "A Creator ID is required.");
        if (!Enum.IsDefined(kind))
        {
            throw new DomainValidationException("creator.kind.invalid", "A Creator must use a defined CreatorKind.");
        }

        Kind = kind;
    }

    public CreatorId Id { get; }

    public CreatorKind Kind { get; }

    public IReadOnlyCollection<CreatorContribution> Contributions => _contributions.Values.ToArray();

    public CreatorContribution AddContribution(
        CreatorContributionId id,
        ContributionTarget target,
        ContributionRole role)
    {
        if (id is null)
        {
            throw new DomainValidationException("creator-contribution.id.required", "A CreatorContribution ID is required.");
        }

        if (_contributions.ContainsKey(id))
        {
            throw new DomainValidationException("creator.contribution.id.duplicate", "A Creator cannot contain the same CreatorContribution twice.");
        }

        if (target is null)
        {
            throw new DomainValidationException("creator-contribution.target.required", "A CreatorContribution target is required.");
        }

        if (role is null)
        {
            throw new DomainValidationException("creator-contribution.role.required", "A CreatorContribution role is required.");
        }

        if (_contributions.Values.Any(contribution => contribution.Target == target && contribution.Role == role))
        {
            throw new DomainValidationException("creator.contribution.duplicate", "A Creator cannot make the same contribution twice.");
        }

        var contribution = new CreatorContribution(id, Id, target, role);
        _contributions.Add(id, contribution);
        return contribution;
    }
}
