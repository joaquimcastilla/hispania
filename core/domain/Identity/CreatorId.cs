namespace Hispania.Media.Domain.Identity;

public sealed record CreatorId
{
    public CreatorId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new DomainValidationException("creator-id.empty", "A Creator ID cannot be empty.");
        }

        Value = value;
    }

    public Guid Value { get; }

    public static CreatorId New() => new(Guid.CreateVersion7());
}
