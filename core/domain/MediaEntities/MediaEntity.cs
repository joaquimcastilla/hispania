using Hispania.Media.Domain.Identity;
using Hispania.Media.Domain.Timelines;

namespace Hispania.Media.Domain.MediaEntities;

public sealed class MediaEntity
{
    private readonly Dictionary<TimelineId, Timeline> _timelines = [];

    internal MediaEntity(MediaEntityId id, WorkId workId, MediaEntityCategory category)
    {
        Id = id ?? throw new DomainValidationException("media-entity.id.required", "A MediaEntity ID is required.");
        WorkId = workId ?? throw new DomainValidationException("media-entity.work-id.required", "A MediaEntity must reference a Work.");
        Category = category ?? throw new DomainValidationException("media-entity.category.required", "A MediaEntity category is required.");
    }

    public MediaEntityId Id { get; }

    public WorkId WorkId { get; }

    public MediaEntityCategory Category { get; }

    public IReadOnlyCollection<Timeline> Timelines => _timelines.Values.ToArray();

    public Timeline AddTimeline(TimelineId id, TimelineDuration? duration = null)
    {
        if (id is null)
        {
            throw new DomainValidationException("timeline.id.required", "A Timeline ID is required.");
        }

        if (_timelines.ContainsKey(id))
        {
            throw new DomainValidationException("media-entity.timeline.duplicate", "A MediaEntity cannot contain the same Timeline twice.");
        }

        var timeline = new Timeline(id, Id, duration);
        _timelines.Add(id, timeline);
        return timeline;
    }
}
