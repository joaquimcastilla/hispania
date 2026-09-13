using Hispania.Media.Domain.Identity;

namespace Hispania.Media.Domain.Timelines;

public sealed class AssetCoverage
{
    private readonly CoverageInterval[] _intervals;

    private AssetCoverage(
        AssetCoverageId id,
        TimelineId timelineId,
        AssetId assetId,
        AssetCoverageState state,
        CoverageKind? coverageKind,
        CoverageInterval[] intervals,
        TemporalAlignment? alignment)
    {
        Id = id ?? throw new DomainValidationException("asset-coverage.id.required", "An AssetCoverage ID is required.");
        TimelineId = timelineId ?? throw new DomainValidationException("asset-coverage.timeline-id.required", "An AssetCoverage must reference a Timeline.");
        AssetId = assetId ?? throw new DomainValidationException("asset-coverage.asset-id.required", "An AssetCoverage must reference an Asset.");
        State = state;
        CoverageKind = coverageKind;
        _intervals = intervals;
        Alignment = alignment;
    }

    public AssetCoverageId Id { get; }

    public TimelineId TimelineId { get; }

    public AssetId AssetId { get; }

    public AssetCoverageState State { get; }

    public CoverageKind? CoverageKind { get; }

    public IReadOnlyCollection<CoverageInterval> Intervals => _intervals.ToArray();

    public TemporalAlignment? Alignment { get; }

    public static AssetCoverage CreateCovered(
        AssetCoverageId id,
        TimelineId timelineId,
        AssetId assetId,
        CoverageKind coverageKind,
        IEnumerable<CoverageInterval>? intervals,
        TemporalAlignment? alignment)
    {
        if (!Enum.IsDefined(coverageKind))
        {
            throw new DomainValidationException("asset-coverage.kind.invalid", "A covered AssetCoverage must use a defined CoverageKind.");
        }

        if (alignment is null)
        {
            throw new DomainValidationException("asset-coverage.alignment.required", "A covered AssetCoverage requires TemporalAlignment.");
        }

        var intervalList = intervals?.ToArray() ?? [];
        ValidateCoveredIntervals(coverageKind, intervalList);

        return new AssetCoverage(id, timelineId, assetId, AssetCoverageState.Covered, coverageKind, intervalList, alignment);
    }

    public static AssetCoverage CreateIncompatible(
        AssetCoverageId id,
        TimelineId timelineId,
        AssetId assetId,
        CoverageKind? coverageKind = null,
        IEnumerable<CoverageInterval>? intervals = null,
        TemporalAlignment? alignment = null)
    {
        var intervalList = intervals?.ToArray() ?? [];

        if (coverageKind is not null || intervalList.Length > 0 || alignment is not null)
        {
            throw new DomainValidationException("asset-coverage.incompatible.shape", "An incompatible AssetCoverage cannot contain coverage details or alignment.");
        }

        return new AssetCoverage(id, timelineId, assetId, AssetCoverageState.Incompatible, null, intervalList, null);
    }

    internal void ValidateFor(TimelineDuration? duration)
    {
        if (State != AssetCoverageState.Covered || CoverageKind != global::Hispania.Media.Domain.Timelines.CoverageKind.Partial)
        {
            return;
        }

        if (duration is null)
        {
            return;
        }

        if (_intervals.Any(interval => interval.End > duration.Value))
        {
            throw new DomainValidationException("asset-coverage.interval.exceeds-duration", "A coverage interval cannot exceed the Timeline duration.");
        }

        if (RepresentsFullTimeline(duration.Value))
        {
            throw new DomainValidationException("asset-coverage.partial.full", "A complete Timeline coverage must be represented as Full.");
        }
    }

    private static void ValidateCoveredIntervals(CoverageKind coverageKind, CoverageInterval[] intervals)
    {
        if (coverageKind == global::Hispania.Media.Domain.Timelines.CoverageKind.Full && intervals.Length > 0)
        {
            throw new DomainValidationException("asset-coverage.full.intervals", "A full AssetCoverage cannot contain intervals.");
        }

        if (coverageKind == global::Hispania.Media.Domain.Timelines.CoverageKind.Partial && intervals.Length == 0)
        {
            throw new DomainValidationException("asset-coverage.partial.intervals.required", "A partial AssetCoverage requires at least one interval.");
        }

        for (var index = 1; index < intervals.Length; index++)
        {
            var previous = intervals[index - 1];
            var current = intervals[index];

            if (current.Start < previous.Start)
            {
                throw new DomainValidationException("asset-coverage.intervals.unordered", "AssetCoverage intervals must be ordered by start position.");
            }

            if (current.Start < previous.End)
            {
                throw new DomainValidationException("asset-coverage.intervals.overlap", "AssetCoverage intervals cannot overlap.");
            }
        }
    }

    private bool RepresentsFullTimeline(TimeSpan duration)
    {
        if (_intervals.Length == 0 || _intervals[0].Start != TimeSpan.Zero || _intervals[^1].End != duration)
        {
            return false;
        }

        for (var index = 1; index < _intervals.Length; index++)
        {
            if (_intervals[index].Start != _intervals[index - 1].End)
            {
                return false;
            }
        }

        return true;
    }
}
