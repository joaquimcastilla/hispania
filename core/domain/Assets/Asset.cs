using Hispania.Media.Domain.Identity;

namespace Hispania.Media.Domain.Assets;

public sealed class Asset
{
    private readonly HashSet<AssetRole> _roles = [];

    public Asset(AssetId id, AssetMediaKind mediaKind, IEnumerable<AssetRole>? roles = null)
    {
        Id = id ?? throw new DomainValidationException("asset.id.required", "An Asset ID is required.");

        if (!Enum.IsDefined(mediaKind))
        {
            throw new DomainValidationException("asset.media-kind.invalid", "An Asset must use a defined MediaKind.");
        }

        MediaKind = mediaKind;

        if (roles is not null)
        {
            foreach (var role in roles)
            {
                AddRole(role);
            }
        }
    }

    public AssetId Id { get; }

    public AssetMediaKind MediaKind { get; }

    public IReadOnlyCollection<AssetRole> Roles => _roles.ToArray();

    public void AddRole(AssetRole role)
    {
        if (role is null)
        {
            throw new DomainValidationException("asset.role.required", "An Asset role is required.");
        }

        if (!IsCompatible(MediaKind, role))
        {
            throw new DomainValidationException("asset.role.incompatible", "The Asset role is not compatible with its MediaKind.");
        }

        if (!_roles.Add(role))
        {
            throw new DomainValidationException("asset.role.duplicate", "An Asset cannot contain the same role twice.");
        }
    }

    private static bool IsCompatible(AssetMediaKind mediaKind, AssetRole role) => (mediaKind, role.Code) switch
    {
        (AssetMediaKind.Video, "main") => true,
        (AssetMediaKind.Audio, "main" or "dub" or "commentary" or "audio-description") => true,
        (AssetMediaKind.Text, "subtitle" or "lyrics") => true,
        _ => false
    };
}
