using Hispania.Media.Domain.Identity;

namespace Hispania.Media.Domain.MediaEntities;

public sealed class MediaEntity
{
    internal MediaEntity(MediaEntityId id, WorkId workId, MediaEntityCategory category)
    {
        Id = id ?? throw new DomainValidationException("media-entity.id.required", "A MediaEntity ID is required.");
        WorkId = workId ?? throw new DomainValidationException("media-entity.work-id.required", "A MediaEntity must reference a Work.");
        Category = category ?? throw new DomainValidationException("media-entity.category.required", "A MediaEntity category is required.");
    }

    public MediaEntityId Id { get; }

    public WorkId WorkId { get; }

    public MediaEntityCategory Category { get; }
}
