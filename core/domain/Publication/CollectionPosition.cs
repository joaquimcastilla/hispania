namespace Hispania.Media.Domain.Publication;

public sealed record CollectionPosition
{
    public CollectionPosition(int value)
    {
        if (value <= 0)
        {
            throw new DomainValidationException("collection-position.non-positive", "A Collection position must be greater than zero.");
        }

        Value = value;
    }

    public int Value { get; }
}
