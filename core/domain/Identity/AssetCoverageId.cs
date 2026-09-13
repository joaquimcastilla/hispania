namespace Hispania.Media.Domain.Identity;

public sealed record AssetCoverageId
{
    public AssetCoverageId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new DomainValidationException("asset-coverage-id.empty", "An AssetCoverage ID cannot be empty.");
        }

        Value = value;
    }

    public Guid Value { get; }

    public static AssetCoverageId New() => new(Guid.CreateVersion7());
}
