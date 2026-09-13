namespace Hispania.Media.Domain.Identity;

public sealed record TimelineId
{
    public TimelineId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new DomainValidationException("timeline-id.empty", "A Timeline ID cannot be empty.");
        }

        Value = value;
    }

    public Guid Value { get; }

    public static TimelineId New() => new(Guid.CreateVersion7());
}
