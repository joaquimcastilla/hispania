namespace Hispania.Media.Domain.Identity;

public sealed record MediaSourceId
{
    public MediaSourceId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new DomainValidationException("media-source-id.empty", "A MediaSource ID cannot be empty.");
        }

        Value = value;
    }

    public Guid Value { get; }

    public static MediaSourceId New() => new(Guid.CreateVersion7());
}
