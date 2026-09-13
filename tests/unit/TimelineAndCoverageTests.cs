using Hispania.Media.Domain;
using Hispania.Media.Domain.Assets;
using Hispania.Media.Domain.Identity;
using Hispania.Media.Domain.MediaEntities;
using Hispania.Media.Domain.Timelines;
using Hispania.Media.Domain.Works;
using Xunit;

namespace Hispania.Media.Domain.Tests;

public sealed class TimelineAndCoverageTests
{
    [Theory]
    [InlineData("timeline-id.empty")]
    [InlineData("asset-coverage-id.empty")]
    public void NewIdentityTypesRejectEmptyValues(string errorCode)
    {
        var exception = errorCode switch
        {
            "timeline-id.empty" => Assert.Throws<DomainValidationException>(() => new TimelineId(Guid.Empty)),
            "asset-coverage-id.empty" => Assert.Throws<DomainValidationException>(() => new AssetCoverageId(Guid.Empty)),
            _ => throw new InvalidOperationException("The test case is not supported.")
        };

        Assert.Equal(errorCode, exception.Code);
    }

    [Fact]
    public void NewIdentityTypesGenerateVersion7AndPreserveExistingValues()
    {
        var value = Guid.NewGuid();

        Assert.Equal(7, TimelineId.New().Value.Version);
        Assert.Equal(7, AssetCoverageId.New().Value.Version);
        Assert.Equal(value, new TimelineId(value).Value);
        Assert.Equal(value, new AssetCoverageId(value).Value);
    }

    [Fact]
    public void MediaEntityAcceptsKnownZeroAndUnknownTimelineDurations()
    {
        var mediaEntity = CreateMediaEntity();
        var knownTimeline = mediaEntity.AddTimeline(TimelineId.New(), new TimelineDuration(TimeSpan.FromMinutes(90)));
        var zeroTimeline = mediaEntity.AddTimeline(TimelineId.New(), new TimelineDuration(TimeSpan.Zero));
        var unknownTimeline = mediaEntity.AddTimeline(TimelineId.New());

        Assert.Equal(TimeSpan.FromMinutes(90), knownTimeline.Duration!.Value);
        Assert.Equal(TimeSpan.Zero, zeroTimeline.Duration!.Value);
        Assert.Null(unknownTimeline.Duration);
        Assert.Equal(3, mediaEntity.Timelines.Count);
    }

    [Fact]
    public void TimelineDurationRejectsNegativeValues()
    {
        var exception = Assert.Throws<DomainValidationException>(() => new TimelineDuration(TimeSpan.FromSeconds(-1)));

        Assert.Equal("timeline-duration.negative", exception.Code);
    }

    [Fact]
    public void MediaEntityRejectsDuplicateTimelineIdsAndTimelinesKeepTheirOwner()
    {
        var mediaEntity = CreateMediaEntity();
        var timelineId = TimelineId.New();
        var timeline = mediaEntity.AddTimeline(timelineId);

        var exception = Assert.Throws<DomainValidationException>(() => mediaEntity.AddTimeline(timelineId));

        Assert.Equal(mediaEntity.Id, timeline.MediaEntityId);
        Assert.Equal("media-entity.timeline.duplicate", exception.Code);
    }

    [Fact]
    public void CoverageIntervalUsesHalfOpenValidRangesAndAllowsAdjacency()
    {
        var first = new CoverageInterval(TimeSpan.Zero, TimeSpan.FromSeconds(10));
        var adjacent = new CoverageInterval(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20));

