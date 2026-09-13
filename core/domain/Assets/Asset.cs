using Hispania.Media.Domain.Identity;
using Hispania.Media.Domain.TechnicalRepresentations;

namespace Hispania.Media.Domain.Assets;

public sealed class Asset
{
    private readonly HashSet<AssetRole> _roles = [];
    private readonly Dictionary<TechnicalRepresentationId, TechnicalRepresentation> _technicalRepresentations = [];

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

    public IReadOnlyCollection<TechnicalRepresentation> TechnicalRepresentations => _technicalRepresentations.Values.ToArray();

    public TechnicalRepresentation AddTechnicalRepresentation(TechnicalRepresentationId id, TechnicalProfile profile)
    {
        if (id is null)
        {
            throw new DomainValidationException("technical-representation.id.required", "A TechnicalRepresentation ID is required.");
        }

        if (profile is null)
        {
            throw new DomainValidationException("technical-representation.profile.required", "A TechnicalProfile is required.");
        }

        if (profile.MediaKind != MediaKind)
        {
            throw new DomainValidationException("technical-representation.profile.media-kind.mismatch", "A TechnicalProfile must match its Asset MediaKind.");
        }

        if (_technicalRepresentations.ContainsKey(id))
        {
            throw new DomainValidationException("asset.technical-representation.duplicate", "An Asset cannot contain the same TechnicalRepresentation twice.");
        }

        var representation = new TechnicalRepresentation(id, Id, profile);
        _technicalRepresentations.Add(id, representation);
        return representation;
    }

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
