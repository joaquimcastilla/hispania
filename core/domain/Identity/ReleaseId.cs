namespace Hispania.Media.Domain.Identity;

public sealed record ReleaseId
{
    public ReleaseId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new DomainValidationException("release-id.empty", "A Release ID cannot be empty.");
        }

        Value = value;
    }

    public Guid Value { get; }

    public static ReleaseId New() => new(Guid.CreateVersion7());
}
