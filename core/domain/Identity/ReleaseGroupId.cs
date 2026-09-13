namespace Hispania.Media.Domain.Identity;

public sealed record ReleaseGroupId
{
    public ReleaseGroupId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new DomainValidationException("release-group-id.empty", "A ReleaseGroup ID cannot be empty.");
        }

        Value = value;
    }

    public Guid Value { get; }

    public static ReleaseGroupId New() => new(Guid.CreateVersion7());
}
