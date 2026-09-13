namespace Hispania.Media.Domain.Identity;

public sealed record ArtworkReferenceId
{
    public ArtworkReferenceId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new DomainValidationException("artwork-reference-id.empty", "An ArtworkReference ID cannot be empty.");
        }

        Value = value;
    }

    public Guid Value { get; }

    public static ArtworkReferenceId New() => new(Guid.CreateVersion7());
}
