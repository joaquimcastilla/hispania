namespace Hispania.Media.Domain.Timelines;

public sealed record TimelineDuration
{
    public TimelineDuration(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            throw new DomainValidationException("timeline-duration.negative", "A Timeline duration cannot be negative.");
        }

        Value = value;
    }

    public TimeSpan Value { get; }
}
