using Hispania.Media.Domain.Identity;

namespace Hispania.Media.Domain.Timelines;

public sealed class Timeline
{
    private readonly Dictionary<AssetId, AssetCoverage> _coverages = [];

    internal Timeline(TimelineId id, MediaEntityId mediaEntityId, TimelineDuration? duration)
    {
        Id = id ?? throw new DomainValidationException("timeline.id.required", "A Timeline ID is required.");
        MediaEntityId = mediaEntityId ?? throw new DomainValidationException("timeline.media-entity-id.required", "A Timeline must reference a MediaEntity.");
        Duration = duration;
    }

    public TimelineId Id { get; }

    public MediaEntityId MediaEntityId { get; }

    public TimelineDuration? Duration { get; }

    public IReadOnlyCollection<AssetCoverage> Coverages => _coverages.Values.ToArray();

    public void AddCoverage(AssetCoverage coverage)
    {
        if (coverage is null)
        {
            throw new DomainValidationException("asset-coverage.required", "An AssetCoverage is required.");
        }

        if (coverage.TimelineId != Id)
        {
            throw new DomainValidationException("asset-coverage.timeline.mismatch", "An AssetCoverage must reference its owning Timeline.");
        }

        if (_coverages.ContainsKey(coverage.AssetId))
        {
            throw new DomainValidationException("timeline.asset-coverage.duplicate", "A Timeline cannot contain more than one current coverage for the same Asset.");
        }

        coverage.ValidateFor(Duration);
        _coverages.Add(coverage.AssetId, coverage);
    }
}
