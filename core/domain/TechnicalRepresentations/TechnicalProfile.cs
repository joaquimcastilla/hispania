using Hispania.Media.Domain.Assets;

namespace Hispania.Media.Domain.TechnicalRepresentations;

public abstract record TechnicalProfile
{
    public abstract AssetMediaKind MediaKind { get; }
}
