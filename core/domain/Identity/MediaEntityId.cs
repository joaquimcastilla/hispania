namespace Hispania.Media.Domain.Identity;

public sealed record MediaEntityId
{
    public MediaEntityId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new DomainValidationException("media-entity-id.empty", "A MediaEntity ID cannot be empty.");
        }

        Value = value;
    }

    public Guid Value { get; }

    public static MediaEntityId New() => new(Guid.CreateVersion7());
}
