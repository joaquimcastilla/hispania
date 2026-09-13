using Hispania.Media.Domain.Identity;

namespace Hispania.Media.Domain.Sources;

public sealed class MediaSource
{
    public MediaSource(MediaSourceId id, SourceLocator locator)
    {
        Id = id ?? throw new DomainValidationException("media-source.id.required", "A MediaSource ID is required.");
        Locator = locator ?? throw new DomainValidationException("media-source.locator.required", "A SourceLocator is required.");
    }

    public MediaSourceId Id { get; }

    public SourceLocator Locator { get; }
}
