namespace Hispania.Media.Domain.Identity;

public sealed record AssetId
{
    public AssetId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new DomainValidationException("asset-id.empty", "An Asset ID cannot be empty.");
        }

        Value = value;
    }

    public Guid Value { get; }

    public static AssetId New() => new(Guid.CreateVersion7());
}