        Assert.True(first.CompareTo(adjacent) < 0);
        Assert.Equal(first.End, adjacent.Start);
    }

    [Theory]
    [InlineData(-1, 1, "coverage-interval.start.negative")]
    [InlineData(1, 1, "coverage-interval.end.invalid")]
    [InlineData(2, 1, "coverage-interval.end.invalid")]
    public void CoverageIntervalRejectsInvalidRanges(int startSeconds, int endSeconds, string errorCode)
    {
        var exception = Assert.Throws<DomainValidationException>(
            () => new CoverageInterval(TimeSpan.FromSeconds(startSeconds), TimeSpan.FromSeconds(endSeconds)));

        Assert.Equal(errorCode, exception.Code);
    }

    [Fact]
    public void FullCoverageIsValidForKnownAndUnknownDurations()
    {
        var knownTimeline = CreateMediaEntity().AddTimeline(TimelineId.New(), new TimelineDuration(TimeSpan.FromMinutes(90)));
        var unknownTimeline = CreateMediaEntity().AddTimeline(TimelineId.New());

        knownTimeline.AddCoverage(CreateFullCoverage(knownTimeline.Id, AssetId.New()));
        unknownTimeline.AddCoverage(CreateFullCoverage(unknownTimeline.Id, AssetId.New()));

        Assert.Equal(CoverageKind.Full, Assert.Single(knownTimeline.Coverages).CoverageKind);
        Assert.Equal(CoverageKind.Full, Assert.Single(unknownTimeline.Coverages).CoverageKind);
    }

    [Fact]
    public void FullCoverageRequiresAlignmentAndRejectsIntervals()
    {
        var timelineId = TimelineId.New();
        var assetId = AssetId.New();

        var missingAlignment = Assert.Throws<DomainValidationException>(
            () => AssetCoverage.CreateCovered(AssetCoverageId.New(), timelineId, assetId, CoverageKind.Full, [], null));
        var withIntervals = Assert.Throws<DomainValidationException>(
            () => AssetCoverage.CreateCovered(
                AssetCoverageId.New(),
                timelineId,
                assetId,
                CoverageKind.Full,
                [new CoverageInterval(TimeSpan.Zero, TimeSpan.FromSeconds(1))],
                TemporalAlignment.Identity));

        Assert.Equal("asset-coverage.alignment.required", missingAlignment.Code);
        Assert.Equal("asset-coverage.full.intervals", withIntervals.Code);
    }

    [Fact]
    public void PartialCoverageAcceptsSingleAndMultipleIntervals()
    {
        var timeline = CreateMediaEntity().AddTimeline(TimelineId.New(), new TimelineDuration(TimeSpan.FromSeconds(30)));
        var single = CreatePartialCoverage(
            timeline.Id,
            AssetId.New(),
            [new CoverageInterval(TimeSpan.Zero, TimeSpan.FromSeconds(10))]);
        var multiple = CreatePartialCoverage(
            timeline.Id,
            AssetId.New(),
            [
                new CoverageInterval(TimeSpan.Zero, TimeSpan.FromSeconds(10)),
                new CoverageInterval(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30))
            ]);

        timeline.AddCoverage(single);
        timeline.AddCoverage(multiple);

        Assert.Equal(2, timeline.Coverages.Count);
    }

    [Fact]
    public void PartialCoverageRejectsUnorderedAndOverlappingIntervals()
    {
        var timelineId = TimelineId.New();
        var unordered = Assert.Throws<DomainValidationException>(
            () => CreatePartialCoverage(
                timelineId,
                AssetId.New(),
                [
                    new CoverageInterval(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20)),
                    new CoverageInterval(TimeSpan.Zero, TimeSpan.FromSeconds(5))
                ]));
        var overlapping = Assert.Throws<DomainValidationException>(
            () => CreatePartialCoverage(
                timelineId,
                AssetId.New(),
                [
                    new CoverageInterval(TimeSpan.Zero, TimeSpan.FromSeconds(10)),
                    new CoverageInterval(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15))
                ]));

        Assert.Equal("asset-coverage.intervals.unordered", unordered.Code);
        Assert.Equal("asset-coverage.intervals.overlap", overlapping.Code);
    }

    [Fact]
    public void TimelineRejectsPartialCoverageOutsideItsDuration()
    {
        var timeline = CreateMediaEntity().AddTimeline(TimelineId.New(), new TimelineDuration(TimeSpan.FromSeconds(10)));
        var coverage = CreatePartialCoverage(
            timeline.Id,
            AssetId.New(),
            [new CoverageInterval(TimeSpan.Zero, TimeSpan.FromSeconds(11))]);

        var exception = Assert.Throws<DomainValidationException>(() => timeline.AddCoverage(coverage));

        Assert.Equal("asset-coverage.interval.exceeds-duration", exception.Code);
    }

    [Fact]
    public void TimelineRejectsPartialCoverageThatIsExactlyFull()
    {
        var timeline = CreateMediaEntity().AddTimeline(TimelineId.New(), new TimelineDuration(TimeSpan.FromSeconds(10)));
        var coverage = CreatePartialCoverage(
            timeline.Id,
            AssetId.New(),
            [
                new CoverageInterval(TimeSpan.Zero, TimeSpan.FromSeconds(5)),
                new CoverageInterval(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10))
            ]);

        var exception = Assert.Throws<DomainValidationException>(() => timeline.AddCoverage(coverage));

        Assert.Equal("asset-coverage.partial.full", exception.Code);
    }

    [Fact]
    public void IncompatibleCoverageHasNoDetailsOrAlignment()
    {
        var timeline = CreateMediaEntity().AddTimeline(TimelineId.New());
        var incompatible = AssetCoverage.CreateIncompatible(AssetCoverageId.New(), timeline.Id, AssetId.New());

        timeline.AddCoverage(incompatible);

        Assert.Equal(AssetCoverageState.Incompatible, Assert.Single(timeline.Coverages).State);
        Assert.Empty(Assert.Single(timeline.Coverages).Intervals);
        Assert.Null(Assert.Single(timeline.Coverages).Alignment);
    }

    [Fact]
    public void IncompatibleCoverageRejectsAlignmentAndIntervals()
    {
        var timelineId = TimelineId.New();
        var withAlignment = Assert.Throws<DomainValidationException>(
            () => AssetCoverage.CreateIncompatible(
                AssetCoverageId.New(),
                timelineId,
                AssetId.New(),
                alignment: TemporalAlignment.Identity));
        var withIntervals = Assert.Throws<DomainValidationException>(
            () => AssetCoverage.CreateIncompatible(
                AssetCoverageId.New(),
                timelineId,
                AssetId.New(),
                intervals: [new CoverageInterval(TimeSpan.Zero, TimeSpan.FromSeconds(1))]));

        Assert.Equal("asset-coverage.incompatible.shape", withAlignment.Code);
        Assert.Equal("asset-coverage.incompatible.shape", withIntervals.Code);
    }

    [Fact]
    public void AbsenceOfCoverageRepresentsUnknownAndTimelineRejectsDuplicatesForAnAsset()
    {
        var timeline = CreateMediaEntity().AddTimeline(TimelineId.New());
        var assetId = AssetId.New();

        Assert.Empty(timeline.Coverages);
        Assert.Equal(2, Enum.GetValues<AssetCoverageState>().Length);

        timeline.AddCoverage(CreateFullCoverage(timeline.Id, assetId));
        var exception = Assert.Throws<DomainValidationException>(
            () => timeline.AddCoverage(AssetCoverage.CreateIncompatible(AssetCoverageId.New(), timeline.Id, assetId)));

        Assert.Equal("timeline.asset-coverage.duplicate", exception.Code);
    }

    [Fact]
    public void AssetHasNoInverseCoverageCollection()
    {
        var propertyNames = typeof(Asset).GetProperties().Select(property => property.Name);

        Assert.DoesNotContain("Coverages", propertyNames);
    }

    [Fact]
    public void TemporalAlignmentSupportsIdentityAndSignedOffsets()
    {
        var positive = TemporalAlignment.Offset(TimeSpan.FromSeconds(2));
        var negative = TemporalAlignment.Offset(TimeSpan.FromSeconds(-2));
        var normalizedZero = TemporalAlignment.Offset(TimeSpan.Zero);

        Assert.Equal(TimeSpan.Zero, TemporalAlignment.Identity.OffsetValue);
        Assert.Equal(TimeSpan.FromSeconds(2), positive.OffsetValue);
        Assert.Equal(TimeSpan.FromSeconds(-2), negative.OffsetValue);
        Assert.Same(TemporalAlignment.Identity, normalizedZero);
    }

    private static MediaEntity CreateMediaEntity()
    {
        var work = new Work(WorkId.New(), WorkCategory.Movie);
        return work.AddMediaEntity(MediaEntityId.New(), MediaEntityCategory.MovieRealisation);
    }

    private static AssetCoverage CreateFullCoverage(TimelineId timelineId, AssetId assetId) =>
        AssetCoverage.CreateCovered(
            AssetCoverageId.New(),
            timelineId,
            assetId,
            CoverageKind.Full,
            [],
            TemporalAlignment.Identity);

    private static AssetCoverage CreatePartialCoverage(
        TimelineId timelineId,
        AssetId assetId,
        IEnumerable<CoverageInterval> intervals) =>
        AssetCoverage.CreateCovered(
            AssetCoverageId.New(),
            timelineId,
            assetId,
            CoverageKind.Partial,
            intervals,
            TemporalAlignment.Identity);
}
