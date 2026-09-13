namespace Hispania.Media.Domain.Timelines;

public sealed record CoverageInterval : IComparable<CoverageInterval>
{
    public CoverageInterval(TimeSpan start, TimeSpan end)
    {
        if (start < TimeSpan.Zero)
        {
            throw new DomainValidationException("coverage-interval.start.negative", "A coverage interval start cannot be negative.");
        }

        if (end <= start)
        {
            throw new DomainValidationException("coverage-interval.end.invalid", "A coverage interval end must be greater than its start.");
        }

        Start = start;
        End = end;
    }

    public TimeSpan Start { get; }

    public TimeSpan End { get; }

    public int CompareTo(CoverageInterval? other)
    {
        ArgumentNullException.ThrowIfNull(other);

        var startComparison = Start.CompareTo(other.Start);
        return startComparison != 0 ? startComparison : End.CompareTo(other.End);
    }

    public static bool operator <(CoverageInterval left, CoverageInterval right) => left.CompareTo(right) < 0;

    public static bool operator <=(CoverageInterval left, CoverageInterval right) => left.CompareTo(right) <= 0;

    public static bool operator >(CoverageInterval left, CoverageInterval right) => left.CompareTo(right) > 0;

    public static bool operator >=(CoverageInterval left, CoverageInterval right) => left.CompareTo(right) >= 0;
}
