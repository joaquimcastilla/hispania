using Hispania.Media.Domain.Identity;
using Hispania.Media.Domain.MediaEntities;

namespace Hispania.Media.Domain.Works;

public sealed class Work
{
    private readonly Dictionary<MediaEntityId, MediaEntity> _mediaEntities = [];

    public Work(WorkId id, WorkCategory category)
    {
        Id = id ?? throw new DomainValidationException("work.id.required", "A Work ID is required.");
        Category = category ?? throw new DomainValidationException("work.category.required", "A Work category is required.");
    }

    public WorkId Id { get; }

    public WorkCategory Category { get; }

    public IReadOnlyCollection<MediaEntity> MediaEntities => _mediaEntities.Values.ToArray();

    public MediaEntity AddMediaEntity(MediaEntityId id, MediaEntityCategory category)
    {
        if (id is null)
        {
            throw new DomainValidationException("media-entity.id.required", "A MediaEntity ID is required.");
        }

        if (_mediaEntities.ContainsKey(id))
        {
            throw new DomainValidationException("work.media-entity.duplicate", "A Work cannot contain the same MediaEntity twice.");
        }

        var mediaEntity = new MediaEntity(id, Id, category);
        _mediaEntities.Add(id, mediaEntity);
        return mediaEntity;
    }
}
