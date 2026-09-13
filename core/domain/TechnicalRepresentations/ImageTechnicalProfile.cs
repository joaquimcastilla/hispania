using Hispania.Media.Domain.Assets;

namespace Hispania.Media.Domain.TechnicalRepresentations;

public sealed record ImageTechnicalProfile : TechnicalProfile
{
    public ImageTechnicalProfile(PixelDimensions dimensions, FormatIdentifier format)
    {
        Dimensions = dimensions ?? throw new DomainValidationException("image-technical-profile.dimensions.required", "Image dimensions are required.");
        Format = format ?? throw new DomainValidationException("image-technical-profile.format.required", "An image format is required.");
    }

    public override AssetMediaKind MediaKind => AssetMediaKind.Image;

    public PixelDimensions Dimensions { get; }

    public FormatIdentifier Format { get; }
}
