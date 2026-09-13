using Hispania.Media.Domain.Identity;

namespace Hispania.Media.Domain.TechnicalRepresentations;

public sealed record RepresentationSourceBinding
{
    internal RepresentationSourceBinding(
        TechnicalRepresentationId technicalRepresentationId,
        MediaSourceId mediaSourceId,
        ContainerExposure? containerExposure)
    {
        TechnicalRepresentationId = technicalRepresentationId ?? throw new DomainValidationException("representation-source-binding.representation-id.required", "A TechnicalRepresentation ID is required.");
        MediaSourceId = mediaSourceId ?? throw new DomainValidationException("representation-source-binding.media-source-id.required", "A MediaSource ID is required.");
        ContainerExposure = containerExposure;
    }

    public TechnicalRepresentationId TechnicalRepresentationId { get; }

    public MediaSourceId MediaSourceId { get; }

    public ContainerExposure? ContainerExposure { get; }
}
