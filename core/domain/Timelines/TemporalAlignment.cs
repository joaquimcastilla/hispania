namespace Hispania.Media.Domain.Timelines;

public sealed record TemporalAlignment
{
    private TemporalAlignment(TimeSpan offset)
    {
        OffsetValue = offset;
    }

    public static TemporalAlignment Identity { get; } = new(TimeSpan.Zero);

    public TimeSpan OffsetValue { get; }

    public static TemporalAlignment Offset(TimeSpan offset) => offset == TimeSpan.Zero ? Identity : new TemporalAlignment(offset);
}
