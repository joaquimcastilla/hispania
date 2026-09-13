namespace Hispania.Media.Domain.TechnicalRepresentations;

public sealed record PixelDimensions
{
    public PixelDimensions(int width, int height)
    {
        if (width <= 0)
        {
            throw new DomainValidationException("pixel-dimensions.width.invalid", "Pixel width must be positive.");
        }

        if (height <= 0)
        {
            throw new DomainValidationException("pixel-dimensions.height.invalid", "Pixel height must be positive.");
        }

        Width = width;
        Height = height;
    }

    public int Width { get; }

    public int Height { get; }
}
