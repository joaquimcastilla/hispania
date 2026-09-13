namespace Hispania.Media.Domain.Identity;

public sealed record WorkId
{
    public WorkId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new DomainValidationException("work-id.empty", "A Work ID cannot be empty.");
        }

        Value = value;
    }

    public Guid Value { get; }

    public static WorkId New() => new(Guid.CreateVersion7());
}
