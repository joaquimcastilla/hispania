using Hispania.Media.Domain.Identity;

namespace Hispania.Media.Domain.Enrichment;

public sealed class ArtworkReference
{
    public ArtworkReference(
        ArtworkReferenceId id,
        EnrichmentTarget target,
        ArtworkRole role,
        ArtworkLocator locator,
        MetadataProvenance provenance)
    {
        Id = id ?? throw new DomainValidationException("artwork-reference.id.required", "An ArtworkReference ID is required.");
        Target = target ?? throw new DomainValidationException("artwork-reference.target.required", "An ArtworkReference target is required.");
        if (target.Kind == EnrichmentTargetKind.ArtworkReference)
        {
            throw new DomainValidationException("artwork-reference.target.artwork-reference.invalid", "An ArtworkReference cannot target another ArtworkReference.");
        }

        Role = role ?? throw new DomainValidationException("artwork-reference.role.required", "An ArtworkReference role is required.");
        Locator = locator ?? throw new DomainValidationException("artwork-reference.locator.required", "An ArtworkReference locator is required.");
        Provenance = provenance ?? throw new DomainValidationException("artwork-reference.provenance.required", "An ArtworkReference provenance is required.");
    }

    public ArtworkReferenceId Id { get; }

    public EnrichmentTarget Target { get; }

    public ArtworkRole Role { get; }

    public ArtworkLocator Locator { get; }

    public MetadataProvenance Provenance { get; }
}
