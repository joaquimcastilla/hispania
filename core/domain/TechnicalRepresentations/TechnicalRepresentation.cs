using Hispania.Media.Domain.Identity;
using Hispania.Media.Domain.Sources;

namespace Hispania.Media.Domain.TechnicalRepresentations;

public sealed class TechnicalRepresentation
{
    private readonly HashSet<RepresentationSourceBinding> _sourceBindings = [];

    internal TechnicalRepresentation(TechnicalRepresentationId id, AssetId assetId, TechnicalProfile profile)
    {
        Id = id ?? throw new DomainValidationException("technical-representation.id.required", "A TechnicalRepresentation ID is required.");
        AssetId = assetId ?? throw new DomainValidationException("technical-representation.asset-id.required", "An Asset ID is required.");
        Profile = profile ?? throw new DomainValidationException("technical-representation.profile.required", "A TechnicalProfile is required.");
    }

    public TechnicalRepresentationId Id { get; }

    public AssetId AssetId { get; }

    public TechnicalProfile Profile { get; }

    public IReadOnlyCollection<RepresentationSourceBinding> SourceBindings => _sourceBindings.ToArray();

    public RepresentationSourceBinding AddSourceBinding(MediaSource source, ContainerExposure? containerExposure = null)
    {
        if (source is null)
        {
            throw new DomainValidationException("representation-source-binding.source.required", "A MediaSource is required.");
        }

        var binding = new RepresentationSourceBinding(Id, source.Id, containerExposure);
        if (!_sourceBindings.Add(binding))
        {
            throw new DomainValidationException("technical-representation.source-binding.duplicate", "A TechnicalRepresentation cannot contain the same source binding twice.");
        }

        return binding;
    }
}
